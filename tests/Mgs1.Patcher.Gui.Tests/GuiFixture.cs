using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mgs1.Patcher.Gui.Logic;

namespace Mgs1.Patcher.Gui.Tests;

internal sealed class GuiFixture : IDisposable
{
    private GuiFixture(string root)
    {
        Root = root;
        DataRoot = Path.Combine(root, "data");
        PatchRoot = Path.Combine(DataRoot, "patches");
        InputRoot = Path.Combine(root, "input");
        OutputDirectory = Path.Combine(root, "output");
        Directory.CreateDirectory(PatchRoot);
        Directory.CreateDirectory(InputRoot);
        Disc1BinPath = Path.Combine(InputRoot, "disc1.bin");
        Disc1CuePath = Path.Combine(InputRoot, "disc1.cue");
        Disc2BinPath = Path.Combine(InputRoot, "disc2.bin");
        Disc2CuePath = Path.Combine(InputRoot, "disc2.cue");
        Disc1BinPatchPath = Path.Combine(PatchRoot, "disc1.bin.bps");
        Disc1CuePatchPath = Path.Combine(PatchRoot, "disc1.cue.bps");
        Disc2BinPatchPath = Path.Combine(PatchRoot, "disc2.bin.bps");
        Disc2CuePatchPath = Path.Combine(PatchRoot, "disc2.cue.bps");
        ManifestPath = Path.Combine(DataRoot, "release-manifest.json");

        byte[] disc1Bin = Bytes(0x11, 32 * 1024);
        byte[] disc2Bin = Bytes(0x22, 32 * 1024);
        byte[] disc1Cue = "FILE \"disc1.bin\" BINARY\r\n"u8.ToArray();
        byte[] disc2Cue = "FILE \"disc2.bin\" BINARY\r\n"u8.ToArray();
        byte[] disc1Target = Bytes(0x61, 40 * 1024);
        byte[] disc2Target = Bytes(0x72, 40 * 1024);
        byte[] disc1CueTarget = "FILE \"mgs1-ptbr-disc1.bin\" BINARY\r\n"u8.ToArray();
        byte[] disc2CueTarget = "FILE \"mgs1-ptbr-disc2.bin\" BINARY\r\n"u8.ToArray();

        File.WriteAllBytes(Disc1BinPath, disc1Bin);
        File.WriteAllBytes(Disc1CuePath, disc1Cue);
        File.WriteAllBytes(Disc2BinPath, disc2Bin);
        File.WriteAllBytes(Disc2CuePath, disc2Cue);
        File.WriteAllBytes(Disc1BinPatchPath, SyntheticBpsBuilder.CreateTargetRead(disc1Bin, disc1Target));
        File.WriteAllBytes(Disc1CuePatchPath, SyntheticBpsBuilder.CreateTargetRead(disc1Cue, disc1CueTarget));
        File.WriteAllBytes(Disc2BinPatchPath, SyntheticBpsBuilder.CreateTargetRead(disc2Bin, disc2Target));
        File.WriteAllBytes(Disc2CuePatchPath, SyntheticBpsBuilder.CreateTargetRead(disc2Cue, disc2CueTarget));

        WriteManifest(
            new Artifact("disc1.bin", disc1Bin),
            new Artifact("disc1.cue", disc1Cue),
            new Artifact("mgs1-ptbr-disc1.bin", disc1Target),
            new Artifact("mgs1-ptbr-disc1.cue", disc1CueTarget),
            new Artifact("disc2.bin", disc2Bin),
            new Artifact("disc2.cue", disc2Cue),
            new Artifact("mgs1-ptbr-disc2.bin", disc2Target),
            new Artifact("mgs1-ptbr-disc2.cue", disc2CueTarget));
    }

    internal string Root { get; }

    internal string DataRoot { get; }

    internal string PatchRoot { get; }

    internal string InputRoot { get; }

    internal string OutputDirectory { get; }

    internal string ManifestPath { get; }

    internal string Disc1BinPath { get; }

    internal string Disc1CuePath { get; }

    internal string Disc2BinPath { get; }

    internal string Disc2CuePath { get; }

    internal string Disc1BinPatchPath { get; }

    internal string Disc1CuePatchPath { get; }

    internal string Disc2BinPatchPath { get; }

    internal string Disc2CuePatchPath { get; }

    internal ApplicationDataRoot ApplicationData => new(DataRoot, ManifestPath, PatchRoot);

    internal static GuiFixture Create()
    {
        string parent = Path.Combine(RepositoryRoot(), "local", "gui-test-tmp");
        Directory.CreateDirectory(parent);
        string root = Path.Combine(parent, $"case-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return new GuiFixture(root);
    }

    internal string CreateWrongSameSizeDisc1Cue()
    {
        string directory = Path.Combine(Root, "wrong-same-size");
        Directory.CreateDirectory(directory);
        string bin = Path.Combine(directory, "disc1.bin");
        byte[] changed = File.ReadAllBytes(Disc1BinPath);
        changed[^1] ^= 1;
        File.WriteAllBytes(bin, changed);
        string cue = Path.Combine(directory, "disc1.cue");
        File.Copy(Disc1CuePath, cue);
        return cue;
    }

    internal string CreateMixedDiscCue()
    {
        string directory = Path.Combine(Root, "mixed");
        Directory.CreateDirectory(directory);
        string cue = Path.Combine(directory, "disc1.cue");
        File.Copy(Disc1CuePath, cue);
        File.Copy(Disc2BinPath, Path.Combine(directory, "disc1.bin"));
        return cue;
    }

    internal void CorruptDisc1BinPayload()
    {
        byte[] patch = File.ReadAllBytes(Disc1BinPatchPath);
        patch[^1] ^= 1;
        File.WriteAllBytes(Disc1BinPatchPath, patch);
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

    private void WriteManifest(
        Artifact disc1Bin,
        Artifact disc1Cue,
        Artifact disc1TargetBin,
        Artifact disc1TargetCue,
        Artifact disc2Bin,
        Artifact disc2Cue,
        Artifact disc2TargetBin,
        Artifact disc2TargetCue)
    {
        var manifest = new Dictionary<string, object?>
        {
            ["schema_version"] = 1,
            ["release_id"] = "gui-synthetic-test",
            ["status"] = "release-candidate",
            ["patch_format"] = new Dictionary<string, object?>
            {
                ["id"] = "BPS1",
                ["profile"] = "linear-streaming-v1",
                ["specification"] = "urn:test:bps1",
                ["implementation_license"] = "MIT",
            },
            ["discs"] = new[]
            {
                Disc("disc1", disc1Bin, disc1Cue, disc1TargetBin, disc1TargetCue, "disc1.bin.bps", "disc1.cue.bps"),
                Disc("disc2", disc2Bin, disc2Cue, disc2TargetBin, disc2TargetCue, "disc2.bin.bps", "disc2.cue.bps"),
            },
        };
        File.WriteAllText(
            ManifestPath,
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));
    }

    private Dictionary<string, object?> Disc(
        string id,
        Artifact sourceBin,
        Artifact sourceCue,
        Artifact targetBin,
        Artifact targetCue,
        string binPatchName,
        string cuePatchName) => new()
    {
        ["id"] = id,
        ["display_name"] = $"Synthetic {id}",
        ["authority_date"] = "2026-08-18",
        ["acceptance_scope"] = "Synthetic GUI controller test only.",
        ["source"] = Pair(sourceBin, sourceCue),
        ["target"] = Pair(targetBin, targetCue),
        ["patches"] = new Dictionary<string, object?>
        {
            ["bin"] = Patch(Path.Combine(PatchRoot, binPatchName), binPatchName),
            ["cue"] = Patch(Path.Combine(PatchRoot, cuePatchName), cuePatchName),
        },
    };

    private static Dictionary<string, object?> Pair(Artifact bin, Artifact cue) => new()
    {
        ["bin"] = bin.ToManifest(),
        ["cue"] = cue.ToManifest(),
    };

    private static Dictionary<string, object?> Patch(string path, string name) => new()
    {
        ["file"] = name,
        ["size"] = new FileInfo(path).Length,
        ["sha256"] = Sha256(path),
    };

    private static byte[] Bytes(byte seed, int size) =>
        Enumerable.Range(0, size).Select(index => (byte)(seed + index * 31)).ToArray();

    private static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static string RepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "Mgs1.Patcher.sln")))
        {
            current = current.Parent;
        }

        return current?.FullName ?? throw new InvalidOperationException("Cannot locate repository root.");
    }

    private sealed record Artifact(string Name, byte[] Contents)
    {
        internal Dictionary<string, object?> ToManifest() => new()
        {
            ["file_name"] = Name,
            ["size"] = Contents.LongLength,
            ["sha256"] = Convert.ToHexString(SHA256.HashData(Contents)).ToLowerInvariant(),
        };
    }
}
