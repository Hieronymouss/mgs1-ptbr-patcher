using System.Text;
using Mgs1.Patcher.Core;

namespace Mgs1.Patcher.Core.Tests;

internal static class Program
{
    private static readonly PatchApplyOptions TestOptions = new()
    {
        FreeSpaceReserveBytes = 0,
        IoBufferSize = 4096,
    };

    private static async Task<int> Main()
    {
        (string Name, Func<Task> Run)[] tests =
        [
            ("tracked manifest parses", TrackedManifestParsesAsync),
            ("reference-vector parity and input preservation", ReferenceVectorParityAsync),
            ("all four BPS action modes", AllBpsActionModesAsync),
            ("wrong same-size input rejected before output", WrongSameSizeInputAsync),
            ("wrong disc rejected", WrongDiscAsync),
            ("wrong patch profile rejected", WrongProfileAsync),
            ("corrupt payload patch CRC rejected", CorruptPayloadAsync),
            ("target CRC mismatch rejected", TargetCrcMismatchAsync),
            ("forced output mismatch cleans partial pair", ForcedOutputMismatchAsync),
            ("existing output is preserved", ExistingOutputAsync),
            ("output-pair disk preflight rejects insufficient space", DiskPreflightAsync),
            ("manifest traversal and absolute paths rejected", UnsafeManifestPathsAsync),
            ("cancellation cleans partial output", CancellationAsync),
            ("progress interruption cleans partial output", ProgressInterruptionAsync),
        ];

        int failures = 0;
        foreach ((string name, Func<Task> run) in tests)
        {
            try
            {
                await run().ConfigureAwait(false);
                Console.WriteLine($"PASS {name}");
            }
            catch (Exception exception)
            {
                failures++;
                Console.Error.WriteLine($"FAIL {name}");
                Console.Error.WriteLine(exception);
            }
        }

        Console.WriteLine($"{tests.Length - failures}/{tests.Length} focused .NET tests passed.");
        return failures == 0 ? 0 : 1;
    }

    private static async Task TrackedManifestParsesAsync()
    {
        string manifestPath = Path.Combine(
            TestFixture.RepositoryRoot(),
            "release",
            "release-manifest.json");
        ReleaseManifest manifest = await ManifestLoader.LoadAsync(manifestPath).ConfigureAwait(false);
        TestAssert.Equal(2, manifest.Discs.Count, "Tracked manifest disc count differs.");
        TestAssert.True(manifest.Discs.ContainsKey("disc1"), "Tracked manifest lacks disc1.");
        TestAssert.True(manifest.Discs.ContainsKey("disc2"), "Tracked manifest lacks disc2.");
    }

    private static async Task ReferenceVectorParityAsync()
    {
        using TestFixture fixture = TestFixture.Create();
        TestAssert.Equal(
            "92cfbfe89ab69d1560fa3f7f5669ebe57ae54a5a8c82b8973b0bea8614ae7813",
            TestFixture.Sha256(fixture.BinPatchPath),
            "Reference patch identity differs.");
        BpsPatchInfo info = await BpsPatchReader.InspectAsync(fixture.BinPatchPath).ConfigureAwait(false);
        TestAssert.Equal(64L, info.SourceSize, "Reference source size differs.");
        TestAssert.Equal(69L, info.TargetSize, "Reference target size differs.");
        TestAssert.Equal(0x056acb21U, info.SourceCrc32, "Reference source CRC differs.");
        TestAssert.Equal(0x721b8f5cU, info.TargetCrc32, "Reference target CRC differs.");

        byte[] sourceBinBefore = File.ReadAllBytes(fixture.SourceBinPath);
        byte[] sourceCueBefore = File.ReadAllBytes(fixture.SourceCuePath);
        DateTime binWriteBefore = File.GetLastWriteTimeUtc(fixture.SourceBinPath);
        DateTime cueWriteBefore = File.GetLastWriteTimeUtc(fixture.SourceCuePath);
        BundleApplyResult result = await ApplyAsync(fixture).ConfigureAwait(false);

        TestAssert.Equal("verified", result.Status, "Bundle status differs.");
        TestAssert.True(result.InputsPreserved, "Bundle did not report preserved inputs.");
        TestAssert.SequenceEqual(
            File.ReadAllBytes(fixture.TargetBinPath),
            File.ReadAllBytes(Path.Combine(fixture.OutputDirectory, "accepted.bin")),
            "BIN output differs from the reference target.");
        TestAssert.SequenceEqual(
            File.ReadAllBytes(fixture.TargetCuePath),
            File.ReadAllBytes(Path.Combine(fixture.OutputDirectory, "accepted.cue")),
            "CUE output differs from expected target.");
        if (OperatingSystem.IsWindows())
        {
            TestAssert.False(
                (File.GetAttributes(Path.Combine(fixture.OutputDirectory, "accepted.bin")) & FileAttributes.Hidden) != 0,
                "Published BIN retained the partial-file hidden attribute.");
            TestAssert.False(
                (File.GetAttributes(Path.Combine(fixture.OutputDirectory, "accepted.cue")) & FileAttributes.Hidden) != 0,
                "Published CUE retained the partial-file hidden attribute.");
        }

        TestAssert.SequenceEqual(sourceBinBefore, File.ReadAllBytes(fixture.SourceBinPath), "Clean BIN changed.");
        TestAssert.SequenceEqual(sourceCueBefore, File.ReadAllBytes(fixture.SourceCuePath), "Clean CUE changed.");
        TestAssert.Equal(binWriteBefore, File.GetLastWriteTimeUtc(fixture.SourceBinPath), "Clean BIN timestamp changed.");
        TestAssert.Equal(cueWriteBefore, File.GetLastWriteTimeUtc(fixture.SourceCuePath), "Clean CUE timestamp changed.");
    }

    private static async Task AllBpsActionModesAsync()
    {
        using TestFixture fixture = TestFixture.Create();
        byte[] source = Encoding.ASCII.GetBytes("abcdefghij");
        byte[] patch = SyntheticBpsBuilder.CreateAllModes(source, out byte[] target);
        fixture.ReplaceBinFixture(source, target, patch);
        BundleApplyResult result = await ApplyAsync(fixture).ConfigureAwait(false);
        BpsActionCounts actions = result.Outputs["bin"].Bps.Actions;
        TestAssert.Equal(1L, actions.SourceReadActions, "SourceRead action count differs.");
        TestAssert.Equal(1L, actions.TargetReadActions, "TargetRead action count differs.");
        TestAssert.Equal(1L, actions.SourceCopyActions, "SourceCopy action count differs.");
        TestAssert.Equal(1L, actions.TargetCopyActions, "TargetCopy action count differs.");
        TestAssert.SequenceEqual(
            target,
            File.ReadAllBytes(Path.Combine(fixture.OutputDirectory, "accepted.bin")),
            "All-mode BPS output differs.");
    }

    private static async Task WrongSameSizeInputAsync()
    {
        using TestFixture fixture = TestFixture.Create();
        byte[] wrong = File.ReadAllBytes(fixture.SourceBinPath);
        wrong[^1] ^= 1;
        string wrongPath = Path.Combine(fixture.Root, "wrong.bin");
        File.WriteAllBytes(wrongPath, wrong);
        ReleaseManifest manifest = await ManifestLoader.LoadAsync(fixture.ManifestPath).ConfigureAwait(false);
        var request = Request(fixture) with { SourceBinPath = wrongPath };
        await TestAssert.ThrowsAsync<PatcherIntegrityException>(
            () => PatchBundleApplier.ApplyAsync(manifest, request, TestOptions),
            "SHA-256 mismatch").ConfigureAwait(false);
        TestAssert.False(Directory.Exists(fixture.OutputDirectory), "Wrong input created an output directory.");
    }

    private static async Task WrongDiscAsync()
    {
        using TestFixture fixture = TestFixture.Create();
        ReleaseManifest manifest = await ManifestLoader.LoadAsync(fixture.ManifestPath).ConfigureAwait(false);
        await TestAssert.ThrowsAsync<PatcherManifestException>(
            () => PatchBundleApplier.ApplyAsync(
                manifest,
                Request(fixture) with { DiscId = "disc2" },
                TestOptions),
            "unknown disc").ConfigureAwait(false);
        TestAssert.False(Directory.Exists(fixture.OutputDirectory), "Wrong disc created an output directory.");
    }

    private static async Task WrongProfileAsync()
    {
        using TestFixture fixture = TestFixture.Create();
        fixture.WriteManifest(profile: "different-profile");
        await TestAssert.ThrowsAsync<PatcherManifestException>(
            () => ManifestLoader.LoadAsync(fixture.ManifestPath),
            "linear-streaming-v1").ConfigureAwait(false);
    }

    private static async Task CorruptPayloadAsync()
    {
        using TestFixture fixture = TestFixture.Create();
        byte[] patch = File.ReadAllBytes(fixture.BinPatchPath);
        patch[patch.Length / 2] ^= 1;
        File.WriteAllBytes(fixture.BinPatchPath, patch);
        fixture.WriteManifest();
        await TestAssert.ThrowsAsync<PatcherIntegrityException>(
            () => ApplyAsync(fixture),
            "patch CRC32 mismatch").ConfigureAwait(false);
        TestAssert.False(Directory.Exists(fixture.OutputDirectory), "Corrupt patch created an output directory.");
    }

    private static async Task TargetCrcMismatchAsync()
    {
        using TestFixture fixture = TestFixture.Create();
        byte[] changed = SyntheticBpsBuilder.WithInvalidTargetCrc(
            File.ReadAllBytes(fixture.BinPatchPath));
        File.WriteAllBytes(fixture.BinPatchPath, changed);
        fixture.WriteManifest();
        await TestAssert.ThrowsAsync<PatcherIntegrityException>(
            () => ApplyAsync(fixture),
            "target CRC32 mismatch").ConfigureAwait(false);
        AssertDirectoryEmpty(fixture.OutputDirectory, "Target CRC failure left an owned partial.");
    }

    private static async Task ForcedOutputMismatchAsync()
    {
        using TestFixture fixture = TestFixture.Create();
        fixture.WriteManifest(targetCueSha256: new string('0', 64));
        await TestAssert.ThrowsAsync<PatcherIntegrityException>(
            () => ApplyAsync(fixture),
            "output SHA-256 mismatch").ConfigureAwait(false);
        AssertDirectoryEmpty(fixture.OutputDirectory, "Forced output mismatch left a partial or published output.");
    }

    private static async Task ExistingOutputAsync()
    {
        using TestFixture fixture = TestFixture.Create();
        Directory.CreateDirectory(fixture.OutputDirectory);
        string existing = Path.Combine(fixture.OutputDirectory, "accepted.bin");
        byte[] sentinel = "do-not-replace"u8.ToArray();
        File.WriteAllBytes(existing, sentinel);
        await TestAssert.ThrowsAsync<PatcherSafetyException>(
            () => ApplyAsync(fixture),
            "refusing to overwrite").ConfigureAwait(false);
        TestAssert.SequenceEqual(sentinel, File.ReadAllBytes(existing), "Existing output was changed.");
        TestAssert.Equal(1, Directory.EnumerateFileSystemEntries(fixture.OutputDirectory).Count(), "Unexpected partial was created.");
    }

    private static async Task DiskPreflightAsync()
    {
        using TestFixture fixture = TestFixture.Create();
        string? volumeRoot = Path.GetPathRoot(fixture.Root);
        TestAssert.True(!string.IsNullOrEmpty(volumeRoot), "Test volume root is unavailable.");
        long available = new DriveInfo(volumeRoot!).AvailableFreeSpace;
        var options = TestOptions with { FreeSpaceReserveBytes = checked(available + 1) };
        ReleaseManifest manifest = await ManifestLoader.LoadAsync(fixture.ManifestPath).ConfigureAwait(false);
        await TestAssert.ThrowsAsync<PatcherSafetyException>(
            () => PatchBundleApplier.ApplyAsync(manifest, Request(fixture), options),
            "insufficient free space").ConfigureAwait(false);
        TestAssert.False(Directory.Exists(fixture.OutputDirectory), "Disk preflight created an output directory.");
    }

    private static async Task UnsafeManifestPathsAsync()
    {
        using TestFixture fixture = TestFixture.Create();
        fixture.WriteManifest(binPatchManifestPath: "../escape.bps");
        await TestAssert.ThrowsAsync<PatcherManifestException>(
            () => ManifestLoader.LoadAsync(fixture.ManifestPath),
            "safe relative").ConfigureAwait(false);

        fixture.WriteManifest(binPatchManifestPath: "C" + ":/escape.bps");
        await TestAssert.ThrowsAsync<PatcherManifestException>(
            () => ManifestLoader.LoadAsync(fixture.ManifestPath),
            "Windows-safe").ConfigureAwait(false);
    }

    private static async Task CancellationAsync()
    {
        using TestFixture fixture = TestFixture.Create();
        using var cancellation = new CancellationTokenSource();
        var options = TestOptions with
        {
            Progress = new InlineProgress<PatchProgress>(progress =>
            {
                if (progress.Phase == PatchProgressPhase.ApplyingBin && progress.CompletedBytes > 0)
                {
                    cancellation.Cancel();
                }
            }),
        };
        ReleaseManifest manifest = await ManifestLoader.LoadAsync(fixture.ManifestPath).ConfigureAwait(false);
        await TestAssert.ThrowsAsync<OperationCanceledException>(
            () => PatchBundleApplier.ApplyAsync(manifest, Request(fixture), options, cancellation.Token),
            "canceled").ConfigureAwait(false);
        AssertDirectoryEmpty(fixture.OutputDirectory, "Cancellation left a partial or published output.");
    }

    private static async Task ProgressInterruptionAsync()
    {
        using TestFixture fixture = TestFixture.Create();
        var options = TestOptions with
        {
            Progress = new InlineProgress<PatchProgress>(progress =>
            {
                if (progress.Phase == PatchProgressPhase.ApplyingBin && progress.CompletedBytes > 0)
                {
                    throw new SimulatedInterruptionException("simulated interruption");
                }
            }),
        };
        ReleaseManifest manifest = await ManifestLoader.LoadAsync(fixture.ManifestPath).ConfigureAwait(false);
        await TestAssert.ThrowsAsync<SimulatedInterruptionException>(
            () => PatchBundleApplier.ApplyAsync(manifest, Request(fixture), options),
            "simulated interruption").ConfigureAwait(false);
        AssertDirectoryEmpty(fixture.OutputDirectory, "Progress interruption left a partial or published output.");
    }

    private static async Task<BundleApplyResult> ApplyAsync(TestFixture fixture)
    {
        ReleaseManifest manifest = await ManifestLoader.LoadAsync(fixture.ManifestPath).ConfigureAwait(false);
        return await PatchBundleApplier.ApplyAsync(manifest, Request(fixture), TestOptions).ConfigureAwait(false);
    }

    private static BundleApplyRequest Request(TestFixture fixture) => new(
        "disc1",
        fixture.SourceBinPath,
        fixture.SourceCuePath,
        fixture.PatchRoot,
        fixture.OutputDirectory);

    private static void AssertDirectoryEmpty(string path, string message)
    {
        TestAssert.True(Directory.Exists(path), "Expected output directory to exist after apply began.");
        TestAssert.False(Directory.EnumerateFileSystemEntries(path).Any(), message);
    }

    private sealed class SimulatedInterruptionException(string message) : Exception(message);
}
