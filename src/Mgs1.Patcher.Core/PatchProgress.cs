namespace Mgs1.Patcher.Core;

public enum PatchProgressPhase
{
    ValidatingInputs,
    ValidatingPatches,
    Preflight,
    ApplyingBin,
    ApplyingCue,
    ReverifyingInputs,
    Publishing,
    Completed,
}

public sealed record PatchProgress(
    PatchProgressPhase Phase,
    string Item,
    long CompletedBytes,
    long TotalBytes);

public sealed record PatchApplyOptions
{
    public const int DefaultIoBufferSize = 8 * 1024 * 1024;
    public const long DefaultFreeSpaceReserveBytes = 2L * 1024 * 1024 * 1024;

    public int IoBufferSize { get; init; } = DefaultIoBufferSize;

    public long FreeSpaceReserveBytes { get; init; } = DefaultFreeSpaceReserveBytes;

    public IProgress<PatchProgress>? Progress { get; init; }

    internal void Validate()
    {
        if (IoBufferSize is < 4096 or > DefaultIoBufferSize)
        {
            throw new PatcherSafetyException("I/O buffer size must be between 4 KiB and 8 MiB.");
        }

        if (FreeSpaceReserveBytes < 0)
        {
            throw new PatcherSafetyException("Free-space reserve cannot be negative.");
        }
    }
}
