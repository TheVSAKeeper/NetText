using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetText.Core;

public sealed record RecentFile(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("windows_1251")] bool Windows1251,
    [property: JsonPropertyName("view_as_log")] bool ViewAsLog = false)
{
    public static RecentFile From(string path, TextEncoding encoding, bool viewAsLog = false) =>
        new(path, encoding == TextEncoding.Windows1251, viewAsLog);
}

[JsonSerializable(typeof(List<RecentFile>))]
internal partial class RecentJsonContext : JsonSerializerContext;

public sealed class RecentFiles
{
    public const int MaxFiles = 16;

    private readonly string? _path;
    private readonly List<RecentFile> _entries;
    private readonly List<RecentFile> _pending;

    private RecentFiles(string? path, List<RecentFile> entries, List<RecentFile> pending)
    {
        _path = path;
        _entries = entries;
        _pending = pending;
    }

    public RecentFiles(string? path = null)
        : this(path, [], [])
    {
    }

    public IReadOnlyList<RecentFile> Entries => _entries;

    public static (RecentFiles Recent, Exception? Error) Load()
    {
        var localAppData = AppPaths.GetLocalAppData();
        string? path = string.IsNullOrEmpty(localAppData)
            ? null
            : Path.Combine(localAppData, "NetText", "recent-files.json");

        var recent = new RecentFiles(path);
        try
        {
            recent.Reload();
            return (recent, null);
        }
        catch (Exception ex)
        {
            return (recent, ex);
        }
    }

    public RecentFiles Clone() => new(_path, [.. _entries], [.. _pending]);

    public void Reload()
    {
        if (_path is null)
        {
            return;
        }

        _entries.Clear();
        _entries.AddRange(ReadEntries(_path));
    }

    public void Record(IEnumerable<RecentFile> files)
    {
        foreach (var file in files)
        {
            MoveToFront(_entries, file);
            MoveToFront(_pending, file);
        }

        if (_pending.Count == 0)
        {
            return;
        }

        if (_path is null)
        {
            throw new IOException("Папка LOCALAPPDATA недоступна");
        }

        var directory = Path.GetDirectoryName(_path)
            ?? throw new IOException("recent history has a parent directory");
        Directory.CreateDirectory(directory);

        using var lockStream = AcquireLock(Path.Combine(directory, "recent-files.lock"));

        List<RecentFile> entries;
        try
        {
            entries = ReadEntries(_path);
        }
        catch (InvalidDataException)
        {
            entries = [];
        }

        for (int i = _pending.Count - 1; i >= 0; i--)
        {
            MoveToFront(entries, _pending[i]);
        }

        var tempPath = Path.Combine(directory, $".recent-{Guid.NewGuid():N}.tmp");
        try
        {
            var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(entries, RecentJsonContext.Default.ListRecentFile);
            using (var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.Write(jsonBytes);
                fs.Flush(flushToDisk: true);
            }
            File.Move(tempPath, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch
                {
                }
            }
        }

        _entries.Clear();
        _entries.AddRange(entries);
        _pending.Clear();
    }

    private static FileStream AcquireLock(string lockPath)
    {
        var deadline = Environment.TickCount64 + 3000;
        while (true)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (Environment.TickCount64 < deadline)
            {
                Thread.Sleep(10);
            }
        }
    }

    private static void MoveToFront(List<RecentFile> entries, RecentFile file)
    {
        entries.RemoveAll(e => string.Equals(e.Path, file.Path, StringComparison.OrdinalIgnoreCase));
        entries.Insert(0, file);
        if (entries.Count > MaxFiles)
        {
            entries.RemoveRange(MaxFiles, entries.Count - MaxFiles);
        }
    }

    private static List<RecentFile> ReadEntries(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        var bytes = File.ReadAllBytes(path);
        try
        {
            var entries = JsonSerializer.Deserialize(bytes, RecentJsonContext.Default.ListRecentFile) ?? [];
            if (entries.Count > MaxFiles)
            {
                entries.RemoveRange(MaxFiles, entries.Count - MaxFiles);
            }
            return entries;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Некорректный JSON истории", ex);
        }
    }
}
