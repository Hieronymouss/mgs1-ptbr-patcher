using Mgs1.Patcher.Core;

namespace Mgs1.Patcher.Gui.Logic;

public enum DiscSelectionKind
{
    Unselected,
    Checking,
    Ready,
    WrongDisc,
    MixedDiscFiles,
    Unsupported,
    CueUnreadable,
}

public enum PatchWorkflowStage
{
    Ready,
    ApplyingDisc1,
    ApplyingDisc2,
    Completed,
    Cancelled,
    Failed,
}

public enum PatchUserErrorCategory
{
    Success,
    InputIntegrity,
    ApplicationPayload,
    ExistingOutput,
    InsufficientSpace,
    UnsafePath,
    Cancelled,
    InvalidSelection,
    UnexpectedIo,
}

public sealed record DiscSelectionState(
    string RequestedDiscId,
    DiscSelectionKind Kind,
    string? CuePath,
    string? BinPath,
    string? RecognizedDiscId)
{
    public static DiscSelectionState Empty(string discId) =>
        new(discId, DiscSelectionKind.Unselected, null, null, null);
}

public sealed record PatchWorkflowState(
    PatchWorkflowStage Stage,
    DiscSelectionState Disc1,
    DiscSelectionState Disc2,
    PatchProgress? Progress);

public sealed record PatchUserMessage(PatchUserErrorCategory Category, string Text);

public sealed record PatchWorkflowResult(
    bool Succeeded,
    PatchUserMessage Message,
    IReadOnlyList<string> CompletedDiscIds);

/// <summary>
/// UI-facing orchestration only. It identifies both pairs before calling the
/// core, then delegates every apply, verification, publication, and rollback
/// decision to <see cref="PatchBundleApplier"/>.
/// </summary>
public sealed class PatchWorkflowController
{
    private readonly ReleaseManifest manifest;
    private readonly ApplicationDataRoot dataRoot;
    private readonly PatchApplyOptions applyOptions;
    private DiscSelectionState disc1 = DiscSelectionState.Empty("disc1");
    private DiscSelectionState disc2 = DiscSelectionState.Empty("disc2");
    private PatchWorkflowStage stage = PatchWorkflowStage.Ready;
    private PatchProgress? progress;

    private PatchWorkflowController(
        ReleaseManifest manifest,
        ApplicationDataRoot dataRoot,
        PatchApplyOptions applyOptions)
    {
        this.manifest = manifest;
        this.dataRoot = dataRoot;
        this.applyOptions = applyOptions;
    }

    public event EventHandler<PatchWorkflowState>? StateChanged;

    public PatchWorkflowState State => new(stage, disc1, disc2, progress);

    public ReleaseManifest Manifest => manifest;

    public static async Task<PatchWorkflowController> CreateAsync(
        ApplicationDataRoot dataRoot,
        PatchApplyOptions? applyOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataRoot);
        ReleaseManifest manifest = await ManifestLoader.LoadAsync(dataRoot.ManifestPath, cancellationToken)
            .ConfigureAwait(false);
        PatchApplyOptions options = applyOptions ?? new PatchApplyOptions();
        return new PatchWorkflowController(manifest, dataRoot, options);
    }

    public async Task<DiscSelectionState> SelectCueAsync(
        string discId,
        string cuePath,
        CancellationToken cancellationToken = default)
    {
        EnsureKnownDisc(discId);
        SetSelection(discId, new DiscSelectionState(
            discId,
            DiscSelectionKind.Checking,
            cuePath,
            null,
            null));
        try
        {
            string binPath = await CueBinResolver.ResolveAsync(cuePath, cancellationToken).ConfigureAwait(false);
            SourcePairIdentification identification = await SourcePairIdentifier.IdentifyAsync(
                manifest,
                binPath,
                cuePath,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            DiscSelectionState selection = ToSelection(discId, cuePath, binPath, identification);
            SetSelection(discId, selection);
            return selection;
        }
        catch (OperationCanceledException)
        {
            SetSelection(discId, DiscSelectionState.Empty(discId));
            throw;
        }
        catch (CueResolutionException)
        {
            DiscSelectionState selection = new(discId, DiscSelectionKind.CueUnreadable, cuePath, null, null);
            SetSelection(discId, selection);
            return selection;
        }
        catch (PatcherException)
        {
            DiscSelectionState selection = new(discId, DiscSelectionKind.Unsupported, cuePath, null, null);
            SetSelection(discId, selection);
            return selection;
        }
    }

    public async Task<PatchWorkflowResult> ApplyAsync(
        string outputDirectory,
        IProgress<PatchProgress>? externalProgress = null,
        CancellationToken cancellationToken = default)
    {
        if (stage is PatchWorkflowStage.ApplyingDisc1 or PatchWorkflowStage.ApplyingDisc2)
        {
            return Failure(PatchUserErrorCategory.InvalidSelection, Array.Empty<string>());
        }

        if (disc1.Kind != DiscSelectionKind.Ready || disc2.Kind != DiscSelectionKind.Ready)
        {
            return Failure(PatchUserErrorCategory.InvalidSelection, Array.Empty<string>());
        }

        var completed = new List<string>(2);
        try
        {
            await ApplyDiscAsync("disc1", disc1, outputDirectory, externalProgress, cancellationToken).ConfigureAwait(false);
            completed.Add("disc1");
            await ApplyDiscAsync("disc2", disc2, outputDirectory, externalProgress, cancellationToken).ConfigureAwait(false);
            completed.Add("disc2");
            stage = PatchWorkflowStage.Completed;
            progress = null;
            PublishState();
            return new PatchWorkflowResult(
                true,
                new PatchUserMessage(
                    PatchUserErrorCategory.Success,
                    "Tradução aplicada e verificada. Os arquivos originais não foram alterados."),
                completed);
        }
        catch (OperationCanceledException)
        {
            stage = PatchWorkflowStage.Cancelled;
            progress = null;
            PublishState();
            return new PatchWorkflowResult(false, PatchUserMessages.For(PatchUserErrorCategory.Cancelled, completed), completed);
        }
        catch (Exception exception) when (exception is PatcherException or IOException or UnauthorizedAccessException)
        {
            PatchUserErrorCategory category = PatchUserMessages.Classify(exception);
            stage = PatchWorkflowStage.Failed;
            progress = null;
            PublishState();
            return new PatchWorkflowResult(false, PatchUserMessages.For(category, completed), completed);
        }
    }

    private async Task ApplyDiscAsync(
        string discId,
        DiscSelectionState selection,
        string outputDirectory,
        IProgress<PatchProgress>? externalProgress,
        CancellationToken cancellationToken)
    {
        stage = discId == "disc1" ? PatchWorkflowStage.ApplyingDisc1 : PatchWorkflowStage.ApplyingDisc2;
        progress = null;
        PublishState();
        var combinedProgress = new CallbackProgress<PatchProgress>(value =>
        {
            progress = value;
            externalProgress?.Report(value);
            applyOptions.Progress?.Report(value);
            PublishState();
        });
        PatchApplyOptions requestOptions = applyOptions with { Progress = combinedProgress };
        await PatchBundleApplier.ApplyAsync(
            manifest,
            new BundleApplyRequest(
                discId,
                selection.BinPath!,
                selection.CuePath!,
                dataRoot.PatchRootPath,
                outputDirectory),
            requestOptions,
            cancellationToken).ConfigureAwait(false);
    }

    private DiscSelectionState ToSelection(
        string requestedDiscId,
        string cuePath,
        string binPath,
        SourcePairIdentification identification)
    {
        DiscSelectionKind kind = identification.Kind switch
        {
            SourcePairRecognitionKind.ExactPair when identification.DiscId == requestedDiscId => DiscSelectionKind.Ready,
            SourcePairRecognitionKind.ExactPair => DiscSelectionKind.WrongDisc,
            SourcePairRecognitionKind.MixedDiscPair => DiscSelectionKind.MixedDiscFiles,
            _ => DiscSelectionKind.Unsupported,
        };
        return new DiscSelectionState(requestedDiscId, kind, cuePath, binPath, identification.DiscId);
    }

    private void EnsureKnownDisc(string discId)
    {
        if (!manifest.Discs.ContainsKey(discId))
        {
            throw new ArgumentException("The requested UI disc is not present in the release manifest.", nameof(discId));
        }
    }

    private void SetSelection(string discId, DiscSelectionState selection)
    {
        if (discId == "disc1")
        {
            disc1 = selection;
        }
        else
        {
            disc2 = selection;
        }

        PublishState();
    }

    private PatchWorkflowResult Failure(PatchUserErrorCategory category, IReadOnlyList<string> completed) =>
        new(false, PatchUserMessages.For(category, completed), completed);

    private void PublishState() => StateChanged?.Invoke(this, State);

    private sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
