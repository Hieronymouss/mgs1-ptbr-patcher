using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Mgs1.Patcher.Core.Tests;

internal sealed class TestFixture : IDisposable
{
    // Independently generated BPS reference vector for the deterministic
    // 64-byte source and 69-byte target constructed below.
    private const string ReferencePatchBase64 =
        "QlBTMcDFsjxtZ3MxLXB0YnItcGF0Y2hlciBwcm9maWxlPSJsaW5lYXItc3RyZWFtaW5nLXYxIi8+uQMUJTZHWGlwdC1iciEhIUCAkS10YWlsIctqBVyPG3KjaiLn";

    private TestFixture(string root)
    {
        Root = root;
        PatchRoot = Path.Combine(root, "patches");
        Directory.CreateDirectory(PatchRoot);
        SourceBinPath = Path.Combine(root, "clean.bin");
        SourceCuePath = Path.Combine(root, "clean.cue");
        TargetBinPath = Path.Combine(root, "accepted.bin");
        TargetCuePath = Path.Combine(root, "accepted.cue");
        BinPatchPath = Path.Combine(PatchRoot, "disc.bin.bps");
        CuePatchPath = Path.Combine(PatchRoot, "disc.cue.bps");
        ManifestPath = Path.Combine(root, "manifest.json");
        OutputDirectory = Path.Combine(root, "output");

        byte[] sourceBin = Enumerable.Range(0, 64)
            .Select(index => (byte)((index * 17 + 3) % 256))
            .ToArray();
        byte[] targetBin = new byte[sourceBin.Length + 5];
        sourceBin.CopyTo(targetBin, 0);
        byte[] localized = Encoding.ASCII.GetBytes("pt-br!!!");
        TestAssert.Equal(8, localized.Length, "Reference replacement length changed.");
        localized.CopyTo(targetBin, 7);
        "-tail"u8.CopyTo(targetBin.AsSpan(sourceBin.Length));
        byte[] sourceCue = "FILE clean.bin BINARY\n"u8.ToArray();
        byte[] targetCue = "FILE accepted.bin BINARY\n"u8.ToArray();

        File.WriteAllBytes(SourceBinPath, sourceBin);
        File.WriteAllBytes(TargetBinPath, targetBin);
        File.WriteAllBytes(SourceCuePath, sourceCue);
        File.WriteAllBytes(TargetCuePath, targetCue);
        File.WriteAllBytes(BinPatchPath, Convert.FromBase64String(ReferencePatchBase64));
        File.WriteAllBytes(CuePatchPath, SyntheticBpsBuilder.CreateTargetRead(sourceCue, targetCue));
        WriteManifest();
    }

    internal string Root { get; }

    internal string PatchRoot { get; }

    internal string SourceBinPath { get; }

    internal string SourceCuePath { get; }

    internal string TargetBinPath { get; }

    internal string TargetCuePath { get; }

    internal string BinPatchPath { get; }

    internal string CuePatchPath { get; }

    internal string ManifestPath { get; }

    internal string OutputDirectory { get; }

    internal static TestFixture Create()
    {
        string repositoryRoot = RepositoryRoot();
        string parent = Path.Combine(repositoryRoot, "local", "dotnet-test-tmp");
        Directory.CreateDirectory(parent);
        string root = Path.Combine(parent, $"case-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return new TestFixture(root);
    }

    internal void ReplaceBinFixture(byte[] source, byte[] target, byte[] patch)
    {
        File.WriteAllBytes(SourceBinPath, source);
        File.WriteAllBytes(TargetBinPath, target);
        File.WriteAllBytes(BinPatchPath, patch);
        WriteManifest();
    }

    internal void WriteManifest(
        string profile = "linear-streaming-v1",
        string? binPatchManifestPath = null,
        string? targetBinSha256 = null,
        string? targetCueSha256 = null)
    {
        var disc = new Dictionary<string, object?>
        {
            ["id"] = "disc1",
            ["display_name"] = "Synthetic Disc",
            ["authority_date"] = "2026-08-17",
            ["acceptance_scope"] = "Synthetic cross-language test only.",
            ["source"] = new Dictionary<string, object?>
            {
                ["bin"] = Artifact(SourceBinPath),
                ["cue"] = Artifact(SourceCuePath),
            },
            ["target"] = new Dictionary<string, object?>
            {
                ["bin"] = Artifact(TargetBinPath, targetBinSha256),
                ["cue"] = Artifact(TargetCuePath, targetCueSha256),
            },
            ["patches"] = new Dictionary<string, object?>
            {
                ["bin"] = Patch(BinPatchPath, binPatchManifestPath ?? "disc.bin.bps"),
                ["cue"] = Patch(CuePatchPath, "disc.cue.bps"),
            },
        };
        var manifest = new Dictionary<string, object?>
        {
            ["schema_version"] = 1,
            ["release_id"] = "synthetic-test",
            ["status"] = "release-candidate",
            ["patch_format"] = new Dictionary<string, object?>
            {
                ["id"] = "BPS1",
                ["profile"] = profile,
                ["specification"] = "urn:test:bps1",
                ["implementation_license"] = "MIT",
            },
            ["discs"] = new[] { disc },
        };
        File.WriteAllText(
            ManifestPath,
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));
    }

    internal static string Sha256(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    internal static string RepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "Mgs1.Patcher.sln")))
        {
            current = current.Parent;
        }

        return current?.FullName
            ?? throw new InvalidOperationException("Cannot locate repository root for test workspace.");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static Dictionary<string, object?> Artifact(string path, string? sha256 = null) => new()
    {
        ["file_name"] = Path.GetFileName(path),
        ["size"] = new FileInfo(path).Length,
        ["sha256"] = sha256 ?? Sha256(path),
    };

    private static Dictionary<string, object?> Patch(string path, string manifestPath) => new()
    {
        ["file"] = manifestPath,
        ["size"] = new FileInfo(path).Length,
        ["sha256"] = Sha256(path),
    };
}
