using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetText.Core;

internal sealed record FileSnapshot(string Id, string Path, DateTimeOffset Captured, TextEncoding Encoding, int Size, string Hash);

[JsonSerializable(typeof(List<FileSnapshot>))]
internal partial class FileVersionsJson : JsonSerializerContext;

internal sealed class FileVersions
{
    private readonly string _directory;
    private string IndexPath => Path.Combine(_directory, "index.json");

    public FileVersions(string? directory = null)
    {
        _directory = Path.GetFullPath(directory ?? Path.Combine(AppPaths.RequireLocalAppData(), "NetText", "versions"));
    }

    private FileStream Lock()
    {
        Directory.CreateDirectory(_directory);
        return RecentFiles.AcquireLock(Path.Combine(_directory, "versions.lock"));
    }

    private List<FileSnapshot> ReadIndex() => File.Exists(IndexPath)
        ? JsonSerializer.Deserialize(File.ReadAllBytes(IndexPath), FileVersionsJson.Default.ListFileSnapshot) ?? []
        : [];

    public IReadOnlyList<FileSnapshot> List(string path)
    {
        using var guard = Lock();
        return ReadIndex().Where(snapshot => SamePath(snapshot.Path, path)).ToArray();
    }

    public void Capture(FileTarget target, TextEncoding encoding, EditorSettings settings, bool required = false)
    {
        if (target.Version is not { } expected) return;
        if (expected.Bytes > settings.VersionFileBytes || expected.Bytes > settings.VersionTotalBytes)
        {
            if (required) throw new IOException("Текущий файл превышает лимит хранения версий.");
            return;
        }

        if (Path.GetFullPath(target.Path).StartsWith(_directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Нельзя сохранять версии файлов хранилища.");

        using var guard = Lock();
        var entries = ReadIndex();
        if (FileVersion.At(target.Path) != expected) throw Changed();
        var bytes = new byte[checked((int)expected.Bytes)];
        using (var input = new FileStream(target.Path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            input.ReadExactly(bytes);
            if (input.ReadByte() != -1 || FileVersion.At(target.Path) != expected) throw Changed();
        }

        string hash = Convert.ToHexString(SHA256.HashData(bytes));
        var previous = entries.Find(item => SamePath(item.Path, target.Path));
        if (previous?.Hash == hash && previous.Encoding == encoding)
        {
            _ = ReadBytes(previous);
            return;
        }

        var snapshot = new FileSnapshot(Guid.NewGuid().ToString("N"), Path.GetFullPath(target.Path),
            DateTimeOffset.UtcNow, encoding, bytes.Length, hash);
        string path = BlobPath(snapshot.Id);
        Files.WriteAtomically(new FileTarget(path, null), stream => stream.Write(bytes));
        entries.Insert(0, snapshot);
        var removed = new List<FileSnapshot>();
        long total = 0;
        foreach (var item in entries)
        {
            if (item.Size > settings.VersionTotalBytes - total) removed.Add(item);
            else total += item.Size;
        }
        entries.RemoveAll(removed.Contains);
        try
        {
            Files.WriteAtomically(FileTarget.FromPicker(IndexPath), stream =>
                JsonSerializer.Serialize(stream, entries, FileVersionsJson.Default.ListFileSnapshot));
        }
        catch
        {
            File.Delete(path);
            throw;
        }

        foreach (var item in removed) File.Delete(BlobPath(item.Id));
    }

    public (string Text, TextFormat Format) Read(FileSnapshot snapshot)
    {
        using var guard = Lock();
        if (!ReadIndex().Contains(snapshot)) throw new FileNotFoundException("Версия не найдена.");
        var decoded = TextFormat.Decode(ReadBytes(snapshot), snapshot.Encoding == TextEncoding.Windows1251);
        if (decoded.Format.Encoding != snapshot.Encoding) throw new InvalidDataException("Кодировка версии изменилась.");
        return decoded;
    }

    private byte[] ReadBytes(FileSnapshot item)
    {
        if (item.Size < 0 || item.Size > Files.MaxEditorBytes) throw new InvalidDataException("Некорректный размер версии.");
        var bytes = new byte[item.Size];
        using var input = File.OpenRead(BlobPath(item.Id));
        input.ReadExactly(bytes);
        if (input.ReadByte() != -1 || Convert.ToHexString(SHA256.HashData(bytes)) != item.Hash)
            throw new InvalidDataException("Контрольная сумма версии не совпадает.");
        return bytes;
    }

    private string BlobPath(string id) => Guid.TryParseExact(id, "N", out var guid)
        ? Path.Combine(_directory, guid.ToString("N") + ".bin")
        : throw new InvalidDataException("Некорректный номер версии.");

    private static IOException Changed() => new("Файл изменился при сохранении версии.");

    private static bool SamePath(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
