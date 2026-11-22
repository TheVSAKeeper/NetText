using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
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
    [property: JsonPropertyName("bom")] bool Bom)
{
    public static SessionFormat FromTextFormat(TextFormat format) => new(
        format.Encoding.ToString(),
        format.Endings.ToString(),
        format.Encoding is TextEncoding.Utf8Bom or TextEncoding.Utf16Le or TextEncoding.Utf16Be);

    public TextFormat ToTextFormat()
    {
        var enc = Enum.TryParse<TextEncoding>(Encoding, ignoreCase: true, out var pe) && pe != TextEncoding.None
            ? pe
            : TextEncoding.Utf8;
        var eol = Enum.TryParse<LineEndings>(LineEndings, ignoreCase: true, out var pl) && pl != Core.LineEndings.None
            ? pl
            : Core.LineEndings.Lf;
        return new TextFormat(enc, eol);
    }
}

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
    [property: JsonPropertyName("log"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LogBookmark? Log,
    [property: JsonPropertyName("group")] int Group = 0,
    [property: JsonPropertyName("json_disabled")] bool JsonDisabled = false,
    [property: JsonPropertyName("line_count"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ulong? LineCount = null,
    [property: JsonPropertyName("last_worked"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? LastWorked = null)
{
    [JsonPropertyName("closed_at_ms"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? ClosedAtMs { get; init; }

    [JsonPropertyName("auto_refresh"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? AutoRefresh { get; init; }

    [JsonPropertyName("draft_log_mode"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? DraftLogMode { get; init; }

    [JsonPropertyName("draft_offset"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? DraftOffset { get; init; }

    [JsonPropertyName("custom_title"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CustomTitle { get; init; }

    [JsonPropertyName("edit_pages"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? EditPages { get; init; }
}

public sealed record SessionPane(int Group, bool Vertical = false, double Fraction = .5,
    SessionPane? First = null, SessionPane? Second = null);

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
    [property: JsonPropertyName("closed"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] List<SessionTab>? Closed = null,
    [property: JsonPropertyName("panes"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SessionPane? Panes = null,
    [property: JsonPropertyName("group_tabs"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Dictionary<int, int>? GroupTabs = null,
    [property: JsonPropertyName("focused_group"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? FocusedGroup = null);

public sealed record SessionDraftInfo(
    [property: JsonPropertyName("bytes")] int Bytes,
    [property: JsonPropertyName("checksum")] uint Checksum);

public sealed record SessionManifest(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("state")] SessionState State,
    [property: JsonPropertyName("drafts")] List<SessionDraftInfo?> Drafts,
    [property: JsonPropertyName("closed_drafts"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] List<SessionDraftInfo?>? ClosedDrafts = null);

[JsonSerializable(typeof(SessionManifest))]
[JsonSerializable(typeof(SessionState))]
[JsonSerializable(typeof(SessionTab))]
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
    private static readonly ConditionalWeakTable<byte[], SessionDraftInfo> DraftInfo = new();
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

    internal static SessionDraftInfo Describe(byte[] bytes) => DraftInfo.GetValue(bytes,
        payload => new SessionDraftInfo(payload.Length, Crc32.Compute(payload)));

    private static string ComputeStateKey(SessionCapture capture)
    {
        static List<SessionDraftInfo?> Summarize(List<byte[]?> payloads) =>
            payloads.Select(p => p is null ? null : Describe(p)).ToList();

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
    private string? _checkpointStructure;
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

    public string DirectoryPath => _directory;

    public static string DefaultDirectory() =>
        Path.Combine(AppPaths.RequireLocalAppData(), "NetText", "session");

    public static SessionJournal Open() => Open(DefaultDirectory());

    public static SessionJournal Open(string? directory)
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
        var draftInfos = capture.DraftPayloads.Select(bytes => bytes is null ? null : SessionWriter.Describe(bytes)).ToList();
        var closedDraftInfos = closedPayloads.Count == 0 ? null :
            closedPayloads.Select(bytes => bytes is null ? null : SessionWriter.Describe(bytes)).ToList();
        var manifest = new SessionManifest(FormatVersion, stateToWrite, draftInfos, closedDraftInfos);
        string structure = StructureOf(manifest);
        if (_sequence != 0 && _checkpointStructure == structure)
        {
            string view = Path.Combine(_directory, $"{CheckpointPrefix}{_sequence:D20}", "view.json");
            Files.WriteAtomically(FileTarget.FromPicker(view), stream =>
                JsonSerializer.Serialize(stream, stateToWrite, SessionJsonContext.Default.SessionState));
            _committedRevision = revision;
            return;
        }

        ulong nextSeq = _sequence + 1;
        var pendingDir = Path.Combine(_directory, $".pending-{Guid.NewGuid():N}");
        var finalDir = Path.Combine(_directory, $"{CheckpointPrefix}{nextSeq:D20}");
        Directory.CreateDirectory(pendingDir);

        try
        {
            WriteDraftPayloads(pendingDir, "draft", capture.DraftPayloads);
            WriteDraftPayloads(pendingDir, "closed-draft", closedPayloads);
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
            _checkpointStructure = structure;

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

    public static bool IsKnownEmptyUntitledDraft(SessionTab tab, byte[]? draftBytes) =>
        tab.File is null && (draftBytes is null || draftBytes.Length == 0);

    public static bool IsKnownEmptyUntitledDraft(SessionTab tab, string? draftText) =>
        tab.File is null && string.IsNullOrEmpty(draftText);

    public static (List<SessionTab> Tabs, List<byte[]?> Payloads) FilterClosedHistory(
        IReadOnlyList<SessionTab> closedTabs,
        IReadOnlyList<byte[]?> closedPayloads)
    {
        if (closedTabs.Count != closedPayloads.Count)
        {
            throw new ArgumentException("Количество закрытых вкладок не совпадает с количеством черновиков");
        }

        var tabs = new List<SessionTab>(closedTabs.Count);
        var payloads = new List<byte[]?>(closedPayloads.Count);
        for (int i = 0; i < closedTabs.Count; i++)
        {
            if (IsKnownEmptyUntitledDraft(closedTabs[i], closedPayloads[i]))
            {
                continue;
            }

            tabs.Add(closedTabs[i]);
            payloads.Add(closedPayloads[i]);
        }

        return (tabs, payloads);
    }

    public static (List<SessionTab> Tabs, List<string?> Texts) FilterClosedHistory(
        IReadOnlyList<SessionTab> closedTabs,
        IReadOnlyList<string?> closedTexts)
    {
        if (closedTabs.Count != closedTexts.Count)
        {
            throw new ArgumentException("Количество закрытых вкладок не совпадает с количеством текстов черновиков");
        }

        var tabs = new List<SessionTab>(closedTabs.Count);
        var texts = new List<string?>(closedTexts.Count);
        for (int i = 0; i < closedTabs.Count; i++)
        {
            if (IsKnownEmptyUntitledDraft(closedTabs[i], closedTexts[i]))
            {
                continue;
            }

            tabs.Add(closedTabs[i]);
            texts.Add(closedTexts[i]);
        }

        return (tabs, texts);
    }

    private static string StructureOf(SessionManifest manifest)
    {
        var state = manifest.State;
        var tabs = state.Tabs.Select(tab => tab with
        {
            Selection = new SessionSelection(0, 0),
            Reversed = false,
            Scroll = [],
            Log = tab.Log is null ? null : new LogBookmark(),
            LineCount = null,
            LastWorked = null,
            DraftOffset = null,
        }).ToList();
        return JsonSerializer.Serialize(manifest with
        {
            State = state with { Tabs = tabs, Active = 0, Window = new SessionWindow(0, 0, false), GroupTabs = null, FocusedGroup = null },
        }, SessionJsonContext.Default.SessionManifest);
    }

    private static void WriteDraftPayloads(string dir, string prefix, List<byte[]?> payloads)
    {
        for (int i = 0; i < payloads.Count; i++)
        {
            var payload = payloads[i];
            if (payload is null)
            {
                continue;
            }

            var draftPath = Path.Combine(dir, $"{prefix}-{i}.txt");
            using (var fs = new FileStream(draftPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.Write(payload);
                fs.Flush(flushToDisk: true);
            }
        }
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
            manifest.State.Active < 0 ||
            manifest.State.Active >= Math.Max(1, manifest.State.Tabs.Count) ||
            manifest.State.Tabs.Count != manifest.Drafts.Count ||
            closed.Count != closedDraftInfos.Count)
        {
            throw new InvalidDataException("Некорректный манифест сессии");
        }

        var texts = ReadDraftPayloads(dir, "draft", manifest.State.Tabs, manifest.Drafts, skip: 0);
        int excess = Math.Max(0, closed.Count - MaxClosedTabs);
        var closedTexts = ReadDraftPayloads(dir, "closed-draft", closed, closedDraftInfos, skip: excess);
        var savedState = ReadView(dir, manifest);
        var normalizedClosed = excess > 0
            ? closed.GetRange(excess, closed.Count - excess)
            : closed;
        var state = ReferenceEquals(savedState.Closed, normalizedClosed)
            ? savedState
            : savedState with { Closed = normalizedClosed };

        return (state, texts, closedTexts);
    }

    private static SessionState ReadView(string directory, SessionManifest manifest)
    {
        string path = Path.Combine(directory, "view.json");
        if (!File.Exists(path)) return manifest.State;
        try
        {
            var view = JsonSerializer.Deserialize(File.ReadAllBytes(path), SessionJsonContext.Default.SessionState);
            if (view is not null && view.Active >= 0 && view.Active < Math.Max(1, view.Tabs.Count) &&
                StructureOf(manifest with { State = view }) == StructureOf(manifest)) return view;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return manifest.State;
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
