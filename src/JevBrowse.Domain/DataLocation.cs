namespace JevBrowse.Domain;

/// <summary>
/// The ONE answer to "where is this browser's data", used by startup, storage and the single-instance guard. They used to compute it separately and disagreed
/// (the guard used the program's folder, storage used %LOCALAPPDATA%), so two copies of the program could open the same database.
/// </summary>
public static class DataLocation
{
    public static string Resolve(string? overrideDir, string localAppData) =>
        Path.GetFullPath(string.IsNullOrWhiteSpace(overrideDir) ? Path.Combine(localAppData, "JevBrowse") : overrideDir);

    public static string Resolve() =>
        Resolve(Environment.GetEnvironmentVariable("JEVBROWSE_DATA_DIR"), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
}

/// <summary>A download's metadata may be written to disk only when its owner is known and ordinary; an unknown owner is treated as a Private session.</summary>
public static class DownloadOwnership
{
    public static bool MayPersist(IdentityContainer? container) => container is { } c && !c.IsEphemeral();
}

/// <summary>
/// Holds one exclusive lock file per data folder for the life of the process, whatever the operating system's app-instance service says. If it cannot be taken, another
/// process is using this data and this one must not open it.
/// </summary>
public sealed class InstanceLock : IDisposable
{
    private FileStream? _stream;
    private InstanceLock(FileStream s) => _stream = s;

    public static InstanceLock? TryAcquire(string dataDir)
    {
        try
        {
            Directory.CreateDirectory(dataDir);
            return new InstanceLock(new FileStream(Path.Combine(dataDir, "instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose));
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public void Dispose() { _stream?.Dispose(); _stream = null; }
}
