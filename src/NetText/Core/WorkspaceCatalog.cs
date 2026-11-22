using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetText.Core;

public enum WorkspaceStartupMode
{
    None = 0,
    LastUsed = 1,
    Specific = 2,
    Empty = 3,
}

public sealed record WorkspaceEntry(string Id, string Name, long LastUsedMs = 0)
{
    public string? Theme { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Symbol { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Color { get; init; }
}

public sealed record DeletedWorkspaceEntry(WorkspaceEntry Workspace, long DeletedAtMs, string DirectoryName);

public sealed record WorkspaceCatalog(
    List<WorkspaceEntry> Workspaces,
    Dictionary<string, string> WindowAssignments,
    List<DeletedWorkspaceEntry>? DeletedWorkspaces = null)
{
    public static WorkspaceCatalog CreateDefault() => new(
        [new WorkspaceEntry(WorkspaceCatalogService.DefaultWorkspaceId, WorkspaceCatalogService.DefaultWorkspaceName)],
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [WorkspaceCatalogService.PrimaryWindowId] = WorkspaceCatalogService.DefaultWorkspaceId,
        });
}

public sealed class WorkspaceCatalogService
{
    public const string DefaultWorkspaceId = "default";
    public const string DefaultWorkspaceName = "По умолчанию";
    public const string PrimaryWindowId = "primary";
    public const string EmptyStartupWindowId = "window:fffffffffffffffffffffffffffffff0";

    private const int MaximumWorkspaceCount = 200;
    private const int MaximumNameLength = 80;

    private readonly string _profileBaseDirectory;
    private readonly string _directory;
    private readonly string _indexPath;
    private readonly string _moveMarker;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public WorkspaceCatalogService(string profileBaseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileBaseDirectory);
        _profileBaseDirectory = Path.GetFullPath(profileBaseDirectory);
        _directory = Path.Combine(_profileBaseDirectory, "workspaces");
        _indexPath = Path.Combine(_directory, "index.json");
        _moveMarker = Path.Combine(_directory, "pending-directory-move");
    }

    public string DirectoryPath => _directory;

    public static string GetProfileRoot() =>
        Path.Combine(AppPaths.RequireLocalAppData(), "NetText");

    public static WorkspaceCatalogService OpenDefault() =>
        new(GetProfileRoot());

    public static string GetSessionDirectory(string profileBaseDirectory, string workspaceId, string windowId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileBaseDirectory);
        var profile = Path.GetFullPath(profileBaseDirectory);
        var normalizedWorkspace = NormalizeWorkspaceId(workspaceId);
        var windowDirectory = ResolveWindowDirectoryName(windowId);

        if (normalizedWorkspace == DefaultWorkspaceId && windowId == PrimaryWindowId)
        {
            return Path.Combine(profile, "session");
        }

        return Path.Combine(profile, "workspaces", normalizedWorkspace, "windows", windowDirectory, "session");
    }

    public string GetSessionDirectory(string workspaceId, string windowId) =>
        GetSessionDirectory(_profileBaseDirectory, workspaceId, windowId);

    public string GetWorkspaceDirectory(string id)
    {
        if (id == DefaultWorkspaceId)
        {
            return Path.Combine(_directory, DefaultWorkspaceId);
        }

        if (!Guid.TryParseExact(id, "N", out _))
        {
            throw new InvalidDataException("Идентификатор рабочего пространства некорректен.");
        }

        return Path.Combine(_directory, id);
    }

    public async Task<WorkspaceCatalog> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var catalogLock = await LockCatalogAsync(cancellationToken).ConfigureAwait(false);
            return await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<WorkspaceEntry> CreateAsync(
        string name,
        string? theme = null,
        CancellationToken cancellationToken = default,
        string? symbol = null,
        string? color = null) =>
        (await CreateWithCatalogAsync(name, theme, cancellationToken, symbol, color).ConfigureAwait(false)).Entry;

    public async Task<(WorkspaceEntry Entry, WorkspaceCatalog Catalog)> CreateWithCatalogAsync(
        string name,
        string? theme = null,
        CancellationToken cancellationToken = default,
        string? symbol = null,
        string? color = null)
    {
        name = ValidateName(name);
        symbol = ValidateSymbol(symbol);
        color = ValidateColor(color);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var catalogLock = await LockCatalogAsync(cancellationToken).ConfigureAwait(false);
            var catalog = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            if (catalog.Workspaces.Count >= MaximumWorkspaceCount)
            {
                throw new InvalidOperationException("Достигнут предел в 200 рабочих пространств.");
            }

            EnsureNameAvailable(catalog, name);
            var entry = new WorkspaceEntry(Guid.NewGuid().ToString("N"), name)
            {
                Theme = theme,
                Symbol = symbol,
                Color = color,
            };
            var updated = catalog with { Workspaces = [.. catalog.Workspaces, entry] };
            await SaveCoreAsync(updated, cancellationToken).ConfigureAwait(false);
            return (entry, updated);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<WorkspaceCatalog> SetThemeAsync(string id, string theme, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(theme);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var catalogLock = await LockCatalogAsync(cancellationToken).ConfigureAwait(false);
            var catalog = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            var selected = catalog.Workspaces.FirstOrDefault(item => item.Id == id)
                ?? throw new KeyNotFoundException("Рабочее пространство не найдено.");
            if (selected.Theme == theme)
            {
                return catalog;
            }

            var updated = catalog with
            {
                Workspaces = catalog.Workspaces.Select(item => item.Id == id ? item with { Theme = theme } : item).ToList(),
            };
            await SaveCoreAsync(updated, cancellationToken).ConfigureAwait(false);
            return updated;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RenameAsync(string id, string name, CancellationToken cancellationToken = default)
    {
        name = ValidateName(name);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var catalogLock = await LockCatalogAsync(cancellationToken).ConfigureAwait(false);
            var catalog = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            if (id == DefaultWorkspaceId)
            {
                throw new InvalidOperationException("Рабочее пространство «По умолчанию» нельзя переименовать.");
            }

            if (!catalog.Workspaces.Any(item => item.Id == id))
            {
                throw new KeyNotFoundException("Рабочее пространство не найдено.");
            }

            EnsureNameAvailable(catalog, name, id);
            var updated = catalog with
            {
                Workspaces = catalog.Workspaces.Select(item => item.Id == id ? item with { Name = name } : item).ToList(),
            };
            await SaveCoreAsync(updated, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetWindowWorkspaceAsync(string windowId, string workspaceId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(windowId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var catalogLock = await LockCatalogAsync(cancellationToken).ConfigureAwait(false);
            var catalog = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            if (!catalog.Workspaces.Any(item => item.Id == workspaceId))
            {
                throw new KeyNotFoundException("Рабочее пространство не найдено.");
            }

            var assignments = new Dictionary<string, string>(catalog.WindowAssignments, StringComparer.Ordinal)
            {
                [windowId] = workspaceId,
            };
            var updated = catalog with
            {
                WindowAssignments = assignments,
                Workspaces = catalog.Workspaces.Select(item => item.Id == workspaceId
                    ? item with { LastUsedMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }
                    : item).ToList(),
            };
            await SaveCoreAsync(updated, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<WorkspaceCatalog> RemoveWindowAsync(string windowId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var catalogLock = await LockCatalogAsync(cancellationToken).ConfigureAwait(false);
            var catalog = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            if (!catalog.WindowAssignments.ContainsKey(windowId))
            {
                return catalog;
            }

            var assignments = new Dictionary<string, string>(catalog.WindowAssignments, StringComparer.Ordinal);
            assignments.Remove(windowId);
            var updated = catalog with { WindowAssignments = assignments };
            await SaveCoreAsync(updated, cancellationToken).ConfigureAwait(false);
            return updated;
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<WorkspaceCatalog> DeleteAsync(string id, int retentionLimit = 10, CancellationToken cancellationToken = default) =>
        DeleteCoreAsync(id, retentionLimit, cancellationToken);

    public Task<WorkspaceCatalog> DiscardIncompleteCopyAsync(string id) =>
        DeleteCoreAsync(id, null, CancellationToken.None);

    private async Task<WorkspaceCatalog> DeleteCoreAsync(string id, int? retentionLimit, CancellationToken cancellationToken)
    {
        if (id == DefaultWorkspaceId)
        {
            throw new InvalidOperationException("Пространство «По умолчанию» удалить нельзя.");
        }

        if (retentionLimit is < -1 or > MaximumWorkspaceCount)
        {
            throw new ArgumentOutOfRangeException(nameof(retentionLimit));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var catalogLock = await LockCatalogAsync(cancellationToken).ConfigureAwait(false);
            var catalog = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            var target = catalog.Workspaces.FirstOrDefault(item => item.Id == id)
                ?? throw new KeyNotFoundException("Рабочее пространство не найдено.");
            var deletedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var archiveRoot = Path.Combine(_directory, "deleted");
            var directoryName = id + "-" + deletedAt.ToString(CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N");
            var tombstone = Path.Combine(archiveRoot, directoryName);
            var workspacePath = GetWorkspaceDirectory(id);
            var moved = false;
            var committed = false;
            try
            {
                if (Directory.Exists(workspacePath))
                {
                    EnsureNoReparsePoints(workspacePath);
                    EnsureNoReparsePoints(tombstone);
                    Directory.CreateDirectory(archiveRoot);
                    await WriteMoveMarkerAsync(directoryName, cancellationToken).ConfigureAwait(false);
                    Directory.Move(workspacePath, tombstone);
                    moved = true;
                }

                var deleted = (catalog.DeletedWorkspaces ?? [])
                    .Append(new DeletedWorkspaceEntry(target, deletedAt, moved ? directoryName : ""))
                    .OrderByDescending(item => item.DeletedAtMs)
                    .ToList();
                var retained = retentionLimit switch
                {
                    null => deleted.Where(item => item.Workspace.Id != id).ToList(),
                    -1 => deleted,
                    int limit => deleted.Take(limit).ToList(),
                };
                var updated = catalog with
                {
                    Workspaces = catalog.Workspaces.Where(item => item.Id != id).ToList(),
                    WindowAssignments = catalog.WindowAssignments
                        .Where(pair => pair.Value != id)
                        .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
                    DeletedWorkspaces = retained,
                };
                await SaveCoreAsync(updated, cancellationToken).ConfigureAwait(false);
                committed = true;
                if (moved)
                {
                    File.Delete(_moveMarker);
                }

                foreach (var expired in deleted.Except(retained))
                {
                    if (string.IsNullOrEmpty(expired.DirectoryName))
                    {
                        continue;
                    }

                    var expiredPath = Path.Combine(archiveRoot, expired.DirectoryName);
                    try
                    {
                        EnsureNoReparsePoints(expiredPath);
                        Directory.Delete(expiredPath, recursive: true);
                    }
                    catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
                    {
                    }
                }

                return updated;
            }
            catch when (!committed)
            {
                if (moved && Directory.Exists(tombstone) && !Directory.Exists(workspacePath))
                {
                    Directory.Move(tombstone, workspacePath);
                }

                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<WorkspaceCatalog> RestoreDeletedAsync(string id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var catalogLock = await LockCatalogAsync(cancellationToken).ConfigureAwait(false);
            var catalog = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            var deleted = (catalog.DeletedWorkspaces ?? []).FirstOrDefault(item => item.Workspace.Id == id)
                ?? throw new KeyNotFoundException("Удалённое рабочее пространство не найдено.");
            if (catalog.Workspaces.Any(item => item.Id == id))
            {
                throw new InvalidOperationException("Рабочее пространство уже восстановлено.");
            }

            if (catalog.Workspaces.Count >= MaximumWorkspaceCount)
            {
                throw new InvalidOperationException("Достигнут предел в 200 рабочих пространств.");
            }

            var restoredWorkspace = deleted.Workspace;
            if (catalog.Workspaces.Any(item => string.Equals(item.Name, restoredWorkspace.Name, StringComparison.OrdinalIgnoreCase)))
            {
                restoredWorkspace = restoredWorkspace with { Name = CreateUniqueRestoredName(catalog, restoredWorkspace.Name) };
            }

            var archiveRoot = Path.Combine(_directory, "deleted");
            var archivedPath = string.IsNullOrEmpty(deleted.DirectoryName)
                ? null
                : Path.Combine(archiveRoot, deleted.DirectoryName);
            var workspacePath = GetWorkspaceDirectory(id);
            var moved = archivedPath is not null;
            if (moved && !Directory.Exists(archivedPath))
            {
                throw new InvalidDataException("Файлы удалённого пространства отсутствуют. Запись оставлена в истории.");
            }

            if (moved)
            {
                EnsureNoReparsePoints(archivedPath!);
                if (Directory.Exists(workspacePath))
                {
                    throw new IOException("Целевая папка пространства уже существует.");
                }

                Directory.CreateDirectory(_directory);
                await WriteMoveMarkerAsync(deleted.DirectoryName, cancellationToken).ConfigureAwait(false);
                Directory.Move(archivedPath!, workspacePath);
            }

            var committed = false;
            try
            {
                var updated = catalog with
                {
                    Workspaces = [.. catalog.Workspaces, restoredWorkspace],
                    DeletedWorkspaces = catalog.DeletedWorkspaces!.Where(item => item.Workspace.Id != id).ToList(),
                };
                await SaveCoreAsync(updated, cancellationToken).ConfigureAwait(false);
                committed = true;
                if (moved)
                {
                    File.Delete(_moveMarker);
                }

                return updated;
            }
            catch when (!committed)
            {
                if (moved && Directory.Exists(workspacePath) && !Directory.Exists(archivedPath!))
                {
                    Directory.Move(workspacePath, archivedPath!);
                }

                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public static string? ValidateSymbol(string? symbol)
    {
        if (symbol is null) return null;
        if (symbol.Length == 0 || string.IsNullOrWhiteSpace(symbol)
            || StringInfo.ParseCombiningCharacters(symbol).Length != 1)
        {
            throw new ArgumentException("Укажите один видимый символ или эмодзи.", nameof(symbol));
        }

        var runes = new List<Rune>();
        var remaining = symbol.AsSpan();
        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(remaining, out var rune, out var consumed) != OperationStatus.Done)
            {
                throw new ArgumentException("Символ содержит некорректную последовательность Unicode.", nameof(symbol));
            }

            runes.Add(rune);
            if (runes.Count > 32)
            {
                throw new ArgumentException("Символ слишком длинный.", nameof(symbol));
            }

            remaining = remaining[consumed..];
        }

        var hasVisibleRune = false;
        for (var index = 0; index < runes.Count; index++)
        {
            var rune = runes[index];
            var category = Rune.GetUnicodeCategory(rune);
            if (Rune.IsWhiteSpace(rune) || category is UnicodeCategory.Control or UnicodeCategory.LineSeparator
                or UnicodeCategory.ParagraphSeparator or UnicodeCategory.Surrogate or UnicodeCategory.OtherNotAssigned
                or UnicodeCategory.PrivateUse)
            {
                throw new ArgumentException("Символ должен быть видимым и не содержать управляющих знаков.", nameof(symbol));
            }

            if (category == UnicodeCategory.Format && rune.Value != 0x200D
                && rune.Value is not (>= 0xE0020 and <= 0xE007F))
            {
                throw new ArgumentException("Символ содержит невидимый управляющий знак.", nameof(symbol));
            }

            if (rune.Value == 0x200D
                && (!runes.Take(index).Any(previous => !IsNonVisibleRune(previous))
                    || !runes.Skip(index + 1).Any(next => !IsNonVisibleRune(next))))
            {
                throw new ArgumentException("Соединитель эмодзи должен находиться между видимыми символами.", nameof(symbol));
            }

            if (!IsNonVisibleRune(rune))
            {
                hasVisibleRune = true;
            }
        }

        if (!hasVisibleRune)
        {
            throw new ArgumentException("Укажите один видимый символ или эмодзи.", nameof(symbol));
        }

        return symbol;
    }

    public static string? ValidateColor(string? color)
    {
        if (color is null) return null;
        if (color.Length != 7 || color[0] != '#')
        {
            throw new ArgumentException("Цвет должен быть задан в формате #RRGGBB.", nameof(color));
        }

        for (var index = 1; index < color.Length; index++)
        {
            if (!char.IsAsciiHexDigit(color[index]))
            {
                throw new ArgumentException("Цвет должен быть задан в формате #RRGGBB.", nameof(color));
            }
        }

        return color.ToUpperInvariant();
    }

    private static bool IsNonVisibleRune(Rune rune)
    {
        var category = Rune.GetUnicodeCategory(rune);
        return category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark
            or UnicodeCategory.EnclosingMark or UnicodeCategory.Format;
    }

    private static string NormalizeWorkspaceId(string workspaceId)
    {
        if (workspaceId == DefaultWorkspaceId)
        {
            return DefaultWorkspaceId;
        }

        if (!Guid.TryParseExact(workspaceId, "N", out var guid))
        {
            throw new InvalidDataException("Идентификатор рабочего пространства некорректен.");
        }

        return guid.ToString("N");
    }

    private static string ResolveWindowDirectoryName(string windowId)
    {
        if (windowId == PrimaryWindowId)
        {
            return PrimaryWindowId;
        }

        if (windowId.StartsWith("window:", StringComparison.Ordinal)
            && Guid.TryParseExact(windowId.AsSpan("window:".Length), "N", out var windowGuid))
        {
            return windowGuid.ToString("N");
        }

        throw new InvalidDataException("Идентификатор окна некорректен.");
    }

    private async Task<WorkspaceCatalog> LoadCoreAsync(CancellationToken token)
    {
        if (!File.Exists(_indexPath))
        {
            if (File.Exists(_moveMarker))
            {
                throw new InvalidDataException("Отсутствует каталог для незавершённого перемещения пространства.");
            }

            var initial = WorkspaceCatalog.CreateDefault();
            await SaveCoreAsync(initial, token).ConfigureAwait(false);
            return initial;
        }

        try
        {
            await using var stream = new FileStream(_indexPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            var catalog = Validate(await JsonSerializer.DeserializeAsync(stream, WorkspaceCatalogJsonContext.Default.WorkspaceCatalog, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("Каталог рабочих пространств пуст."));
            await RecoverDirectoryMoveAsync(catalog, token).ConfigureAwait(false);
            return catalog;
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("Каталог рабочих пространств повреждён. Другие сохранённые данные не изменены.", error);
        }
    }

    private async Task<FileStream> LockCatalogAsync(CancellationToken token)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "index.lock");
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33
                && Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(5))
            {
                await Task.Delay(25, token).ConfigureAwait(false);
            }
        }
    }

    private Task WriteMoveMarkerAsync(string directoryName, CancellationToken token) =>
        AtomicWriteAsync(_moveMarker, Encoding.UTF8.GetBytes(directoryName), token);

    private async Task RecoverDirectoryMoveAsync(WorkspaceCatalog catalog, CancellationToken token)
    {
        if (!File.Exists(_moveMarker)) return;
        EnsureNoReparsePoints(_moveMarker);
        if (new FileInfo(_moveMarker).Length > 128)
        {
            throw new InvalidDataException("Журнал перемещения пространства повреждён.");
        }

        var name = await File.ReadAllTextAsync(_moveMarker, token).ConfigureAwait(false);
        var parts = name.Split('-');
        if (parts.Length != 3 || !Guid.TryParseExact(parts[0], "N", out _) || !long.TryParse(parts[1], CultureInfo.InvariantCulture, out _)
            || !Guid.TryParseExact(parts[2], "N", out _))
        {
            throw new InvalidDataException("Журнал перемещения пространства повреждён.");
        }

        var live = GetWorkspaceDirectory(parts[0]);
        var archived = Path.Combine(_directory, "deleted", name);
        var active = catalog.Workspaces.Any(entry => entry.Id == parts[0]);
        var retained = catalog.DeletedWorkspaces?.Any(entry => entry.DirectoryName == name) == true;
        if (active || retained)
        {
            var target = active ? live : archived;
            var source = active ? archived : live;
            if (Directory.Exists(target) == Directory.Exists(source))
            {
                throw new InvalidDataException("Не удалось однозначно восстановить папку рабочего пространства.");
            }

            if (!Directory.Exists(target))
            {
                EnsureNoReparsePoints(source);
                EnsureNoReparsePoints(target);
                Directory.Move(source, target);
            }
        }

        File.Delete(_moveMarker);
    }

    private Task SaveCoreAsync(WorkspaceCatalog catalog, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(Validate(catalog), WorkspaceCatalogJsonContext.Default.WorkspaceCatalog);
        return AtomicWriteAsync(_indexPath, bytes, token);
    }

    private static async Task AtomicWriteAsync(string path, byte[] bytes, CancellationToken token)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new IOException("Некорректный путь.");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.pending");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                32 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
                stream.Flush(true);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                try { File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private WorkspaceCatalog Validate(WorkspaceCatalog catalog)
    {
        if (catalog.Workspaces is null || catalog.WindowAssignments is null || catalog.Workspaces.Count is < 1 or > MaximumWorkspaceCount
            || catalog.Workspaces.Any(item => item is null))
        {
            throw new InvalidDataException("Каталог рабочих пространств содержит недопустимое число записей.");
        }

        if (!catalog.Workspaces.Any(item => item.Id == DefaultWorkspaceId && item.Name == DefaultWorkspaceName))
        {
            throw new InvalidDataException("В каталоге отсутствует пространство «По умолчанию».");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in catalog.Workspaces)
        {
            if (item is null || (item.Id != DefaultWorkspaceId && !Guid.TryParseExact(item.Id, "N", out _))
                || !ids.Add(item.Id) || !names.Add(ValidateStoredName(item.Name)))
            {
                throw new InvalidDataException("Каталог рабочих пространств содержит повторяющиеся или некорректные записи.");
            }
        }

        foreach (var assignment in catalog.WindowAssignments)
        {
            if (string.IsNullOrWhiteSpace(assignment.Key) || !ids.Contains(assignment.Value))
            {
                throw new InvalidDataException("Назначение рабочего пространства окну некорректно.");
            }
        }

        foreach (var deleted in catalog.DeletedWorkspaces ?? [])
        {
            if (deleted is null || deleted.Workspace is null || deleted.DirectoryName is null || deleted.Workspace.Id == DefaultWorkspaceId
                || !Guid.TryParseExact(deleted.Workspace.Id, "N", out _) || ids.Contains(deleted.Workspace.Id)
                || (deleted.DirectoryName.Length != 0 && (deleted.DirectoryName.Any(character => !Uri.IsHexDigit(character) && character != '-')
                    || !deleted.DirectoryName.StartsWith(deleted.Workspace.Id + "-", StringComparison.Ordinal))))
            {
                throw new InvalidDataException("История удалённых пространств содержит некорректную запись.");
            }

            _ = ValidateStoredName(deleted.Workspace.Name);
        }

        try
        {
            return catalog with
            {
                Workspaces = catalog.Workspaces.Select(NormalizeAppearance).ToList(),
                DeletedWorkspaces = catalog.DeletedWorkspaces?.Select(item => item with
                {
                    Workspace = NormalizeAppearance(item.Workspace),
                }).ToList(),
            };
        }
        catch (ArgumentException error)
        {
            throw new InvalidDataException("Каталог содержит некорректный символ или цвет рабочего пространства.", error);
        }
    }

    private static WorkspaceEntry NormalizeAppearance(WorkspaceEntry entry) => entry with
    {
        Symbol = ValidateSymbol(entry.Symbol),
        Color = ValidateColor(entry.Color),
    };

    private static string ValidateName(string name)
    {
        var value = name?.Trim() ?? "";
        if (value.Length is < 1 or > MaximumNameLength || value.Any(char.IsControl))
        {
            throw new ArgumentException("Имя рабочего пространства должно содержать от 1 до 80 печатных символов.", nameof(name));
        }

        return value;
    }

    private static string ValidateStoredName(string name)
    {
        try
        {
            return ValidateName(name);
        }
        catch (ArgumentException error)
        {
            throw new InvalidDataException("Имя рабочего пространства в каталоге некорректно.", error);
        }
    }

    private static void EnsureNameAvailable(WorkspaceCatalog catalog, string name, string? exceptId = null)
    {
        if (catalog.Workspaces.Any(item => item.Id != exceptId && string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("Рабочее пространство с таким именем уже существует.");
        }
    }

    private static string CreateUniqueRestoredName(WorkspaceCatalog catalog, string originalName)
    {
        for (var suffixIndex = 1; ; suffixIndex++)
        {
            var suffix = suffixIndex == 1 ? " (восстановлено)" : $" (восстановлено {suffixIndex})";
            var prefixLength = Math.Min(originalName.Length, MaximumNameLength - suffix.Length);
            if (prefixLength > 0 && char.IsHighSurrogate(originalName[prefixLength - 1]))
            {
                prefixLength--;
            }

            var candidate = originalName[..prefixLength].TrimEnd() + suffix;
            if (!catalog.Workspaces.Any(item => string.Equals(item.Name, candidate, StringComparison.OrdinalIgnoreCase)))
            {
                return candidate;
            }
        }
    }

    private void EnsureNoReparsePoints(string path)
    {
        var root = Path.GetFullPath(_directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(path);
        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Путь рабочего пространства выходит за пределы каталога.");
        }

        for (var current = target; current.StartsWith(root, StringComparison.OrdinalIgnoreCase); current = Path.GetDirectoryName(current)!)
        {
            if (File.Exists(current) || Directory.Exists(current))
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException("Рабочее пространство содержит ссылку на другой каталог.");
                }
            }

            var parent = Path.GetDirectoryName(current);
            if (parent is null || string.Equals(parent, _directory, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
        }

        if (Directory.Exists(target))
        {
            var pendingDirectories = new Stack<string>();
            pendingDirectories.Push(target);
            while (pendingDirectories.TryPop(out var currentDirectory))
            {
                foreach (var child in Directory.EnumerateFileSystemEntries(currentDirectory, "*", SearchOption.TopDirectoryOnly))
                {
                    var attributes = File.GetAttributes(child);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        throw new InvalidDataException("Рабочее пространство содержит ссылку на другой каталог.");
                    }

                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        pendingDirectories.Push(child);
                    }
                }
            }
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(WorkspaceCatalog))]
[JsonSerializable(typeof(WorkspaceEntry))]
[JsonSerializable(typeof(DeletedWorkspaceEntry))]
internal partial class WorkspaceCatalogJsonContext : JsonSerializerContext;
