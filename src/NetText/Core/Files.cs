using System.IO;
using System.Runtime.InteropServices;
using System.Text.Unicode;
using Microsoft.Win32.SafeHandles;

namespace NetText.Core;

public sealed unsafe class NativeOpenedFile : IDisposable
{
    private byte* _nativePtr;
    private readonly int _nativeOffset;
    private readonly byte[]? _managed;
    private readonly string? _streamedPath;
    private readonly int _streamOffset;

    public FileTarget Target { get; }
    public TextFormat Format { get; }
    public int Length { get; }
    public ulong? LineCount { get; }
    public bool IsStreamed => _streamedPath is not null;

    internal NativeOpenedFile(FileTarget target, TextFormat format, byte* nativePtr, int offset, int length, ulong? lineCount = null)
    {
        Target = target;
        Format = format;
        _nativePtr = nativePtr;
        _nativeOffset = offset;
        Length = length;
        LineCount = lineCount;
    }

    internal NativeOpenedFile(FileTarget target, TextFormat format, byte[] managed, ulong? lineCount = null)
    {
        Target = target;
        Format = format;
        _managed = managed;
        Length = managed.Length;
        LineCount = lineCount;
    }

    internal NativeOpenedFile(FileTarget target, TextFormat format, string streamedPath, int streamOffset, int length, ulong lineCount)
    {
        Target = target;
        Format = format;
        _streamedPath = streamedPath;
        _streamOffset = streamOffset;
        Length = length;
        LineCount = lineCount;
    }

    public ReadOnlySpan<byte> Span
    {
        get
        {
            if (_managed is not null)
            {
                return _managed.AsSpan();
            }
            if (_nativePtr is null && _streamedPath is not null && Length > 0)
            {
                MaterializeStreamed();
            }
            return _nativePtr is not null
                ? new ReadOnlySpan<byte>(_nativePtr + _nativeOffset, Length)
                : ReadOnlySpan<byte>.Empty;
        }
    }

    public void StreamUtf8Chunks(Action<ReadOnlySpan<byte>> onChunk)
    {
        if (!IsStreamed)
        {
            if (Length > 0)
            {
                onChunk(Span);
            }
            return;
        }

        if (Length == 0)
        {
            if (FileVersion.At(_streamedPath!) != Target.Version)
            {
                throw Files.FileChanged();
            }
            return;
        }

        const int ChunkBytes = 1024 * 1024;
        byte* buf = (byte*)NativeMemory.Alloc(ChunkBytes);
        try
        {
            using var handle = File.OpenHandle(_streamedPath!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.SequentialScan);
            long filePos = _streamOffset;
            int remaining = Length;
            while (remaining > 0)
            {
                int want = Math.Min(ChunkBytes, remaining);
                int got = 0;
                while (got < want)
                {
                    int read = RandomAccess.Read(handle, new Span<byte>(buf + got, want - got), filePos + got);
                    if (read == 0)
                    {
                        throw Files.FileChanged();
                    }
                    got += read;
                }

                Span<byte> chunk = new(buf, got);
                if (Format.Endings == LineEndings.Cr)
                {
                    chunk.Replace((byte)'\r', (byte)'\n');
                }
                onChunk(chunk);
                filePos += got;
                remaining -= got;
            }

            if (FileVersion.At(_streamedPath!) != Target.Version)
            {
                throw Files.FileChanged();
            }
        }
        finally
        {
            NativeMemory.Free(buf);
        }
    }

    private void MaterializeStreamed()
    {
        byte* buf = (byte*)NativeMemory.Alloc((nuint)Length);
        try
        {
            using var handle = File.OpenHandle(_streamedPath!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.SequentialScan);
            int got = 0;
            while (got < Length)
            {
                int read = RandomAccess.Read(handle, new Span<byte>(buf + got, Length - got), _streamOffset + got);
                if (read == 0)
                {
                    throw Files.FileChanged();
                }
                got += read;
            }

            if (FileVersion.At(_streamedPath!) != Target.Version)
            {
                throw Files.FileChanged();
            }

            if (Format.Endings == LineEndings.Cr)
            {
                new Span<byte>(buf, Length).Replace((byte)'\r', (byte)'\n');
            }

            _nativePtr = buf;
            buf = null;
        }
        finally
        {
            if (buf is not null)
            {
                NativeMemory.Free(buf);
            }
        }
    }

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

        using var handle = File.OpenHandle(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.SequentialScan);
        long length = RandomAccess.GetLength(handle);
        if (length > MaxEditorBytes)
        {
            throw FileTooLarge();
        }

        if (!windows1251 && version.Bytes > 2L * 1024 * 1024)
        {
            Span<byte> bom = stackalloc byte[3];
            int bomRead = 0;
            while (bomRead < 3)
            {
                int r = RandomAccess.Read(handle, bom[bomRead..], bomRead);
                if (r == 0) break;
                bomRead += r;
            }

            bool isUtf16Bom = bomRead >= 2 &&
                ((bom[0] == 0xFF && bom[1] == 0xFE) || (bom[0] == 0xFE && bom[1] == 0xFF));
            if (!isUtf16Bom)
            {
                return OpenStreamedUtf8ForEditor(fullPath, version, handle, (int)length, bom[..bomRead]);
            }
        }

        byte* nativePtr = null;
        int actualLen = 0;
        try
        {
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

    private static unsafe NativeOpenedFile OpenStreamedUtf8ForEditor(
        string fullPath,
        FileVersion version,
        SafeFileHandle handle,
        int expected,
        ReadOnlySpan<byte> bomPeek)
    {
        const int ChunkBytes = 1024 * 1024;
        int bomOff = 0;
        TextEncoding enc = TextEncoding.Utf8;
        if (bomPeek is [0xEF, 0xBB, 0xBF, ..])
        {
            bomOff = 3;
            enc = TextEncoding.Utf8Bom;
        }

        byte* buf = (byte*)NativeMemory.Alloc(ChunkBytes + 4);
        try
        {
            long diskPos = bomOff;
            int carryLen = 0;
            bool hasLf = false;
            bool hasCrLf = false;
            bool hasCr = false;
            bool pendingCr = false;
            ulong lines = 1;

            while (diskPos < expected)
            {
                int want = (int)Math.Min(ChunkBytes, expected - diskPos);
                int got = 0;
                while (got < want)
                {
                    int read = RandomAccess.Read(handle, new Span<byte>(buf + carryLen + got, want - got), diskPos + got);
                    if (read == 0)
                    {
                        break;
                    }
                    got += read;
                }
                if (got == 0)
                {
                    break;
                }

                diskPos += got;
                int total = carryLen + got;
                bool moreToRead = got == want && diskPos < expected;
                int validLen = moreToRead ? LogIo.TrimUtf8Boundary(new ReadOnlySpan<byte>(buf, total)) : total;
                int nextCarry = total - validLen;
                if (nextCarry > 3)
                {
                    throw new InvalidDataException(
                        "Ожидается UTF-8 или UTF-16 с BOM. Для Windows-1251 выберите кодировку при открытии");
                }

                if (validLen > 0)
                {
                    ReadOnlySpan<byte> valid = new(buf, validLen);
                    if (!Utf8.IsValid(valid))
                    {
                        throw new InvalidDataException(
                            "Ожидается UTF-8 или UTF-16 с BOM. Для Windows-1251 выберите кодировку при открытии");
                    }
                    if (valid.IndexOf((byte)0) >= 0)
                    {
                        throw new InvalidDataException("Файл содержит нулевые символы");
                    }

                    ScanLinesAndEndings(valid, ref pendingCr, ref hasLf, ref hasCrLf, ref hasCr, ref lines);
                }

                if (nextCarry > 0)
                {
                    new ReadOnlySpan<byte>(buf + validLen, nextCarry).CopyTo(new Span<byte>(buf, nextCarry));
                }
                carryLen = nextCarry;
            }

            if (carryLen > 0)
            {
                ReadOnlySpan<byte> tail = new(buf, carryLen);
                if (!Utf8.IsValid(tail))
                {
                    throw new InvalidDataException(
                        "Ожидается UTF-8 или UTF-16 с BOM. Для Windows-1251 выберите кодировку при открытии");
                }
                if (tail.IndexOf((byte)0) >= 0)
                {
                    throw new InvalidDataException("Файл содержит нулевые символы");
                }
                ScanLinesAndEndings(tail, ref pendingCr, ref hasLf, ref hasCrLf, ref hasCr, ref lines);
            }

            if (pendingCr)
            {
                hasCr = true;
                lines++;
            }

            if (diskPos == expected)
            {
                Span<byte> eofProbe = stackalloc byte[1];
                if (RandomAccess.Read(handle, eofProbe, expected) > 0)
                {
                    throw FileTooLarge();
                }
            }

            if (diskPos != expected || FileVersion.At(fullPath) != version)
            {
                throw FileChanged();
            }

            int kindCount = (hasLf ? 1 : 0) + (hasCrLf ? 1 : 0) + (hasCr ? 1 : 0);
            LineEndings endings = kindCount > 1
                ? LineEndings.Mixed
                : hasCrLf
                    ? LineEndings.CrLf
                    : hasCr
                        ? LineEndings.Cr
                        : LineEndings.Lf;

            return new NativeOpenedFile(
                new FileTarget(fullPath, version),
                new TextFormat(enc, endings),
                fullPath,
                bomOff,
                Math.Max(0, expected - bomOff),
                lines);
        }
        finally
        {
            NativeMemory.Free(buf);
        }
    }

    private static void ScanLinesAndEndings(
        ReadOnlySpan<byte> span,
        ref bool pendingCr,
        ref bool hasLf,
        ref bool hasCrLf,
        ref bool hasCr,
        ref ulong lines)
    {
        int i = 0;
        if (pendingCr)
        {
            pendingCr = false;
            lines++;
            if (span[0] == (byte)'\n')
            {
                hasCrLf = true;
                i = 1;
            }
            else
            {
                hasCr = true;
            }
        }

        while (i < span.Length)
        {
            int rel = span[i..].IndexOfAny((byte)'\n', (byte)'\r');
            if (rel < 0)
            {
                break;
            }

            i += rel;
            if (span[i] == (byte)'\n')
            {
                hasLf = true;
                lines++;
                i++;
            }
            else if (i + 1 < span.Length)
            {
                if (span[i + 1] == (byte)'\n')
                {
                    hasCrLf = true;
                    lines++;
                    i += 2;
                }
                else
                {
                    hasCr = true;
                    lines++;
                    i++;
                }
            }
            else
            {
                pendingCr = true;
                i++;
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

    private static FileTarget SaveEncoded(FileTarget target, byte[] bytes) =>
        WriteAtomically(target, stream => stream.Write(bytes));

    internal static FileTarget WriteAtomically(FileTarget target, Action<FileStream> write, CancellationToken cancellationToken = default)
    {
        var path = target.Version is not null ? File.ResolveLinkTarget(target.Path, returnFinalTarget: true)?.FullName ?? target.Path : target.Path;
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory))
        {
            throw new InvalidDataException("Укажите папку файла");
        }

        var tempPath = Path.Combine(directory, $".nettext-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var pending = WindowsFileSave.CreatePending(tempPath))
            {
                write(pending);
                pending.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (FileVersion.At(target.Path) != target.Version)
            {
                throw FileChanged();
            }

            if (target.Version is { } version)
            {
                WindowsFileSave.Replace(tempPath, path, version);
            }
            else
            {
                File.Move(tempPath, path, overwrite: false);
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

    internal static IOException FileChanged() =>
        new("Файл изменился на диске. Откройте его заново или сохраните правки в отдельный файл");
}
