using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetText.Core;

public sealed record DraftArchiveEntry(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("tab")] SessionTab Tab,
    [property: JsonPropertyName("closed_at_ms")] long ClosedAtMs,
    [property: JsonPropertyName("bytes")] long Bytes,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("archived_at_ms")] long ArchivedAtMs);

public sealed record DraftArchiveIndex(
    [property: JsonPropertyName("entries")] List<DraftArchiveEntry> Entries);

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(DraftArchiveIndex))]
[JsonSerializable(typeof(DraftArchiveEntry))]
[JsonSerializable(typeof(SessionTab))]
internal partial class DraftArchiveJsonContext : JsonSerializerContext;

public sealed class DraftArchiveService
{
    private const string DraftEntryName = "draft.txt";
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);

    private readonly string _archiveDirectory;
    private readonly string _indexPath;
    private readonly SemaphoreSlim _gate;

    public DraftArchiveService(string profileRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileRoot);
        _archiveDirectory = Path.Combine(Path.GetFullPath(profileRoot), "drafts-archive");
        _indexPath = Path.Combine(_archiveDirectory, "index.json");
        _gate = Gates.GetOrAdd(_archiveDirectory, static _ => new SemaphoreSlim(1, 1));
        Directory.CreateDirectory(_archiveDirectory);
    }

    public string ArchiveDirectory => _archiveDirectory;

    public async Task<IReadOnlyList<DraftArchiveEntry>> ListAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await ReadIndexAsync(token).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<DraftArchiveEntry> ArchiveAsync(
        SessionTab tab,
        byte[] draftBytes,
        string? displayName = null,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(tab);
        if (tab.File is not null || draftBytes is null || draftBytes.Length == 0)
        {
            throw new InvalidDataException("Архивировать можно только закрытый черновик с сохранённым текстом.");
        }

        await _gate.WaitAsync(token).ConfigureAwait(false);
        string? temporary = null;
        string? archivePath = null;
        try
        {
            var entries = (await ReadIndexAsync(token).ConfigureAwait(false)).ToList();
            var sourceHash = Convert.ToHexString(SHA256.HashData(draftBytes));
            var existing = tab.ClosedAtMs is { } closedAtMs
                ? entries.FirstOrDefault(candidate => candidate.Tab.Id == tab.Id && candidate.ClosedAtMs == closedAtMs)
                : null;
            if (existing is not null && await IsValidArchiveAsync(existing, token).ConfigureAwait(false))
            {
                if (!sourceHash.Equals(existing.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("Сохранённый черновик изменился после архивации. Исходный текст оставлен в истории.");
                }

                return existing;
            }

            var id = Guid.NewGuid().ToString("N");
            archivePath = ArchivePath(id);
            temporary = Path.Combine(_archiveDirectory, id + ".tmp");
            var (bytes, hash) = await WriteArchiveFromBytesAsync(draftBytes, temporary, token).ConfigureAwait(false);
            var closedAt = tab.ClosedAtMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var resolvedName = ResolveDisplayName(draftBytes, tab, displayName);
            var entry = new DraftArchiveEntry(id, tab, closedAt, bytes, hash, resolvedName, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            if (!await VerifyArchiveFileAsync(temporary, entry.Bytes, entry.Sha256, token).ConfigureAwait(false))
            {
                throw new InvalidDataException("Проверка сжатого архива не пройдена. Исходный текст сохранён.");
            }

            File.Move(temporary, archivePath);
            temporary = null;
            var staleArchives = entries.Where(candidate => candidate.Tab.Id == tab.Id && candidate.ClosedAtMs == entry.ClosedAtMs).ToArray();
            entries.RemoveAll(candidate => staleArchives.Any(stale => stale.Id == candidate.Id));
            entries.Insert(0, entry);
            await WriteIndexAsync(entries, token).ConfigureAwait(false);
            foreach (var stale in staleArchives)
            {
                TryDelete(ArchivePath(stale.Id));
            }

            return entry;
        }
        catch
        {
            if (temporary is not null) TryDelete(temporary);
            if (archivePath is not null) TryDelete(archivePath);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<DraftArchiveEntry> ArchiveAsync(
        SessionTab tab,
        string draftPath,
        string? displayName = null,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentException.ThrowIfNullOrWhiteSpace(draftPath);
        if (tab.File is not null)
        {
            throw new InvalidDataException("Архивировать можно только закрытый черновик с сохранённым текстом.");
        }

        await _gate.WaitAsync(token).ConfigureAwait(false);
        string? temporary = null;
        string? archivePath = null;
        try
        {
            var entries = (await ReadIndexAsync(token).ConfigureAwait(false)).ToList();
            var existing = tab.ClosedAtMs is { } closedAtMs
                ? entries.FirstOrDefault(candidate => candidate.Tab.Id == tab.Id && candidate.ClosedAtMs == closedAtMs)
                : null;
            if (existing is not null && await IsValidArchiveAsync(existing, token).ConfigureAwait(false))
            {
                var sourceHash = await HashFileAsync(draftPath, token).ConfigureAwait(false);
                if (!sourceHash.Equals(existing.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("Сохранённый черновик изменился после архивации. Исходный текст оставлен в истории.");
                }

                return existing;
            }

            var id = Guid.NewGuid().ToString("N");
            archivePath = ArchivePath(id);
            temporary = Path.Combine(_archiveDirectory, id + ".tmp");
            var (bytes, hash) = await WriteArchiveFromFileAsync(draftPath, temporary, token).ConfigureAwait(false);
            if (bytes == 0)
            {
                throw new InvalidDataException("Архивировать можно только закрытый черновик с сохранённым текстом.");
            }

            var closedAt = tab.ClosedAtMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var resolvedName = string.IsNullOrWhiteSpace(displayName)
                ? ReadDisplayNameFromFile(draftPath, tab)
                : ClampDisplayName(displayName);
            var entry = new DraftArchiveEntry(id, tab, closedAt, bytes, hash, resolvedName, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            if (!await VerifyArchiveFileAsync(temporary, entry.Bytes, entry.Sha256, token).ConfigureAwait(false))
            {
                throw new InvalidDataException("Проверка сжатого архива не пройдена. Исходный текст сохранён.");
            }

            File.Move(temporary, archivePath);
            temporary = null;
            var staleArchives = entries.Where(candidate => candidate.Tab.Id == tab.Id && candidate.ClosedAtMs == entry.ClosedAtMs).ToArray();
            entries.RemoveAll(candidate => staleArchives.Any(stale => stale.Id == candidate.Id));
            entries.Insert(0, entry);
            await WriteIndexAsync(entries, token).ConfigureAwait(false);
            foreach (var stale in staleArchives)
            {
                TryDelete(ArchivePath(stale.Id));
            }

            return entry;
        }
        catch
        {
            if (temporary is not null) TryDelete(temporary);
            if (archivePath is not null) TryDelete(archivePath);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<(SessionTab Tab, byte[] DraftBytes)> RestoreAsync(DraftArchiveEntry entry, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var entries = await ReadIndexAsync(token).ConfigureAwait(false);
            if (!entries.Any(candidate => candidate.Id == entry.Id))
            {
                throw new FileNotFoundException("Черновик уже удалён из архива.");
            }

            var source = ArchivePath(entry.Id);
            if (!File.Exists(source))
            {
                throw new FileNotFoundException("Файл архива черновика отсутствует.", source);
            }

            var (draftBytes, hash) = await ExtractAndVerifyBytesAsync(source, entry.Bytes, token).ConfigureAwait(false);
            if (draftBytes.LongLength != entry.Bytes || !hash.Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Архив повреждён или контрольная сумма не совпадает. Остальные черновики не затронуты.");
            }

            var tab = entry.Tab with
            {
                ClosedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };
            return (tab, draftBytes);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteAsync(DraftArchiveEntry entry, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        string? tombstone = null;
        var archivePath = ArchivePath(entry.Id);
        try
        {
            var entries = (await ReadIndexAsync(token).ConfigureAwait(false)).ToList();
            if (entries.RemoveAll(candidate => candidate.Id == entry.Id) == 0)
            {
                return;
            }

            if (File.Exists(archivePath))
            {
                tombstone = archivePath + ".delete";
                File.Move(archivePath, tombstone, overwrite: true);
            }

            try
            {
                await WriteIndexAsync(entries, token).ConfigureAwait(false);
            }
            catch
            {
                if (tombstone is not null && File.Exists(tombstone))
                {
                    File.Move(tombstone, archivePath, overwrite: true);
                }

                throw;
            }

            if (tombstone is not null)
            {
                TryDelete(tombstone);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> PruneExpiredAsync(int retentionDays, CancellationToken token = default)
    {
        if (retentionDays <= 0)
        {
            return 0;
        }

        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var entries = (await ReadIndexAsync(token).ConfigureAwait(false)).ToList();
            var initialCount = entries.Count;
            var cutoff = DateTimeOffset.UtcNow.AddDays(-retentionDays).ToUnixTimeMilliseconds();
            var expired = entries.Where(entry => entry.ArchivedAtMs <= cutoff).ToArray();
            var tombstones = new List<(DraftArchiveEntry Entry, string Path)>();
            foreach (var entry in expired)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var path = ArchivePath(entry.Id);
                    if (File.Exists(path))
                    {
                        var tombstone = path + ".delete";
                        File.Move(path, tombstone, overwrite: true);
                        tombstones.Add((entry, tombstone));
                    }

                    entries.RemoveAll(candidate => candidate.Id == entry.Id);
                }
                catch (FileNotFoundException)
                {
                    entries.RemoveAll(candidate => candidate.Id == entry.Id);
                }
                catch (DirectoryNotFoundException)
                {
                    entries.RemoveAll(candidate => candidate.Id == entry.Id);
                }
                catch (UnauthorizedAccessException) { }
                catch (IOException) { }
            }

            var removed = initialCount - entries.Count;
            if (removed == 0)
            {
                return 0;
            }

            try
            {
                await WriteIndexAsync(entries, token).ConfigureAwait(false);
            }
            catch
            {
                foreach (var (entry, path) in tombstones)
                {
                    var original = ArchivePath(entry.Id);
                    if (File.Exists(path))
                    {
                        File.Move(path, original, overwrite: true);
                    }
                }

                throw;
            }

            foreach (var (_, path) in tombstones)
            {
                TryDelete(path);
            }

            return removed;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CopyEntryToAsync(
        DraftArchiveEntry entry,
        DraftArchiveService destination,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(destination);
        string? temporary = null;
        DraftArchiveEntry source;
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var entries = await ReadIndexAsync(token).ConfigureAwait(false);
            source = entries.FirstOrDefault(candidate => candidate.Id == entry.Id)
                ?? throw new FileNotFoundException("Архивный черновик уже удалён.");
            if (source.Bytes != entry.Bytes || !source.Sha256.Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Архивный черновик изменился после открытия списка.");
            }

            if (source.Tab.File is not null)
            {
                throw new InvalidDataException("Запись архива не содержит закрытый черновик.");
            }

            temporary = Path.Combine(destination._archiveDirectory, $"copy-{Guid.NewGuid():N}.tmp");
            await CopyFileAsync(ArchivePath(source.Id), temporary, token).ConfigureAwait(false);
        }
        catch
        {
            if (temporary is not null) TryDelete(temporary);
            throw;
        }
        finally
        {
            _gate.Release();
        }

        try
        {
            if (!await VerifyArchiveFileAsync(temporary!, source.Bytes, source.Sha256, token).ConfigureAwait(false))
            {
                throw new InvalidDataException("Копия архивного черновика не прошла проверку.");
            }

            await destination.CommitCopiedEntryAsync(source, temporary!, token).ConfigureAwait(false);
        }
        finally
        {
            TryDelete(temporary!);
        }
    }

    private async Task CommitCopiedEntryAsync(
        DraftArchiveEntry copied,
        string stagedArchive,
        CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        var archivePath = ArchivePath(copied.Id);
        try
        {
            var entries = (await ReadIndexAsync(token).ConfigureAwait(false)).ToList();
            var existing = entries.FirstOrDefault(candidate => candidate.Id == copied.Id);
            if (existing is not null)
            {
                if (!SameArchiveContent(existing, copied) || !await IsValidArchiveAsync(existing, token).ConfigureAwait(false))
                {
                    throw new InvalidDataException("В целевом архиве уже есть другой черновик с таким идентификатором.");
                }

                return;
            }

            if (File.Exists(archivePath))
            {
                if (!await VerifyArchiveFileAsync(archivePath, copied.Bytes, copied.Sha256, token).ConfigureAwait(false))
                {
                    throw new InvalidDataException("В целевом архиве уже есть файл с таким идентификатором.");
                }
            }
            else
            {
                File.Move(stagedArchive, archivePath);
            }

            entries.Insert(0, copied);
            await WriteIndexAsync(entries, token).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool SameArchiveContent(DraftArchiveEntry left, DraftArchiveEntry right) =>
        left.Id == right.Id
        && left.ClosedAtMs == right.ClosedAtMs
        && left.ArchivedAtMs == right.ArchivedAtMs
        && left.Bytes == right.Bytes
        && left.Sha256.Equals(right.Sha256, StringComparison.OrdinalIgnoreCase)
        && left.DisplayName == right.DisplayName
        && JsonSerializer.SerializeToUtf8Bytes(left.Tab, DraftArchiveJsonContext.Default.SessionTab)
            .AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(right.Tab, DraftArchiveJsonContext.Default.SessionTab));

    private async Task<List<DraftArchiveEntry>> ReadIndexAsync(CancellationToken token)
    {
        if (!File.Exists(_indexPath))
        {
            return [];
        }

        try
        {
            await using var stream = File.OpenRead(_indexPath);
            var index = await JsonSerializer.DeserializeAsync(stream, DraftArchiveJsonContext.Default.DraftArchiveIndex, token)
                .ConfigureAwait(false);
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (index?.Entries is not { } entries || entries.Any(entry => entry is null
                || entry.Tab is not { File: null }
                || !Guid.TryParseExact(entry.Id, "N", out _)
                || !ids.Add(entry.Id)
                || entry.Bytes < 0
                || entry.Sha256 is not { Length: 64 }
                || !entry.Sha256.All(char.IsAsciiHexDigit)
                || !ValidTimestamp(entry.ClosedAtMs)
                || !ValidTimestamp(entry.ArchivedAtMs)))
            {
                throw new InvalidDataException("Индекс архива черновиков повреждён. Файлы архива не удалены.");
            }

            RecoverDeleteTransactions(entries);
            return entries;
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("Индекс архива черновиков повреждён. Файлы архива не удалены.", error);
        }
    }

    private static bool ValidTimestamp(long value) =>
        value >= DateTimeOffset.MinValue.ToUnixTimeMilliseconds() && value <= DateTimeOffset.MaxValue.ToUnixTimeMilliseconds();

    private void RecoverDeleteTransactions(IReadOnlyList<DraftArchiveEntry> entries)
    {
        var activeIds = entries.Select(entry => entry.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            var archive = ArchivePath(entry.Id);
            var tombstone = archive + ".delete";
            if (!File.Exists(archive) && File.Exists(tombstone))
            {
                try { File.Move(tombstone, archive); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        foreach (var tombstone in Directory.EnumerateFiles(_archiveDirectory, "*.zip.delete"))
        {
            var id = Path.GetFileName(tombstone)[..^".zip.delete".Length];
            if (!activeIds.Contains(id))
            {
                TryDelete(tombstone);
            }
        }
    }

    private async Task WriteIndexAsync(List<DraftArchiveEntry> entries, CancellationToken token)
    {
        var temporary = _indexPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, new DraftArchiveIndex(entries),
                    DraftArchiveJsonContext.Default.DraftArchiveIndex, token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
                stream.Flush(true);
            }

            File.Move(temporary, _indexPath, overwrite: true);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private string ArchivePath(string id)
    {
        if (id.Length != 32 || !id.All(char.IsAsciiHexDigit))
        {
            throw new InvalidDataException("Недопустимый идентификатор архива.");
        }

        return Path.Combine(_archiveDirectory, id + ".zip");
    }

    private static async Task<(long Bytes, string Hash)> WriteArchiveFromBytesAsync(byte[] draftBytes, string outputPath, CancellationToken token)
    {
        await using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
            64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry(DraftEntryName, CompressionLevel.Optimal);
            await using var compressed = entry.Open();
            await compressed.WriteAsync(draftBytes, token).ConfigureAwait(false);
            await compressed.FlushAsync(token).ConfigureAwait(false);
        }

        await output.FlushAsync(token).ConfigureAwait(false);
        output.Flush(true);
        return (draftBytes.LongLength, Convert.ToHexString(SHA256.HashData(draftBytes)));
    }

    private static async Task<(long Bytes, string Hash)> WriteArchiveFromFileAsync(string inputPath, string outputPath, CancellationToken token)
    {
        await using var input = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
            64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long bytes = 0;

        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry(DraftEntryName, CompressionLevel.Optimal);
            await using var compressed = entry.Open();
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
            {
                await compressed.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                hash.AppendData(buffer, 0, read);
                bytes += read;
            }

            await compressed.FlushAsync(token).ConfigureAwait(false);
        }

        await output.FlushAsync(token).ConfigureAwait(false);
        output.Flush(true);
        return (bytes, Convert.ToHexString(hash.GetHashAndReset()));
    }

    private static async Task<(byte[] Bytes, string Hash)> ExtractAndVerifyBytesAsync(string archivePath, long maximumBytes, CancellationToken token)
    {
        await using var archiveStream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var zip = new ZipArchive(archiveStream, ZipArchiveMode.Read);
        var entry = zip.GetEntry(DraftEntryName) ?? throw new InvalidDataException("В архиве отсутствует текст черновика.");
        await using var input = entry.Open();
        using var output = new MemoryStream(maximumBytes is > 0 and <= int.MaxValue ? (int)maximumBytes : 0);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long bytes = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            if (bytes + read > maximumBytes)
            {
                throw new InvalidDataException("Распакованный архив превышает сохранённый размер.");
            }

            output.Write(buffer, 0, read);
            hash.AppendData(buffer, 0, read);
            bytes += read;
        }

        return (output.ToArray(), Convert.ToHexString(hash.GetHashAndReset()));
    }

    private async Task<bool> IsValidArchiveAsync(DraftArchiveEntry entry, CancellationToken token)
    {
        try
        {
            var path = ArchivePath(entry.Id);
            if (!File.Exists(path)) return false;
            return await VerifyArchiveFileAsync(path, entry.Bytes, entry.Sha256, token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static async Task<bool> VerifyArchiveFileAsync(string path, long expectedBytes, string expectedHash, CancellationToken token)
    {
        await using var archiveStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var zip = new ZipArchive(archiveStream, ZipArchiveMode.Read);
        var draft = zip.GetEntry(DraftEntryName);
        if (draft is null) return false;
        await using var input = draft.Open();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long bytes = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            if (bytes + read > expectedBytes) return false;
            hash.AppendData(buffer, 0, read);
            bytes += read;
        }

        return bytes == expectedBytes
            && Convert.ToHexString(hash.GetHashAndReset()).Equals(expectedHash, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task CopyFileAsync(string source, string destination, CancellationToken token)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await input.CopyToAsync(output, 64 * 1024, token).ConfigureAwait(false);
        await output.FlushAsync(token).ConfigureAwait(false);
        output.Flush(true);
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
    }

    private static string ResolveDisplayName(byte[] draftBytes, SessionTab tab, string? explicitDisplayName)
    {
        if (!string.IsNullOrWhiteSpace(explicitDisplayName))
        {
            return ClampDisplayName(explicitDisplayName);
        }

        var text = Encoding.UTF8.GetString(draftBytes.AsSpan(0, Math.Min(draftBytes.Length, 256)));
        var firstLine = ClampDisplayName(text);
        if (!string.IsNullOrEmpty(firstLine))
        {
            return firstLine;
        }

        return !string.IsNullOrWhiteSpace(tab.CustomTitle) ? tab.CustomTitle : "Без имени";
    }

    private static string ReadDisplayNameFromFile(string path, SessionTab tab)
    {
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var prefix = new StringBuilder(40);
        for (var index = 0; index < 40; index++)
        {
            var character = reader.Read();
            if (character < 0 || character is '\r' or '\n') break;
            prefix.Append((char)character);
        }

        return prefix.Length == 0
            ? (!string.IsNullOrWhiteSpace(tab.CustomTitle) ? tab.CustomTitle : "Без имени")
            : prefix.ToString();
    }

    private static string ClampDisplayName(string value)
    {
        var prefix = new StringBuilder(40);
        foreach (var ch in value)
        {
            if (ch is '\r' or '\n' || prefix.Length >= 40) break;
            prefix.Append(ch);
        }

        return prefix.ToString().Trim();
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
