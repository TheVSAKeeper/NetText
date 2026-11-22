using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetText.Core;

public sealed record RecentFile(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("windows_1251")] bool Windows1251,
    [property: JsonPropertyName("view_as_log")] bool ViewAsLog = false,
    [property: JsonPropertyName("opened_at")] DateTimeOffset? OpenedAt = null,
    [property: JsonPropertyName("lines")] ulong? Lines = null,
    [property: JsonPropertyName("position")] SessionTab? Position = null,
    [property: JsonPropertyName("edit_pages"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? EditPages = null)
{
    public static RecentFile From(string path, TextEncoding encoding, bool viewAsLog = false, bool? editPages = null) =>
        new(path, encoding == TextEncoding.Windows1251, viewAsLog, DateTimeOffset.UtcNow, EditPages: editPages);
}

[JsonSerializable(typeof(List<RecentFile>))]
internal partial class RecentJsonContext : JsonSerializerContext;

public sealed class RecentFiles
{
    public const int MaxFiles = 16;
    public const int MaxHistoryFiles = 10_000;

    private readonly string? _path;
    private readonly List<RecentFile> _entries;
    private readonly List<RecentFile> _pending;
    private readonly HashSet<string> _pendingRemovals;

    private RecentFiles(string? path, List<RecentFile> entries, List<RecentFile> pending, HashSet<string>? pendingRemovals = null)
    {
        _path = path;
        _entries = entries;
        _pending = pending;
        _pendingRemovals = pendingRemovals ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    public RecentFiles(string? path = null)
        : this(path, [], [])
    {
    }

    public IReadOnlyList<RecentFile> Entries => _entries.Count <= MaxFiles ? _entries : _entries.GetRange(0, MaxFiles);
    public IReadOnlyList<RecentFile> History => _entries;

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

    public RecentFiles Clone() => new(_path, [.. _entries], [.. _pending], new HashSet<string>(_pendingRemovals, StringComparer.OrdinalIgnoreCase));

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
            _pendingRemovals.Remove(file.Path);
            MoveToFront(_entries, file);
            MoveToFront(_pending, file);
        }

        if (_entries.Count > MaxHistoryFiles)
        {
            _entries.RemoveRange(MaxHistoryFiles, _entries.Count - MaxHistoryFiles);
        }

        Persist([]);
    }

    public bool Remove(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        bool removed = _entries.RemoveAll(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase)) > 0;
        _pending.RemoveAll(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase));
        if (_path is null)
        {
            return removed;
        }

        _pendingRemovals.Add(path);
        Persist([]);
        return removed;
    }

    public void RecordPositions(IReadOnlyList<RecentFile> files) => Persist(files);

    private void Persist(IReadOnlyList<RecentFile> positions)
    {
        if (_pending.Count == 0 && _pendingRemovals.Count == 0 && positions.Count == 0)
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

        if (_pendingRemovals.Count > 0)
        {
            entries.RemoveAll(e => _pendingRemovals.Contains(e.Path));
        }

        for (int i = _pending.Count - 1; i >= 0; i--)
        {
            MoveToFront(entries, _pending[i]);
        }

        foreach (var position in positions)
        {
            int index = entries.FindIndex(item => item.ViewAsLog == position.ViewAsLog &&
                string.Equals(item.Path, position.Path, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                entries[index] = entries[index] with
                {
                    Lines = position.Lines ?? entries[index].Lines,
                    Position = position.Position,
                    EditPages = position.EditPages ?? position.Position?.EditPages ?? entries[index].EditPages,
                };
            }
        }

        if (entries.Count > MaxHistoryFiles)
        {
            entries.RemoveRange(MaxHistoryFiles, entries.Count - MaxHistoryFiles);
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
        _pendingRemovals.Clear();
    }

    internal static FileStream AcquireLock(string lockPath)
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
        var previous = entries.Find(e => string.Equals(e.Path, file.Path, StringComparison.OrdinalIgnoreCase));
        if (previous is not null)
        {
            entries.Remove(previous);
            file = file with
            {
                Lines = file.Lines ?? previous.Lines,
                Position = file.Position ?? previous.Position,
                EditPages = file.EditPages ?? file.Position?.EditPages ?? previous.EditPages,
            };
        }
        else if (file.EditPages is null && file.Position?.EditPages is { } posEditPages)
        {
            file = file with { EditPages = posEditPages };
        }
        entries.Insert(0, file);
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
            return entries;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Некорректный JSON истории", ex);
        }
    }
}
