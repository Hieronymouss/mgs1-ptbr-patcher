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
            ("other-disc field mix-up is detected", WrongDiscFieldDetectionAsync),
            ("same-size hash mismatch is rejected", SameSizeHashMismatchAsync),
            ("BIN and CUE cross-disc mismatch is rejected", BinCueMismatchAsync),
            ("missing and corrupt payloads map safely", MissingAndCorruptPayloadsAsync),
            ("existing output is refused", ExistingOutputAsync),
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
        string existing = Path.Combine(fixture.OutputDirectory, "mgs1-ptbr-disc1.bin");
        byte[] sentinel = "do-not-replace"u8.ToArray();
        File.WriteAllBytes(existing, sentinel);
        PatchWorkflowResult result = await controller.ApplyAsync(fixture.OutputDirectory).ConfigureAwait(false);
        TestAssert.False(result.Succeeded, "Existing output unexpectedly applied.");
        TestAssert.Equal(PatchUserErrorCategory.ExistingOutput, result.Message.Category, "Existing-output category differs.");
        TestAssert.True(File.ReadAllBytes(existing).SequenceEqual(sentinel), "Existing output changed.");
    }

    private static async Task InsufficientSpaceAsync()
    {
        using GuiFixture fixture = GuiFixture.Create();
        string root = Path.GetPathRoot(fixture.Root) ?? throw new InvalidOperationException("Test volume has no root.");
        long available = new DriveInfo(root).AvailableFreeSpace;
        PatchApplyOptions options = TestOptions with { FreeSpaceReserveBytes = checked(available + 1) };
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
