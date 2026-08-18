using Mgs1.Patcher.Core;
using Mgs1.Patcher.Gui.Logic;

namespace Mgs1.Patcher.Gui.Tests;

internal static class Program
{
    private static async Task<int> Main()
    {
        (string Name, Func<Task> Run)[] tests =
        [
            ("valid pairs are recognized exactly", ValidPairsRecognizedAsync),
            ("one CUE path is accepted for drag-and-drop", SingleCueDropAcceptedAsync),
            ("invalid drag-and-drop shapes are rejected", InvalidCueDropsRejectedAsync),
            ("one existing destination directory is accepted for drag-and-drop", DestinationDropAcceptedAsync),
            ("invalid destination drops are rejected", InvalidDestinationDropsRejectedAsync),
            ("output naming preserves the selected CUE stem", OutputNamingPolicyAsync),
            ("other-disc field mix-up is detected", WrongDiscFieldDetectionAsync),
            ("same-size hash mismatch is rejected", SameSizeHashMismatchAsync),
            ("BIN and CUE cross-disc mismatch is rejected", BinCueMismatchAsync),
            ("missing and corrupt payloads map safely", MissingAndCorruptPayloadsAsync),
            ("selected CUE names become PT-BR output pairs", SelectedNamesBecomeOutputsAsync),
            ("existing output is refused", ExistingOutputAsync),
            ("cross-disc output-name collision is refused before apply", OutputNameCollisionAsync),
            ("insufficient space maps safely", InsufficientSpaceAsync),
            ("cancellation rolls back the active disc", CancellationRollbackAsync),
            ("progress and workflow states transition", ProgressAndStateTransitionsAsync),
            ("rejected selections create no output", RejectedSelectionCreatesNoOutputAsync),
            ("production data root rejects launch overrides", ProductionDataRootRejectsOverridesAsync),
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

        Console.WriteLine($"{tests.Length - failures}/{tests.Length} focused GUI/controller tests passed.");
        return failures == 0 ? 0 : 1;
    }

    private static Task SingleCueDropAcceptedAsync()
    {
        string cuePath = Path.Combine("Roms", "Metal Gear Solid (Disc 1).CUE");
        CueDropCandidate candidate = CueDropPolicy.Evaluate([cuePath]);
        TestAssert.Equal(CueDropCandidateKind.Accepted, candidate.Kind, "Single CUE drop was rejected.");
        TestAssert.Equal(cuePath, candidate.CuePath!, "Accepted CUE path changed.");
        return Task.CompletedTask;
    }

    private static Task InvalidCueDropsRejectedAsync()
    {
        TestAssert.Equal(
            CueDropCandidateKind.NoFiles,
            CueDropPolicy.Evaluate(null).Kind,
            "Empty drop was accepted.");
        TestAssert.Equal(
            CueDropCandidateKind.MultipleFiles,
            CueDropPolicy.Evaluate(["disc1.cue", "disc2.cue"]).Kind,
            "Multiple-file drop was accepted.");
        TestAssert.Equal(
            CueDropCandidateKind.NotCue,
            CueDropPolicy.Evaluate(["disc1.bin"]).Kind,
            "Non-CUE drop was accepted.");
        return Task.CompletedTask;
    }

    private static Task DestinationDropAcceptedAsync()
    {
        using GuiFixture fixture = GuiFixture.Create();
        DestinationDropCandidate candidate = DestinationDropPolicy.Evaluate([fixture.Root]);
        TestAssert.Equal(DestinationDropCandidateKind.Accepted, candidate.Kind, "Existing destination directory was rejected.");
        TestAssert.Equal(fixture.Root, candidate.DirectoryPath!, "Accepted destination path changed.");
        return Task.CompletedTask;
    }

    private static Task InvalidDestinationDropsRejectedAsync()
    {
        using GuiFixture fixture = GuiFixture.Create();
        TestAssert.Equal(
            DestinationDropCandidateKind.NoPaths,
            DestinationDropPolicy.Evaluate(null).Kind,
            "Empty destination drop was accepted.");
        TestAssert.Equal(
            DestinationDropCandidateKind.MultiplePaths,
            DestinationDropPolicy.Evaluate([fixture.Root, fixture.InputRoot]).Kind,
            "Multiple destination paths were accepted.");
        TestAssert.Equal(
            DestinationDropCandidateKind.NotDirectory,
            DestinationDropPolicy.Evaluate([fixture.Disc1CuePath]).Kind,
            "A file was accepted as a destination directory.");
        return Task.CompletedTask;
    }

    private static Task ProductionDataRootRejectsOverridesAsync()
    {
        ApplicationDataRoot resolved = ApplicationDataRoot.Resolve([]);
        string expected = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "data"));
        TestAssert.Equal(expected, resolved.RootPath, "Production data root differs.");

        bool rejected = false;
        try
        {
            _ = ApplicationDataRoot.Resolve(["--data-root", "elsewhere"]);
        }
        catch (ApplicationDataException)
        {
            rejected = true;
        }

        TestAssert.True(rejected, "Launch-time data-root override was accepted.");
        return Task.CompletedTask;
    }

    private static async Task ValidPairsRecognizedAsync()
    {
        using GuiFixture fixture = GuiFixture.Create();
        PatchWorkflowController controller = await CreateControllerAsync(fixture).ConfigureAwait(false);
        DiscSelectionState disc1 = await controller.SelectCueAsync("disc1", fixture.Disc1CuePath).ConfigureAwait(false);
        DiscSelectionState disc2 = await controller.SelectCueAsync("disc2", fixture.Disc2CuePath).ConfigureAwait(false);
        TestAssert.Equal(DiscSelectionKind.Ready, disc1.Kind, "Disc 1 was not recognized.");
        TestAssert.Equal(DiscSelectionKind.Ready, disc2.Kind, "Disc 2 was not recognized.");
        TestAssert.Equal("disc1", disc1.RecognizedDiscId!, "Disc 1 identity differs.");
        TestAssert.Equal("disc2", disc2.RecognizedDiscId!, "Disc 2 identity differs.");
    }

    private static Task OutputNamingPolicyAsync()
    {
        OutputPairNames names = OutputNamePolicy.FromCuePath(Path.Combine(
            "Roms",
            "Metal Gear Solid (USA) (Disc 1) (Rev 1).cue"));
        TestAssert.Equal(
            "Metal Gear Solid (USA) (Disc 1) (Rev 1) (PT-BR).bin",
            names.BinFileName,
            "BIN output did not preserve the selected CUE stem.");
        TestAssert.Equal(
            "Metal Gear Solid (USA) (Disc 1) (Rev 1) (PT-BR).cue",
            names.CueFileName,
            "CUE output did not preserve the selected CUE stem.");

        OutputPairNames alreadySuffixed = OutputNamePolicy.FromCuePath("Game (PT-BR).cue");
        TestAssert.Equal("Game (PT-BR).cue", alreadySuffixed.CueFileName, "PT-BR suffix was duplicated.");

        OutputPairNames shortened = OutputNamePolicy.FromCuePath(new string('x', 300) + ".cue");
        TestAssert.True(shortened.CueFileName.Length <= 244, "Excessive CUE name was not bounded safely.");
        return Task.CompletedTask;
    }

    private static async Task WrongDiscFieldDetectionAsync()
    {
        using GuiFixture fixture = GuiFixture.Create();
        PatchWorkflowController controller = await CreateControllerAsync(fixture).ConfigureAwait(false);
        DiscSelectionState selection = await controller.SelectCueAsync("disc1", fixture.Disc2CuePath).ConfigureAwait(false);
        TestAssert.Equal(DiscSelectionKind.WrongDisc, selection.Kind, "Other-disc pair was not identified.");
        TestAssert.True(
            PatchUserMessages.ForSelection(selection).Text.Contains("outro disco", StringComparison.OrdinalIgnoreCase),
            "Wrong-disc message is not specific.");
    }

    private static async Task SameSizeHashMismatchAsync()
    {
        using GuiFixture fixture = GuiFixture.Create();
        PatchWorkflowController controller = await CreateControllerAsync(fixture).ConfigureAwait(false);
        DiscSelectionState selection = await controller.SelectCueAsync("disc1", fixture.CreateWrongSameSizeDisc1Cue()).ConfigureAwait(false);
        TestAssert.Equal(DiscSelectionKind.Unsupported, selection.Kind, "Same-size wrong BIN was accepted.");
        TestAssert.True(
            PatchUserMessages.ForSelection(selection).Text.Contains("USA Rev 1", StringComparison.Ordinal),
            "Unsupported-image guidance is missing.");
    }

    private static async Task BinCueMismatchAsync()
    {
        using GuiFixture fixture = GuiFixture.Create();
        PatchWorkflowController controller = await CreateControllerAsync(fixture).ConfigureAwait(false);
        DiscSelectionState selection = await controller.SelectCueAsync("disc1", fixture.CreateMixedDiscCue()).ConfigureAwait(false);
        TestAssert.Equal(DiscSelectionKind.MixedDiscFiles, selection.Kind, "Mixed BIN/CUE pair was accepted.");
    }

    private static async Task MissingAndCorruptPayloadsAsync()
    {
        using (GuiFixture missing = GuiFixture.Create())
        {
            PatchWorkflowController controller = await SelectValidPairsAsync(missing).ConfigureAwait(false);
            File.Delete(missing.Disc1BinPatchPath);
            PatchWorkflowResult result = await controller.ApplyAsync(missing.OutputDirectory).ConfigureAwait(false);
            TestAssert.False(result.Succeeded, "Missing payload unexpectedly applied.");
            TestAssert.Equal(PatchUserErrorCategory.ApplicationPayload, result.Message.Category, "Missing payload category differs.");
            TestAssert.False(Directory.Exists(missing.OutputDirectory), "Missing payload created output.");
        }

        using (GuiFixture corrupt = GuiFixture.Create())
        {
            PatchWorkflowController controller = await SelectValidPairsAsync(corrupt).ConfigureAwait(false);
            corrupt.CorruptDisc1BinPayload();
            PatchWorkflowResult result = await controller.ApplyAsync(corrupt.OutputDirectory).ConfigureAwait(false);
            TestAssert.False(result.Succeeded, "Corrupt payload unexpectedly applied.");
            TestAssert.Equal(PatchUserErrorCategory.ApplicationPayload, result.Message.Category, "Corrupt payload category differs.");
            TestAssert.False(Directory.Exists(corrupt.OutputDirectory), "Corrupt payload created output.");
        }
    }

    private static async Task ExistingOutputAsync()
    {
        using GuiFixture fixture = GuiFixture.Create();
        PatchWorkflowController controller = await SelectValidPairsAsync(fixture).ConfigureAwait(false);
        Directory.CreateDirectory(fixture.OutputDirectory);
        string existing = Path.Combine(fixture.OutputDirectory, "disc1 (PT-BR).bin");
        byte[] sentinel = "do-not-replace"u8.ToArray();
        File.WriteAllBytes(existing, sentinel);
        PatchWorkflowResult result = await controller.ApplyAsync(fixture.OutputDirectory).ConfigureAwait(false);
        TestAssert.False(result.Succeeded, "Existing output unexpectedly applied.");
        TestAssert.Equal(PatchUserErrorCategory.ExistingOutput, result.Message.Category, "Existing-output category differs.");
        TestAssert.True(File.ReadAllBytes(existing).SequenceEqual(sentinel), "Existing output changed.");
    }

    private static async Task SelectedNamesBecomeOutputsAsync()
    {
        using GuiFixture fixture = GuiFixture.Create();
        PatchWorkflowController controller = await SelectValidPairsAsync(fixture).ConfigureAwait(false);
        PatchWorkflowResult result = await controller.ApplyAsync(fixture.OutputDirectory).ConfigureAwait(false);
        TestAssert.True(result.Succeeded, "Valid named-output apply failed.");

        string disc1Bin = Path.Combine(fixture.OutputDirectory, "disc1 (PT-BR).bin");
        string disc1Cue = Path.Combine(fixture.OutputDirectory, "disc1 (PT-BR).cue");
        string disc2Bin = Path.Combine(fixture.OutputDirectory, "disc2 (PT-BR).bin");
        string disc2Cue = Path.Combine(fixture.OutputDirectory, "disc2 (PT-BR).cue");
        TestAssert.True(File.Exists(disc1Bin), "Disc 1 BIN did not inherit the selected CUE name.");
        TestAssert.True(File.Exists(disc1Cue), "Disc 1 CUE did not inherit the selected CUE name.");
        TestAssert.True(File.Exists(disc2Bin), "Disc 2 BIN did not inherit the selected CUE name.");
        TestAssert.True(File.Exists(disc2Cue), "Disc 2 CUE did not inherit the selected CUE name.");
        TestAssert.True(
            File.ReadAllText(disc1Cue).Contains("\"disc1 (PT-BR).bin\"", StringComparison.Ordinal),
            "Disc 1 CUE does not reference its renamed BIN.");
        TestAssert.True(
            File.ReadAllText(disc2Cue).Contains("\"disc2 (PT-BR).bin\"", StringComparison.Ordinal),
            "Disc 2 CUE does not reference its renamed BIN.");
        TestAssert.False(File.Exists(Path.Combine(fixture.OutputDirectory, "mgs1-ptbr-disc1.bin")), "Checkpoint Disc 1 name leaked into publication.");
        TestAssert.False(File.Exists(Path.Combine(fixture.OutputDirectory, "mgs1-ptbr-disc2.bin")), "Checkpoint Disc 2 name leaked into publication.");
    }

    private static async Task OutputNameCollisionAsync()
    {
        using GuiFixture fixture = GuiFixture.Create();
        string firstRoot = Path.Combine(fixture.Root, "same-name-disc1");
        string secondRoot = Path.Combine(fixture.Root, "same-name-disc2");
        Directory.CreateDirectory(firstRoot);
        Directory.CreateDirectory(secondRoot);
        File.Copy(fixture.Disc1BinPath, Path.Combine(firstRoot, "disc1.bin"));
        File.Copy(fixture.Disc1CuePath, Path.Combine(firstRoot, "same-name.cue"));
        File.Copy(fixture.Disc2BinPath, Path.Combine(secondRoot, "disc2.bin"));
        File.Copy(fixture.Disc2CuePath, Path.Combine(secondRoot, "same-name.cue"));

        PatchWorkflowController controller = await CreateControllerAsync(fixture).ConfigureAwait(false);
        TestAssert.Equal(
            DiscSelectionKind.Ready,
            (await controller.SelectCueAsync("disc1", Path.Combine(firstRoot, "same-name.cue")).ConfigureAwait(false)).Kind,
            "Disc 1 collision fixture was not recognized.");
        TestAssert.Equal(
            DiscSelectionKind.Ready,
            (await controller.SelectCueAsync("disc2", Path.Combine(secondRoot, "same-name.cue")).ConfigureAwait(false)).Kind,
            "Disc 2 collision fixture was not recognized.");

        PatchWorkflowResult result = await controller.ApplyAsync(fixture.OutputDirectory).ConfigureAwait(false);
        TestAssert.False(result.Succeeded, "Colliding output names unexpectedly applied.");
        TestAssert.Equal(PatchUserErrorCategory.OutputNameConflict, result.Message.Category, "Collision category differs.");
        TestAssert.False(Directory.Exists(fixture.OutputDirectory), "Collision created an output before preflight completed.");
    }

    private static async Task InsufficientSpaceAsync()
    {
        using GuiFixture fixture = GuiFixture.Create();
        string root = Path.GetPathRoot(fixture.Root) ?? throw new InvalidOperationException("Test volume has no root.");
        long available = new DriveInfo(root).AvailableFreeSpace;
        PatchApplyOptions options = TestOptions with { FreeSpaceReserveBytes = checked(available + 1024L * 1024 * 1024) };
        PatchWorkflowController controller = await SelectValidPairsAsync(fixture, options).ConfigureAwait(false);
        PatchWorkflowResult result = await controller.ApplyAsync(fixture.OutputDirectory).ConfigureAwait(false);
        TestAssert.False(result.Succeeded, "Insufficient-space apply unexpectedly succeeded.");
        TestAssert.Equal(PatchUserErrorCategory.InsufficientSpace, result.Message.Category, "Insufficient-space category differs.");
        TestAssert.False(Directory.Exists(fixture.OutputDirectory), "Insufficient space created output.");
    }

    private static async Task CancellationRollbackAsync()
    {
        using GuiFixture fixture = GuiFixture.Create();
        using var cancellation = new CancellationTokenSource();
        PatchApplyOptions options = TestOptions with
        {
            Progress = new InlineProgress<PatchProgress>(value =>
            {
                if (value.Phase == PatchProgressPhase.ApplyingBin && value.CompletedBytes > 0)
                {
                    cancellation.Cancel();
                }
            }),
        };
        PatchWorkflowController controller = await SelectValidPairsAsync(fixture, options).ConfigureAwait(false);
        PatchWorkflowResult result = await controller.ApplyAsync(fixture.OutputDirectory, cancellationToken: cancellation.Token).ConfigureAwait(false);
        TestAssert.False(result.Succeeded, "Cancelled apply unexpectedly succeeded.");
        TestAssert.Equal(PatchUserErrorCategory.Cancelled, result.Message.Category, "Cancellation category differs.");
        TestAssert.True(Directory.Exists(fixture.OutputDirectory), "Cancellation did not reach output staging.");
        TestAssert.False(Directory.EnumerateFileSystemEntries(fixture.OutputDirectory).Any(), "Cancellation left an output or partial file.");
    }

    private static async Task ProgressAndStateTransitionsAsync()
    {
        using GuiFixture fixture = GuiFixture.Create();
        PatchWorkflowController controller = await SelectValidPairsAsync(fixture).ConfigureAwait(false);
        var stages = new List<PatchWorkflowStage>();
        var progress = new List<PatchProgress>();
        controller.StateChanged += (_, state) => stages.Add(state.Stage);
        PatchWorkflowResult result = await controller.ApplyAsync(
            fixture.OutputDirectory,
            new InlineProgress<PatchProgress>(value => progress.Add(value))).ConfigureAwait(false);
        TestAssert.True(result.Succeeded, "Valid synthetic apply failed.");
        TestAssert.True(stages.Contains(PatchWorkflowStage.ApplyingDisc1), "Disc 1 apply state missing.");
        TestAssert.True(stages.Contains(PatchWorkflowStage.ApplyingDisc2), "Disc 2 apply state missing.");
        TestAssert.Equal(PatchWorkflowStage.Completed, controller.State.Stage, "Completed state missing.");
        TestAssert.True(progress.Any(value => value.Phase == PatchProgressPhase.Completed), "Completed progress missing.");
    }

    private static async Task RejectedSelectionCreatesNoOutputAsync()
    {
        using GuiFixture fixture = GuiFixture.Create();
        PatchWorkflowController controller = await CreateControllerAsync(fixture).ConfigureAwait(false);
        await controller.SelectCueAsync("disc1", fixture.CreateWrongSameSizeDisc1Cue()).ConfigureAwait(false);
        await controller.SelectCueAsync("disc2", fixture.Disc2CuePath).ConfigureAwait(false);
        PatchWorkflowResult result = await controller.ApplyAsync(fixture.OutputDirectory).ConfigureAwait(false);
        TestAssert.False(result.Succeeded, "Rejected selection unexpectedly applied.");
        TestAssert.Equal(PatchUserErrorCategory.InvalidSelection, result.Message.Category, "Rejected-selection category differs.");
        TestAssert.False(Directory.Exists(fixture.OutputDirectory), "Rejected selection created output.");
    }

    private static readonly PatchApplyOptions TestOptions = new()
    {
        FreeSpaceReserveBytes = 0,
        IoBufferSize = 4096,
    };

    private static Task<PatchWorkflowController> CreateControllerAsync(GuiFixture fixture, PatchApplyOptions? options = null) =>
        PatchWorkflowController.CreateAsync(fixture.ApplicationData, options ?? TestOptions);

    private static async Task<PatchWorkflowController> SelectValidPairsAsync(
        GuiFixture fixture,
        PatchApplyOptions? options = null)
    {
        PatchWorkflowController controller = await CreateControllerAsync(fixture, options).ConfigureAwait(false);
        TestAssert.Equal(DiscSelectionKind.Ready, (await controller.SelectCueAsync("disc1", fixture.Disc1CuePath).ConfigureAwait(false)).Kind, "Disc 1 selection failed.");
        TestAssert.Equal(DiscSelectionKind.Ready, (await controller.SelectCueAsync("disc2", fixture.Disc2CuePath).ConfigureAwait(false)).Kind, "Disc 2 selection failed.");
        return controller;
    }
}
