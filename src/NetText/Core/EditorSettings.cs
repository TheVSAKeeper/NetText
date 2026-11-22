using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetText.Core;

internal sealed record EditorSettings(int UntitledLimit = 10, bool Minimap = true,
    long VersionFileBytes = 5 * 1024 * 1024, long VersionTotalBytes = 100 * 1024 * 1024)
{
    public string Theme { get; init; } = "focus";
    public float FontSize { get; init; } = 12f;
    public bool WordWrap { get; init; }
    public bool? OpenLargeFilesAsLog { get; init; }
    public WorkspaceStartupMode WorkspaceStartupMode { get; init; } = WorkspaceStartupMode.LastUsed;
    public string? StartupWorkspaceId { get; init; }
    public int WorkspaceHistoryLimit { get; init; } = 10;
    public bool ShowDraftLineCount { get; init; } = true;
    public int ArchiveClosedDraftAfterMinutes { get; init; } = 300;
    public int ArchivedDraftRetentionDays { get; init; } = 30;
    public bool AutoCheckUpdates { get; init; } = true;
    public bool AutoCheckUpdatesChoiceMade { get; init; }

    private static string SettingsPath => Path.Combine(AppPaths.RequireLocalAppData(), "NetText", "settings.json");

    public static EditorSettings Load() => Load(SettingsPath);

    public static EditorSettings Load(string path)
    {
        if (!File.Exists(path)) return new();
        var settings = JsonSerializer.Deserialize(File.ReadAllBytes(path), EditorSettingsJson.Default.EditorSettings) ?? new();
        return settings.Normalize().MigrateLegacyUpdateChoice();
    }

    public void Save() => Save(SettingsPath);

    public void Save(string path)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var normalized = Normalize();
        Files.WriteAtomically(FileTarget.FromPicker(fullPath), stream =>
            JsonSerializer.Serialize(stream, normalized, EditorSettingsJson.Default.EditorSettings));
    }

    public EditorSettings Normalize() => this with
    {
        UntitledLimit = Math.Clamp(UntitledLimit, 1, 200),
        VersionFileBytes = Math.Clamp(VersionFileBytes, 0, Files.MaxEditorBytes),
        VersionTotalBytes = Math.Max(0, VersionTotalBytes),
        Theme = string.IsNullOrWhiteSpace(Theme) ? "focus" : Theme.Trim(),
        FontSize = float.IsFinite(FontSize) ? Math.Clamp(FontSize, 8f, 48f) : 12f,
        WorkspaceStartupMode = Enum.IsDefined(WorkspaceStartupMode) && WorkspaceStartupMode != WorkspaceStartupMode.None
            ? WorkspaceStartupMode
            : WorkspaceStartupMode.LastUsed,
        StartupWorkspaceId = string.IsNullOrWhiteSpace(StartupWorkspaceId) ? null : StartupWorkspaceId.Trim(),
        WorkspaceHistoryLimit = Math.Clamp(WorkspaceHistoryLimit, -1, 200),
        ArchiveClosedDraftAfterMinutes = Math.Clamp(ArchiveClosedDraftAfterMinutes, 0, 525_600),
        ArchivedDraftRetentionDays = Math.Clamp(ArchivedDraftRetentionDays, 0, 3650),
    };

    public EditorSettings MigrateLegacyUpdateChoice() =>
        !AutoCheckUpdates && !AutoCheckUpdatesChoiceMade
            ? this with { AutoCheckUpdates = true, AutoCheckUpdatesChoiceMade = true }
            : this;

    public string UntitledName(ReadOnlySpan<byte> firstLine)
    {
        var name = new StringBuilder();
        int count = 0;
        foreach (var rune in Encoding.UTF8.GetString(firstLine).Trim().EnumerateRunes())
        {
            if (count++ >= UntitledLimit) break;
            name.Append(rune.ToString());
        }
        return name.Length == 0 ? "Без имени" : name.ToString();
    }
}

[JsonSerializable(typeof(EditorSettings))]
internal partial class EditorSettingsJson : JsonSerializerContext;
