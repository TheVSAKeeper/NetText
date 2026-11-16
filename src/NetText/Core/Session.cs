using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;

namespace NetText.Core;

public sealed record SessionVersion(
    [property: JsonPropertyName("bytes")] long Bytes,
    [property: JsonPropertyName("modified")] DateTime ModifiedUtc,
    [property: JsonPropertyName("created")] DateTime CreatedUtc);

public sealed record SessionFileTarget(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("version")] SessionVersion? Version);

public sealed record SessionFormat(
    [property: JsonPropertyName("encoding")] string Encoding,
    [property: JsonPropertyName("line_endings")] string LineEndings,
    [property: JsonPropertyName("bom")] bool Bom);

public sealed record SessionSelection(
    [property: JsonPropertyName("start")] int Start,
    [property: JsonPropertyName("end")] int End);

public sealed record SessionTab(
    [property: JsonPropertyName("id")] ulong Id,
    [property: JsonPropertyName("file")] SessionFileTarget? File,
    [property: JsonPropertyName("format")] SessionFormat Format,
    [property: JsonPropertyName("selection")] SessionSelection Selection,
    [property: JsonPropertyName("reversed")] bool Reversed,
    [property: JsonPropertyName("scroll")] float[] Scroll,
    [property: JsonPropertyName("draft_revision")] ulong? DraftRevision,
    [property: JsonPropertyName("log"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LogBookmark? Log);

public sealed record SessionWindow(
    [property: JsonPropertyName("width")] float Width,
    [property: JsonPropertyName("height")] float Height,
    [property: JsonPropertyName("maximized")] bool Maximized);

public sealed record SessionState(
    [property: JsonPropertyName("tabs")] List<SessionTab> Tabs,
    [property: JsonPropertyName("active")] int Active,
    [property: JsonPropertyName("window")] SessionWindow Window,
    [property: JsonPropertyName("wrap")] bool Wrap,
    [property: JsonPropertyName("font_size")] float FontSize,
    [property: JsonPropertyName("closed"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] List<SessionTab>? Closed = null);

public sealed record SessionDraftInfo(
    [property: JsonPropertyName("bytes")] int Bytes,
    [property: JsonPropertyName("checksum")] uint Checksum);

public sealed record SessionManifest(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("state")] SessionState State,
    [property: JsonPropertyName("drafts")] List<SessionDraftInfo?> Drafts,
    [property: JsonPropertyName("closed_drafts"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] List<SessionDraftInfo?>? ClosedDrafts = null);

[JsonSerializable(typeof(SessionManifest))]
internal partial class SessionJsonContext : JsonSerializerContext;

public sealed record SessionCapture(
    SessionState State,
    List<byte[]?> DraftPayloads,
    List<byte[]?>? ClosedDraftPayloads = null);

public sealed record RecoveredSession(
    SessionState State,
    List<string?> DraftTexts,
    List<string?> ClosedDraftTexts,
    string? Warning);

public sealed class SessionWriter
{
    private ulong _revision;
    private bool _inFlight;
    private string? _lastSavedKey;

    public (ulong Revision, SessionCapture Capture)? Queue(SessionCapture capture)
    {
        var key = ComputeStateKey(capture);
        if (!_inFlight && _lastSavedKey == key)
        {
            return null;
        }

        _revision++;
        _inFlight = true;
        return (_revision, capture);
    }

    public void Complete(ulong revision, SessionCapture capture, bool success)
    {
        if (revision == _revision)
        {
            _inFlight = false;
        }
        if (success)
        {
            _lastSavedKey = ComputeStateKey(capture);
        }
    }

    public bool IsInFlight => _inFlight;

    private static string ComputeStateKey(SessionCapture capture)
    {
        static List<SessionDraftInfo?> Summarize(List<byte[]?> payloads) =>
            payloads.Select(p => p is null ? null : new SessionDraftInfo(p.Length, Crc32.Compute(p))).ToList();

        var manifest = new SessionManifest(
            1,
            capture.State,
            Summarize(capture.DraftPayloads),
            capture.ClosedDraftPayloads is { Count: > 0 } ? Summarize(capture.ClosedDraftPayloads) : null);
        return JsonSerializer.Serialize(manifest, SessionJsonContext.Default.SessionManifest);
    }
}

public sealed class SessionJournal : IDisposable
{
    public const int FormatVersion = 1;
    public const int MaxClosedTabs = 100;
    private const string CheckpointPrefix = "checkpoint-";

    private readonly string _directory;
    private readonly FileStream _lockStream;
    private ulong _sequence;
    private ulong _committedRevision;

    private SessionJournal(string directory, FileStream lockStream, ulong sequence)
    {
        _directory = directory;
        _lockStream = lockStream;
        _sequence = sequence;
    }

    public static string DefaultDirectory() =>
        Path.Combine(AppPaths.RequireLocalAppData(), "NetText", "session");

    public static SessionJournal Open(string? directory = null)
    {
        var dir = directory ?? DefaultDirectory();
        Directory.CreateDirectory(dir);
        var lockPath = Path.Combine(dir, "session.lock");
        FileStream lockStream;
        try
        {
            lockStream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex)
        {
            throw new IOException("Сессия уже открыта в другом окне NetText", ex);
        }

        ulong maxSeq = 0;
        foreach (var (seq, _) in ListCheckpoints(dir))
        {
            if (seq > maxSeq) maxSeq = seq;
        }
        return new SessionJournal(dir, lockStream, maxSeq);
    }

    public RecoveredSession? Restore()
    {
        var checkpoints = ListCheckpoints(_directory);
        if (checkpoints.Count == 0)
        {
            return null;
        }

        string? firstCorruptError = null;
        for (int i = 0; i < checkpoints.Count; i++)
        {
            var (_, path) = checkpoints[i];
            try
            {
                var (state, drafts, closedDrafts) = ReadCheckpoint(path);
                string? warning = firstCorruptError is null
                    ? null
                    : $"Последняя запись сессии повреждена. Восстановлена предыдущая. {firstCorruptError}";
                return new RecoveredSession(state, drafts, closedDrafts, warning);
            }
            catch (Exception ex)
            {
                firstCorruptError ??= ex.Message;
            }
        }

        throw new InvalidDataException($"Не удалось восстановить сессию. {firstCorruptError}");
    }

    public void Commit(ulong revision, SessionCapture capture)
    {
        if (revision <= _committedRevision)
        {
            return;
        }

        var closedTabs = capture.State.Closed ?? [];
        var closedPayloads = capture.ClosedDraftPayloads ?? [];
        if (closedTabs.Count != closedPayloads.Count)
        {
            throw new ArgumentException("Количество закрытых вкладок не совпадает с количеством черновиков");
        }

        if (_sequence == ulong.MaxValue)
        {
            throw new IOException("Номер записи сессии исчерпан");
        }

        int excess = Math.Max(0, closedTabs.Count - MaxClosedTabs);
        if (excess > 0)
        {
            closedTabs = closedTabs.GetRange(excess, closedTabs.Count - excess);
            closedPayloads = closedPayloads.GetRange(excess, closedPayloads.Count - excess);
        }

        var stateToWrite = capture.State with { Closed = closedTabs.Count > 0 ? closedTabs : null };

        ulong nextSeq = _sequence + 1;
        var pendingDir = Path.Combine(_directory, $".pending-{Guid.NewGuid():N}");
        var finalDir = Path.Combine(_directory, $"{CheckpointPrefix}{nextSeq:D20}");
        Directory.CreateDirectory(pendingDir);

        try
        {
            var draftInfos = WriteDraftPayloads(pendingDir, "draft", capture.DraftPayloads);
            var closedDraftInfos = closedPayloads.Count > 0
                ? WriteDraftPayloads(pendingDir, "closed-draft", closedPayloads)
                : null;

            var manifest = new SessionManifest(FormatVersion, stateToWrite, draftInfos, closedDraftInfos);
            var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, SessionJsonContext.Default.SessionManifest);
            var manifestPath = Path.Combine(pendingDir, "manifest.json");
            using (var fs = new FileStream(manifestPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.Write(manifestBytes);
                fs.Flush(flushToDisk: true);
            }

            Directory.Move(pendingDir, finalDir);
            _sequence = nextSeq;
            _committedRevision = revision;

            PruneOldCheckpoints(keepCount: 2);
        }
        finally
        {
            if (Directory.Exists(pendingDir))
            {
                try { Directory.Delete(pendingDir, recursive: true); } catch { }
            }
        }
    }

    private static List<SessionDraftInfo?> WriteDraftPayloads(string dir, string prefix, List<byte[]?> payloads)
    {
        var infos = new List<SessionDraftInfo?>(payloads.Count);
        for (int i = 0; i < payloads.Count; i++)
        {
            var payload = payloads[i];
            if (payload is null)
            {
                infos.Add(null);
                continue;
            }

            var draftPath = Path.Combine(dir, $"{prefix}-{i}.txt");
            using (var fs = new FileStream(draftPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.Write(payload);
                fs.Flush(flushToDisk: true);
            }
            infos.Add(new SessionDraftInfo(payload.Length, Crc32.Compute(payload)));
        }
        return infos;
    }

    private void PruneOldCheckpoints(int keepCount)
    {
        var list = ListCheckpoints(_directory);
        for (int i = keepCount; i < list.Count; i++)
        {
            try
            {
                Directory.Delete(list[i].Path, recursive: true);
            }
            catch
            {
            }
        }
    }

    private static (SessionState State, List<string?> Drafts, List<string?> ClosedDrafts) ReadCheckpoint(string dir)
    {
        var manifestPath = Path.Combine(dir, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            throw new InvalidDataException("Некорректный манифест сессии");
        }

        var manifestBytes = File.ReadAllBytes(manifestPath);
        var manifest = JsonSerializer.Deserialize(manifestBytes, SessionJsonContext.Default.SessionManifest)
            ?? throw new InvalidDataException("Некорректный манифест сессии");

        var closed = manifest.State.Closed ?? [];
        var closedDraftInfos = manifest.ClosedDrafts ?? [];

        if (manifest.Version != FormatVersion ||
            manifest.State.Tabs.Count == 0 ||
            manifest.State.Active < 0 ||
            manifest.State.Active >= manifest.State.Tabs.Count ||
            manifest.State.Tabs.Count != manifest.Drafts.Count ||
            closed.Count != closedDraftInfos.Count)
        {
            throw new InvalidDataException("Некорректный манифест сессии");
        }

        var texts = ReadDraftPayloads(dir, "draft", manifest.State.Tabs, manifest.Drafts, skip: 0);
        int excess = Math.Max(0, closed.Count - MaxClosedTabs);
        var closedTexts = ReadDraftPayloads(dir, "closed-draft", closed, closedDraftInfos, skip: excess);
        var normalizedClosed = excess > 0
            ? closed.GetRange(excess, closed.Count - excess)
            : closed;
        var state = ReferenceEquals(manifest.State.Closed, normalizedClosed)
            ? manifest.State
            : manifest.State with { Closed = normalizedClosed };

        return (state, texts, closedTexts);
    }

    private static List<string?> ReadDraftPayloads(
        string dir,
        string prefix,
        List<SessionTab> tabs,
        List<SessionDraftInfo?> draftInfos,
        int skip)
    {
        var texts = new List<string?>(Math.Max(0, draftInfos.Count - skip));
        for (int i = skip; i < draftInfos.Count; i++)
        {
            var tab = tabs[i];
            var info = draftInfos[i];
            if ((info is not null) != (tab.DraftRevision is not null))
            {
                throw new InvalidDataException("Отсутствует описание черновика");
            }

            if (info is null)
            {
                texts.Add(null);
                continue;
            }

            var draftPath = Path.Combine(dir, $"{prefix}-{i}.txt");
            if (!File.Exists(draftPath))
            {
                throw new InvalidDataException("Повреждён снимок черновика");
            }

            var bytes = File.ReadAllBytes(draftPath);
            if (bytes.Length != info.Bytes ||
                Crc32.Compute(bytes) != info.Checksum ||
                !Utf8.IsValid(bytes))
            {
                throw new InvalidDataException("Повреждён снимок черновика");
            }

            texts.Add(Encoding.UTF8.GetString(bytes));
        }

        return texts;
    }

    private static List<(ulong Seq, string Path)> ListCheckpoints(string dir)
    {
        if (!Directory.Exists(dir))
        {
            return [];
        }

        var result = new List<(ulong Seq, string Path)>();
        foreach (var sub in Directory.EnumerateDirectories(dir))
        {
            var name = Path.GetFileName(sub);
            if (name.StartsWith(CheckpointPrefix, StringComparison.Ordinal) &&
                name.Length == CheckpointPrefix.Length + 20 &&
                ulong.TryParse(name.AsSpan(CheckpointPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out ulong seq))
            {
                result.Add((seq, sub));
            }
        }

        result.Sort((a, b) => b.Seq.CompareTo(a.Seq));
        return result;
    }

    public void Dispose()
    {
        _lockStream.Dispose();
    }
}
