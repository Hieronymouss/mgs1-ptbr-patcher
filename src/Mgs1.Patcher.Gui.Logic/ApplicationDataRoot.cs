namespace Mgs1.Patcher.Gui.Logic;

/// <summary>
/// App-owned, read-only release data. Production installs place the manifest
/// and payload directory directly below the application's data directory.
/// </summary>
public sealed record ApplicationDataRoot(string RootPath, string ManifestPath, string PatchRootPath)
{
    public static ApplicationDataRoot Resolve(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length != 0)
        {
            throw new ApplicationDataException("This release does not accept launch arguments.");
        }

        try
        {
            string root = Path.Combine(AppContext.BaseDirectory, "data");
            string fullRoot = Path.GetFullPath(root);
            return new ApplicationDataRoot(
                fullRoot,
                Path.Combine(fullRoot, "release-manifest.json"),
                Path.Combine(fullRoot, "patches"));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ApplicationDataException("The application data location is invalid.", exception);
        }
    }
}

public sealed class ApplicationDataException : Exception
{
    public ApplicationDataException(string message) : base(message) { }

    public ApplicationDataException(string message, Exception innerException) : base(message, innerException) { }
}
