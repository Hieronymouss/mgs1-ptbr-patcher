namespace Mgs1.Patcher.Core;

public class PatcherException : Exception
{
    public PatcherException(string message) : base(message) { }

    public PatcherException(string message, Exception innerException)
        : base(message, innerException) { }
}

public sealed class PatcherIntegrityException : PatcherException
{
    public PatcherIntegrityException(string message) : base(message) { }

    public PatcherIntegrityException(string message, Exception innerException)
        : base(message, innerException) { }
}

public sealed class PatcherManifestException : PatcherException
{
    public PatcherManifestException(string message) : base(message) { }

    public PatcherManifestException(string message, Exception innerException)
        : base(message, innerException) { }
}

public sealed class PatcherSafetyException : PatcherException
{
    public PatcherSafetyException(string message) : base(message) { }

    public PatcherSafetyException(string message, Exception innerException)
        : base(message, innerException) { }
}
