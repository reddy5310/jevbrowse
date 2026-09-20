namespace JevBrowse.Storage;

/// <summary>
/// Owns throwaway profile directories. An exclusive sibling lock keeps another app instance's startup sweep
/// from touching an active session. Failed deletion remains on disk for retry here or on the next startup.
/// </summary>
public sealed class EphemeralProfileStore : IDisposable
{
    private readonly string _root;
    private readonly Dictionary<string, FileStream> _owners = [];

    public EphemeralProfileStore(string root)
    {
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
    }

    public string Create()
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        var owner = Lock(path);
        try { Directory.CreateDirectory(path); _owners.Add(path, owner); }
        catch { owner.Dispose(); throw; }
        return path;
    }

    private string Validate(string path)
    {
        path = Path.GetFullPath(path);
        if (!string.Equals(Path.GetDirectoryName(path), _root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Profile cleanup must stay within the ephemeral profile directory.");
        if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Profile cleanup cannot follow a directory link.");
        return path;
    }

    private static FileStream Lock(string path) => new(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    public bool TryDelete(string path)
    {
        path = Validate(path);
        if (_owners.ContainsKey(path)) return false;
        try
        {
            using (var owner = Lock(path))
            {
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            }
            File.Delete(path + ".lock");
            return !Directory.Exists(path);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public async Task<bool> EndAsync(string path)
    {
        path = Validate(path);
        if (_owners.Remove(path, out var owner)) owner.Dispose();
        // The caller must first wait for the engine to exit; antivirus or another file reader can still hold a lock.
        for (var attempt = 0; attempt < 30; attempt++)
        {
            if (TryDelete(path)) return true;
            await Task.Delay(100);
        }
        return false;
    }

    public void Sweep()
    {
        foreach (var path in Directory.GetDirectories(_root))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
            TryDelete(path);
        }
    }

    public void Dispose()
    {
        var paths = _owners.Keys.ToArray();
        foreach (var owner in _owners.Values) owner.Dispose();
        _owners.Clear();
        foreach (var path in paths) TryDelete(path);
    }
}
