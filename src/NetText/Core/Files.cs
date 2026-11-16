using System.IO;
using System.Runtime.InteropServices;
using System.Text.Unicode;

namespace NetText.Core;

public sealed unsafe class NativeOpenedFile : IDisposable
{
    private byte* _nativePtr;
    private readonly int _offset;
    private readonly byte[]? _managed;

    public FileTarget Target { get; }
    public TextFormat Format { get; }
    public int Length { get; }

    internal NativeOpenedFile(FileTarget target, TextFormat format, byte* nativePtr, int offset, int length)
    {
        Target = target;
        Format = format;
        _nativePtr = nativePtr;
        _offset = offset;
        Length = length;
    }

    internal NativeOpenedFile(FileTarget target, TextFormat format, byte[] managed)
    {
        Target = target;
        Format = format;
        _managed = managed;
        Length = managed.Length;
    }

    public ReadOnlySpan<byte> Span =>
        _managed is not null
            ? _managed.AsSpan()
            : _nativePtr is not null
                ? new ReadOnlySpan<byte>(_nativePtr + _offset, Length)
                : ReadOnlySpan<byte>.Empty;

    public void Dispose()
    {
        if (_nativePtr is not null)
        {
            NativeMemory.Free(_nativePtr);
            _nativePtr = null;
        }
    }
}

internal static unsafe partial class NativeFileAttributes
{
    private const uint ErrorFileNotFound = 2;
    private const uint ErrorPathNotFound = 3;
    private const uint ErrorAccessDenied = 5;
    private const uint FileAttributeDirectory = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    internal struct FILETIME
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;

        public DateTime ToUtcDateTime()
        {
            long ft = ((long)dwHighDateTime << 32) | dwLowDateTime;
            return DateTime.FromFileTimeUtc(ft);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WIN32_FILE_ATTRIBUTE_DATA
    {
        public uint dwFileAttributes;
        public FILETIME ftCreationTime;
        public FILETIME ftLastAccessTime;
        public FILETIME ftLastWriteTime;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileAttributesExW(
        string lpFileName,
        int fInfoLevelId,
        WIN32_FILE_ATTRIBUTE_DATA* lpFileInformation);

    public static FileVersion? TryGetVersion(string path)
    {
        WIN32_FILE_ATTRIBUTE_DATA data;
        if (!GetFileAttributesExW(path, 0, &data))
        {
            int err = Marshal.GetLastPInvokeError();
            if (err is (int)ErrorFileNotFound or (int)ErrorPathNotFound)
            {
                return null;
            }
            if (err == (int)ErrorAccessDenied)
            {
                throw new UnauthorizedAccessException($"Access denied reading attributes for '{path}' (Win32 {err})");
            }
            throw new IOException($"Failed to query attributes for '{path}' (Win32 {err})", err);
        }

        if ((data.dwFileAttributes & FileAttributeDirectory) != 0)
        {
            throw new InvalidDataException("Выберите обычный файл");
        }

        long bytes = ((long)data.nFileSizeHigh << 32) | data.nFileSizeLow;
        return new FileVersion(bytes, data.ftLastWriteTime.ToUtcDateTime(), data.ftCreationTime.ToUtcDateTime());
    }
}

public readonly record struct FileVersion(long Bytes, DateTime ModifiedUtc, DateTime CreatedUtc)
{
    public static FileVersion? At(string path) => NativeFileAttributes.TryGetVersion(path);
}

public sealed record FileTarget(string Path, FileVersion? Version)
{
    public static FileTarget FromPicker(string path)
    {
        var fullPath = System.IO.Path.GetFullPath(path);
        var version = FileVersion.At(fullPath);
        if (version is null)
        {
            var name = System.IO.Path.GetFileName(fullPath);
            if (string.IsNullOrEmpty(name))
            {
                throw new InvalidDataException("Укажите имя файла");
            }
            var dir = System.IO.Path.GetDirectoryName(fullPath);
            if (string.IsNullOrEmpty(dir))
            {
                throw new InvalidDataException("Укажите папку файла");
            }
        }
        return new FileTarget(fullPath, version);
    }
}

public sealed record OpenedFile(FileTarget Target, byte[] Utf8Bytes, TextFormat Format);

public static class Files
{
    public const long MaxEditorBytes = 50L * 1024 * 1024;

    public static OpenedFile Open(string path, bool windows1251)
    {
        var fullPath = Path.GetFullPath(path);
        var version = FileVersion.At(fullPath)
            ?? throw new FileNotFoundException("Файл не найден", fullPath);
        if (version.Bytes > MaxEditorBytes)
        {
            throw FileTooLarge();
        }

        byte[] bytes;
        using (var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete))
        {
            long length = stream.Length;
            if (length > MaxEditorBytes)
            {
                throw FileTooLarge();
            }

            int expected = (int)length;
            bytes = GC.AllocateUninitializedArray<byte>(expected);
            int offset = 0;
            while (offset < expected)
            {
                int read = stream.Read(bytes, offset, expected - offset);
                if (read == 0)
                {
                    break;
                }
                offset += read;
            }
            if (offset != expected)
            {
                bytes = bytes[..offset];
            }
            else if (stream.ReadByte() != -1)
            {
                throw FileTooLarge();
            }
        }

        if (FileVersion.At(fullPath) != version)
        {
            throw FileChanged();
        }

        var (utf8Bytes, format) = TextFormat.DecodeOwnedToUtf8(bytes, windows1251);
        return new OpenedFile(new FileTarget(fullPath, version), utf8Bytes, format);
    }

    public static unsafe NativeOpenedFile OpenForEditor(string path, bool windows1251)
    {
        var fullPath = Path.GetFullPath(path);
        var version = FileVersion.At(fullPath)
            ?? throw new FileNotFoundException("Файл не найден", fullPath);
        if (version.Bytes > MaxEditorBytes)
        {
            throw FileTooLarge();
        }

        byte* nativePtr = null;
        int actualLen = 0;
        try
        {
            using var handle = File.OpenHandle(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.SequentialScan);
            long length = RandomAccess.GetLength(handle);
            if (length > MaxEditorBytes)
            {
                throw FileTooLarge();
            }

            int expected = (int)length;
            nativePtr = (byte*)NativeMemory.Alloc((nuint)Math.Max(1, expected));
            Span<byte> dest = new(nativePtr, expected);
            while (actualLen < expected)
            {
                int read = RandomAccess.Read(handle, dest[actualLen..], actualLen);
                if (read == 0) break;
                actualLen += read;
            }
            if (actualLen == expected)
            {
                Span<byte> eofProbe = stackalloc byte[1];
                if (RandomAccess.Read(handle, eofProbe, expected) > 0)
                {
                    throw FileTooLarge();
                }
            }

            if (FileVersion.At(fullPath) != version)
            {
                throw FileChanged();
            }

            Span<byte> raw = new(nativePtr, actualLen);
            bool needsTranscode = windows1251 ||
                raw is [0xFF, 0xFE, ..] ||
                raw is [0xFE, 0xFF, ..];
            if (!needsTranscode)
            {
                int bomOff = 0;
                TextEncoding enc = TextEncoding.Utf8;
                if (raw is [0xEF, 0xBB, 0xBF, ..])
                {
                    enc = TextEncoding.Utf8Bom;
                    bomOff = 3;
                }

                Span<byte> content = raw[bomOff..];
                if (!Utf8.IsValid(content))
                {
                    throw new InvalidDataException(
                        "Ожидается UTF-8 или UTF-16 с BOM. Для Windows-1251 выберите кодировку при открытии");
                }
                if (content.IndexOf((byte)0) >= 0)
                {
                    throw new InvalidDataException("Файл содержит нулевые символы");
                }

                var endings = TextFormat.DetectLineEndings(content);
                if (endings == LineEndings.Cr)
                {
                    content.Replace((byte)'\r', (byte)'\n');
                }

                var owned = new NativeOpenedFile(new FileTarget(fullPath, version), new TextFormat(enc, endings), nativePtr, bomOff, content.Length);
                nativePtr = null;
                return owned;
            }

            var (utf8Bytes, format) = TextFormat.DecodeToUtf8(raw, windows1251);
            return new NativeOpenedFile(new FileTarget(fullPath, version), format, utf8Bytes);
        }
        finally
        {
            if (nativePtr is not null)
            {
                NativeMemory.Free(nativePtr);
            }
        }
    }

    public static FileTarget Save(FileTarget target, TextFormat format, ReadOnlySpan<byte> utf8Bytes)
    {
        var bytes = format.EncodeFromUtf8(utf8Bytes);
        return SaveEncoded(target, bytes);
    }

    public static FileTarget SaveText(FileTarget target, TextFormat format, string text)
    {
        var bytes = format.Encode(text);
        return SaveEncoded(target, bytes);
    }

    private static FileTarget SaveEncoded(FileTarget target, byte[] bytes)
    {
        var directory = Path.GetDirectoryName(target.Path);
        if (string.IsNullOrEmpty(directory))
        {
            throw new InvalidDataException("Укажите папку файла");
        }

        var tempPath = Path.Combine(directory, $".nettext-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var pending = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 65536,
                FileOptions.WriteThrough))
            {
                pending.Write(bytes);
                pending.Flush(flushToDisk: true);
            }

            if (FileVersion.At(target.Path) != target.Version)
            {
                throw FileChanged();
            }

            if (target.Version is not null)
            {
                File.Move(tempPath, target.Path, overwrite: true);
            }
            else
            {
                File.Move(tempPath, target.Path, overwrite: false);
            }

            var newVersion = FileVersion.At(target.Path);
            return target with { Version = newVersion };
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
    }

    private static InvalidDataException FileTooLarge() =>
        new("Лимит редактора – 50 МиБ. Просмотр больших файлов появится на следующем этапе");

    public static bool ShouldReplaceOpenedDocument(
        bool dirty,
        bool placeholder,
        FileTarget? currentTarget,
        FileTarget openedTarget,
        TextEncoding currentEncoding,
        TextEncoding openedEncoding) =>
        !dirty && (placeholder || currentTarget != openedTarget || currentEncoding != openedEncoding);

    private static IOException FileChanged() =>
        new("Файл изменился на диске. Откройте его заново или сохраните правки в отдельный файл");
}
