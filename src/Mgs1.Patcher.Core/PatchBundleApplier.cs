using System.Collections.ObjectModel;
using System.Runtime.ExceptionServices;
using System.Text;

namespace Mgs1.Patcher.Core;

public sealed record BundleApplyRequest(
    string DiscId,
    string SourceBinPath,
    string SourceCuePath,
    string PatchRootPath,
    string OutputDirectoryPath,
    string? OutputBinFileName = null,
    string? OutputCueFileName = null);

public sealed record VerifiedPatch(PatchSpec Spec, FileDigest Digest, BpsPatchInfo Bps);

public sealed record VerifiedOutput(
    ArtifactSpec Spec,
    string Path,
    FileDigest PublishedDigest,
    BpsApplyResult Bps);

public sealed record BundleApplyResult(
    string ReleaseId,
    string DiscId,
    string Status,
    IReadOnlyDictionary<string, FileDigest> Source,
    IReadOnlyDictionary<string, FileDigest> SourceAfter,
    IReadOnlyDictionary<string, VerifiedPatch> Patches,
    IReadOnlyDictionary<string, VerifiedOutput> Outputs,
    bool InputsPreserved,
    long TemporaryDiskPeakBytes,
    long FreeSpaceBeforeBytes,
    long FreeSpaceAfterBytes);

public static class PatchBundleApplier
{
    public static async Task<BundleApplyResult> ApplyAsync(
        ReleaseManifest manifest,
        BundleApplyRequest request,
        PatchApplyOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(request);
        options ??= new PatchApplyOptions();
        options.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        if (!manifest.Discs.TryGetValue(request.DiscId, out DiscSpec? disc))
        {
            throw new PatcherManifestException($"Unknown disc id: {request.DiscId}");
        }

        var sourcePaths = new Dictionary<FileKind, string>
        {
            [FileKind.Bin] = NormalizePath(request.SourceBinPath, "clean BIN"),
            [FileKind.Cue] = NormalizePath(request.SourceCuePath, "clean CUE"),
        };
        if (PathsEqual(sourcePaths[FileKind.Bin], sourcePaths[FileKind.Cue]))
        {
            throw new PatcherSafetyException("Clean BIN and CUE paths must be distinct.");
        }

        var beforeFingerprints = new Dictionary<FileKind, FileFingerprint>();
        var sourceDigests = new Dictionary<FileKind, FileDigest>();
        foreach (FileKind kind in FileKindExtensions.All)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArtifactSpec expected = disc.Source.Get(kind);
            string label = $"Clean {kind.Label()}";
            beforeFingerprints[kind] = FileIntegrity.Fingerprint(sourcePaths[kind], label);
            sourceDigests[kind] = await FileIntegrity.VerifyAsync(
                sourcePaths[kind],
                expected.Size,
                expected.Sha256,
                label,
                options.IoBufferSize,
                (completed, total) => Report(
                    options.Progress,
                    PatchProgressPhase.ValidatingInputs,
                    expected.FileName,
                    completed,
                    total,
                    cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }

        string patchRoot = NormalizePath(request.PatchRootPath, "patch root");
        if (!Directory.Exists(patchRoot))
        {
            throw new PatcherIntegrityException($"Patch root is not a readable directory: {patchRoot}");
        }

        var patchPaths = new Dictionary<FileKind, string>();
        var verifiedPatches = new Dictionary<FileKind, VerifiedPatch>();
        foreach (FileKind kind in FileKindExtensions.All)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PatchSpec expectedPatch = disc.Patches.Get(kind);
            string patchPath = ResolvePatchPath(patchRoot, expectedPatch.File);
            patchPaths[kind] = patchPath;
            FileDigest digest = await FileIntegrity.VerifyAsync(
                patchPath,
                expectedPatch.Size,
                expectedPatch.Sha256,
                $"{kind.Label()} patch",
                options.IoBufferSize,
                (completed, total) => Report(
                    options.Progress,
                    PatchProgressPhase.ValidatingPatches,
                    expectedPatch.File,
                    completed,
                    total,
                    cancellationToken),
                cancellationToken).ConfigureAwait(false);
            BpsPatchInfo info = await BpsPatchReader.InspectAsync(patchPath, cancellationToken)
                .ConfigureAwait(false);
            if (info.SourceSize != disc.Source.Get(kind).Size)
            {
                throw new PatcherIntegrityException(
                    $"{kind.Label()} patch source size does not match the manifest.");
            }

            if (info.TargetSize != disc.Target.Get(kind).Size)
            {
                throw new PatcherIntegrityException(
                    $"{kind.Label()} patch target size does not match the manifest.");
            }

            if (info.SourceCrc32 != sourceDigests[kind].Crc32)
            {
                throw new PatcherIntegrityException(
                    $"{kind.Label()} patch source CRC32 does not match the selected input.");
            }

            verifiedPatches[kind] = new VerifiedPatch(expectedPatch, digest, info);
        }

        string outputDirectory = NormalizePath(request.OutputDirectoryPath, "output directory");
        if (File.Exists(outputDirectory))
        {
            throw new PatcherSafetyException($"Output directory path is an existing file: {outputDirectory}");
        }

        var outputFileNames = new Dictionary<FileKind, string>
        {
            [FileKind.Bin] = ResolveOutputFileName(
                request.OutputBinFileName,
                disc.Target.Bin.FileName,
                ".bin",
                "BIN"),
            [FileKind.Cue] = ResolveOutputFileName(
                request.OutputCueFileName,
                disc.Target.Cue.FileName,
                ".cue",
                "CUE"),
        };
        var outputPaths = new Dictionary<FileKind, string>
        {
            [FileKind.Bin] = Path.Combine(outputDirectory, outputFileNames[FileKind.Bin]),
            [FileKind.Cue] = Path.Combine(outputDirectory, outputFileNames[FileKind.Cue]),
        };
        var protectedPaths = sourcePaths.Values.Concat(patchPaths.Values).ToArray();
        foreach (FileKind kind in FileKindExtensions.All)
        {
            string outputPath = outputPaths[kind];
            if (protectedPaths.Any(path => PathsEqual(path, outputPath)))
            {
                throw new PatcherSafetyException(
                    $"{kind.Label()} output path aliases a clean input or patch payload.");
            }

            if (File.Exists(outputPath) || Directory.Exists(outputPath))
            {
                throw new PatcherSafetyException($"Refusing to overwrite output: {outputPath}");
            }
        }

        if (PathsEqual(outputPaths[FileKind.Bin], outputPaths[FileKind.Cue]))
        {
            throw new PatcherSafetyException("BIN and CUE output paths must be distinct.");
        }

        string existingParent = FindExistingParent(outputDirectory);
        RejectReparsePointsToRoot(existingParent, "output path");
        long temporaryDiskPeak;
        long requiredFreeSpace;
        try
        {
            int cueNameGrowth = Encoding.UTF8.GetByteCount(outputFileNames[FileKind.Bin])
                - Encoding.UTF8.GetByteCount(disc.Target.Bin.FileName);
            long publishedCueSize = checked(disc.Target.Cue.Size + Math.Max(0, cueNameGrowth));
            temporaryDiskPeak = checked(disc.Target.Bin.Size + publishedCueSize);
            requiredFreeSpace = checked(temporaryDiskPeak + options.FreeSpaceReserveBytes);
        }
        catch (OverflowException exception)
        {
            throw new PatcherSafetyException("Output-pair disk-space requirement exceeds 64-bit limits.", exception);
        }

        long freeSpaceBefore = GetAvailableFreeSpace(existingParent);
        if (freeSpaceBefore < requiredFreeSpace)
        {
            throw new PatcherSafetyException(
                $"Insufficient free space: need {requiredFreeSpace} bytes for output pair plus reserve, " +
                $"have {freeSpaceBefore}.");
        }

        Report(
            options.Progress,
            PatchProgressPhase.Preflight,
            "output pair",
            freeSpaceBefore,
            requiredFreeSpace,
            cancellationToken);
        try
        {
            Directory.CreateDirectory(outputDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new PatcherSafetyException(
                $"Cannot create output directory {outputDirectory}: {exception.Message}",
                exception);
        }

        var partialPaths = new Dictionary<FileKind, string>();
        foreach (FileKind kind in FileKindExtensions.All)
        {
            partialPaths[kind] = Path.Combine(
                outputDirectory,
                $".{outputFileNames[kind]}.partial-{Guid.NewGuid():N}");
        }

        var applyResults = new Dictionary<FileKind, BpsApplyResult>();
        var createdPartials = new List<string>();
        var published = new List<string>();
        try
        {
            foreach (FileKind kind in FileKindExtensions.All)
            {
                PatchProgressPhase phase = kind == FileKind.Bin
                    ? PatchProgressPhase.ApplyingBin
                    : PatchProgressPhase.ApplyingCue;
                applyResults[kind] = await BpsPatchApplier.ApplyAsync(
                    sourcePaths[kind],
                    patchPaths[kind],
                    partialPaths[kind],
                    sourceDigests[kind],
                    disc.Target.Get(kind),
                    verifiedPatches[kind].Bps,
                    options.IoBufferSize,
                    phase,
                    options.Progress,
                    cancellationToken).ConfigureAwait(false);
                createdPartials.Add(partialPaths[kind]);
            }

            var sourceAfter = new Dictionary<FileKind, FileDigest>();
            bool inputsPreserved = true;
            foreach (FileKind kind in FileKindExtensions.All)
            {
                ArtifactSpec expected = disc.Source.Get(kind);
                sourceAfter[kind] = await FileIntegrity.VerifyAsync(
                    sourcePaths[kind],
                    expected.Size,
                    expected.Sha256,
                    $"Clean {kind.Label()} after application",
                    options.IoBufferSize,
                    (completed, total) => Report(
                        options.Progress,
                        PatchProgressPhase.ReverifyingInputs,
                        expected.FileName,
                        completed,
                        total,
                        cancellationToken),
                    cancellationToken).ConfigureAwait(false);
                FileFingerprint afterFingerprint = FileIntegrity.Fingerprint(
                    sourcePaths[kind],
                    $"Clean {kind.Label()} after application");
                inputsPreserved &= sourceDigests[kind] == sourceAfter[kind]
                    && beforeFingerprints[kind] == afterFingerprint;
            }

            if (!inputsPreserved)
            {
                throw new PatcherIntegrityException(
                    "A selected clean input changed during patch application.");
            }

            foreach (FileKind kind in FileKindExtensions.All)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ClearHiddenAttribute(partialPaths[kind]);
            }

            await CueSheetPublisher.RewriteCompanionNameAsync(
                partialPaths[FileKind.Cue],
                disc.Target.Bin.FileName,
                outputFileNames[FileKind.Bin],
                cancellationToken).ConfigureAwait(false);

            var publishedDigests = new Dictionary<FileKind, FileDigest>
            {
                [FileKind.Bin] = ToDigest(applyResults[FileKind.Bin]),
                [FileKind.Cue] = string.Equals(
                    outputFileNames[FileKind.Bin],
                    disc.Target.Bin.FileName,
                    StringComparison.Ordinal)
                    ? ToDigest(applyResults[FileKind.Cue])
                    : await FileIntegrity.DigestAsync(
                        partialPaths[FileKind.Cue],
                        options.IoBufferSize,
                        report: null,
                        cancellationToken).ConfigureAwait(false),
            };

            foreach (FileKind kind in FileKindExtensions.All)
            {
                Report(
                    options.Progress,
                    PatchProgressPhase.Publishing,
                    disc.Target.Get(kind).FileName,
                    published.Count,
                    FileKindExtensions.All.Length,
                    cancellationToken);
                File.Move(partialPaths[kind], outputPaths[kind], overwrite: false);
                published.Add(outputPaths[kind]);
            }

            long freeSpaceAfter = GetAvailableFreeSpace(outputDirectory);
            Report(
                options.Progress,
                PatchProgressPhase.Completed,
                disc.Id,
                temporaryDiskPeak,
                temporaryDiskPeak,
                cancellationToken);
            return new BundleApplyResult(
                manifest.ReleaseId,
                disc.Id,
                "verified",
                ToNamedReadOnly(sourceDigests),
                ToNamedReadOnly(sourceAfter),
                ToNamedReadOnly(verifiedPatches),
                ToNamedReadOnly(new Dictionary<FileKind, VerifiedOutput>
                {
                    [FileKind.Bin] = new VerifiedOutput(
                        disc.Target.Bin,
                        outputPaths[FileKind.Bin],
                        publishedDigests[FileKind.Bin],
                        applyResults[FileKind.Bin]),
                    [FileKind.Cue] = new VerifiedOutput(
                        disc.Target.Cue,
                        outputPaths[FileKind.Cue],
                        publishedDigests[FileKind.Cue],
                        applyResults[FileKind.Cue]),
                }),
                inputsPreserved,
                temporaryDiskPeak,
                freeSpaceBefore,
                freeSpaceAfter);
        }
        catch (Exception failure)
        {
            var cleanupFailures = new List<Exception>();
            foreach (string path in createdPartials.Concat(published))
            {
                Exception? cleanupFailure = TryDelete(path);
                if (cleanupFailure is not null)
                {
                    cleanupFailures.Add(cleanupFailure);
                }
            }

            if (cleanupFailures.Count > 0)
            {
                cleanupFailures.Insert(0, failure);
                throw new PatcherSafetyException(
                    "Bundle application failed and one or more owned outputs could not be cleaned up.",
                    new AggregateException(cleanupFailures));
            }

            ExceptionDispatchInfo.Capture(failure).Throw();
            throw;
        }
    }

    private static FileDigest ToDigest(BpsApplyResult result) =>
        new(result.OutputSize, result.OutputSha256, result.OutputCrc32);

    private static string ResolveOutputFileName(
        string? requested,
        string manifestDefault,
        string expectedExtension,
        string label)
    {
        string fileName = string.IsNullOrWhiteSpace(requested) ? manifestDefault : requested;
        bool unsafeCharacter = fileName.Any(character =>
            char.IsControl(character)
            || character is '<' or '>' or ':' or '"' or '/' or '|' or '?' or '*'
            || character == (char)92);
        if (fileName.Length is <= 0 or > 255
            || fileName is "." or ".."
            || unsafeCharacter
            || fileName.EndsWith(' ')
            || fileName.EndsWith('.')
            || !string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal)
            || Path.IsPathRooted(fileName)
            || !fileName.EndsWith(expectedExtension, StringComparison.OrdinalIgnoreCase)
            || IsReservedWindowsName(fileName))
        {
            throw new PatcherSafetyException($"Unsafe {label} output file name: {fileName}");
        }

        return fileName;
    }

    private static bool IsReservedWindowsName(string fileName)
    {
        string stem = Path.GetFileNameWithoutExtension(fileName).ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL")
        {
            return true;
        }

        return stem.Length == 4
            && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
            && stem[3] is >= '1' and <= '9';
    }

    private static string NormalizePath(string path, string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new PatcherSafetyException($"Invalid {label} path: {path}", exception);
        }
    }

    private static string ResolvePatchPath(string patchRoot, string relative)
    {
        string current = patchRoot;
        foreach (string part in relative.Split('/'))
        {
            current = Path.Combine(current, part);
            if ((Directory.Exists(current) || File.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new PatcherSafetyException(
                    $"Patch path contains a reparse point and is not allowed: {relative}");
            }
        }

        string candidate = Path.GetFullPath(current);
        string rootPrefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(patchRoot))
            + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new PatcherManifestException($"Patch path escapes patch root: {relative}");
        }

        return candidate;
    }

    private static string FindExistingParent(string outputDirectory)
    {
        string? current = outputDirectory;
        while (!string.IsNullOrEmpty(current) && !Directory.Exists(current))
        {
            current = Directory.GetParent(current)?.FullName;
        }

        if (string.IsNullOrEmpty(current))
        {
            throw new PatcherSafetyException(
                "Cannot find an existing parent for the output directory.");
        }

        return current;
    }

    private static long GetAvailableFreeSpace(string path)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root))
            {
                throw new PatcherSafetyException($"Cannot determine output volume for {path}");
            }

            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch (PatcherSafetyException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new PatcherSafetyException(
                $"Cannot determine available output space for {path}: {exception.Message}",
                exception);
        }
    }

    private static void RejectReparsePointsToRoot(string path, string label)
    {
        DirectoryInfo? current = new(path);
        while (current is not null)
        {
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new PatcherSafetyException(
                    $"{label} contains a reparse point and cannot be preflighted safely: {current.FullName}");
            }

            current = current.Parent;
        }
    }

    private static void ClearHiddenAttribute(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.Hidden);
        }
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyDictionary<string, T> ToNamedReadOnly<T>(Dictionary<FileKind, T> source) =>
        new ReadOnlyDictionary<string, T>(new Dictionary<string, T>(StringComparer.Ordinal)
        {
            ["bin"] = source[FileKind.Bin],
            ["cue"] = source[FileKind.Cue],
        });

    private static void Report(
        IProgress<PatchProgress>? progress,
        PatchProgressPhase phase,
        string item,
        long completed,
        long total,
        CancellationToken cancellationToken)
    {
        progress?.Report(new PatchProgress(phase, item, completed, total));
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static Exception? TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return exception;
        }
    }
}
