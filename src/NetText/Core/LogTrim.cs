using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NetText.Core;

public readonly record struct TrimFileIdentity(ulong VolumeSerial, ulong LowId, ulong HighId);

public sealed record TrimPlan(
    byte Percent,
    ulong OriginalSize,
    ulong RemovableBytes,
    ulong BoundaryOffset,
    TextEncoding Encoding,
    DateTime ModifiedUtc,
    TrimFileIdentity Identity,
    ulong RemovedLines = 0);

public sealed record TrimOutcome(
    string BackupPath,
    ulong RemovedBytes,
    ulong RemainingBytes);

public sealed class TrimInterruptedException : IOException
{
    public TrimInterruptedException(string message, Exception? inner = null)
        : base(message, inner) { }
}

internal sealed class TrimHooks
{
    public Action<long>? OnBackupChunkWritten { get; init; }
    public Action<long>? OnShiftChunkWritten { get; init; }
    public Func<string, int, string?>? CustomBackupPath { get; init; }
}

public static unsafe partial class LogTrim
{
    public const int BufferSize = 1024 * 1024;
    private const int FileBasicInfoClass = 0;
    private const int FileIdInfoClass = 18;

    [StructLayout(LayoutKind.Sequential)]
    private struct FILE_BASIC_INFO
    {
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public long ChangeTime;
        public uint FileAttributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILE_ID_INFO
    {
        public ulong VolumeSerialNumber;
        public fixed byte FileId[16];
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandleEx(
        SafeFileHandle hFile,
        int FileInformationClass,
        void* lpFileInformation,
        uint dwBufferSize);

    public static TrimPlan PrepareWithProgress(
        string path,
        byte percent,
        TextEncoding encoding,
        CancellationToken ct,
        Action<ulong, ulong>? progress = null)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        var plan = PrepareFromStream(path, stream, percent, encoding, ct, progress);
        if (plan.RemovableBytes == 0) return plan;
        var index = new LineIndex();
        while (index.ScannedTo < plan.BoundaryOffset)
        {
            ulong end = Math.Min(plan.BoundaryOffset, index.ScannedTo + 64 * 1024 * 1024);
            if (index.Advance(stream.SafeFileHandle, encoding, end, ct) == 0) break;
            progress?.Invoke(index.ScannedTo, plan.BoundaryOffset);
        }
        ulong lines = index.LineCount - 1;
        if (plan.BoundaryOffset == plan.OriginalSize)
        {
            int unit = TextFormat.UnitSize(encoding);
            Span<byte> suffix = stackalloc byte[2];
            if (plan.RemovableBytes >= (ulong)unit &&
                LogIo.ReadAt(stream.SafeFileHandle, plan.BoundaryOffset - (ulong)unit, suffix[..unit]) == unit &&
                !LogIo.IsNewlineUnit(suffix[..unit], encoding) && !LogIo.IsCarriageReturnUnit(suffix[..unit], encoding))
                lines++;
        }
        return plan with { RemovedLines = lines };
    }

    public static TrimPlan HiddenStartWithProgress(
        string path,
        byte percent,
        TextEncoding encoding,
        CancellationToken ct,
        Action<ulong, ulong>? progress = null)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return PrepareFromStream(path, stream, percent, encoding, ct, progress);
    }

    public static TrimOutcome Trim(
        string path,
        TrimPlan plan,
        CancellationToken ct,
        Action<ulong, ulong>? progress = null) =>
        TrimInternal(path, plan, ct, progress, null);

    internal static TrimOutcome TrimInternal(
        string path,
        TrimPlan plan,
        CancellationToken ct,
        Action<ulong, ulong>? progress,
        TrimHooks? hooks)
    {
        using var source = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None, BufferSize);
        ValidatePlan(path, source, plan);

        if (ct.IsCancellationRequested)
        {
            throw new TrimInterruptedException("Операция отменена; исходный файл не изменён");
        }

        string backupPath = CreateBackupPath(path, hooks);
        bool backupCompleted = false;
        try
        {
            using (var backup = new FileStream(backupPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize))
            {
                CopyToBackup(source, backup, plan.OriginalSize, ct, progress, hooks);
                backup.Flush(flushToDisk: true);
                backupCompleted = true;
            }
        }
        catch (Exception ex) when (!backupCompleted)
        {
            TryDeletePartialBackup(backupPath);
            if (ex is OperationCanceledException or TrimInterruptedException)
            {
                throw new TrimInterruptedException("Операция отменена; исходный файл не изменён", ex);
            }
            throw;
        }

        if (ct.IsCancellationRequested)
        {
            throw new TrimInterruptedException(
                $"Операция отменена до изменения файла. Полная резервная копия сохранена: {backupPath}");
        }

        try
        {
            ValidatePlan(path, source, plan);
        }
        catch (Exception ex)
        {
            throw new IOException(
                $"Файл изменился до начала обрезки; исходник не изменён. Резервная копия сохранена: {backupPath}. Причина: {ex.Message}",
                ex);
        }

        ulong bomLen = (ulong)Math.Min(TextFormat.BomLength(plan.Encoding), (int)Math.Min(plan.OriginalSize, 8));
        ulong retained = plan.OriginalSize - plan.BoundaryOffset;
        ulong finalSize = bomLen + retained;

        try
        {
            ShiftPrefix(source, bomLen, plan.BoundaryOffset, retained, hooks);
            source.SetLength((long)finalSize);
            source.Flush(flushToDisk: true);
            return new TrimOutcome(backupPath, plan.RemovableBytes, finalSize);
        }
        catch (Exception ex)
        {
            try
            {
                RestoreFromBackup(source, backupPath);
            }
            catch (Exception restoreEx)
            {
                throw new IOException(
                    $"Не удалось обрезать файл и восстановить исходник. Сохраните резервную копию: {backupPath}. Ошибка обработки: {ex.Message}; ошибка восстановления: {restoreEx.Message}",
                    ex);
            }

            throw new IOException(
                $"Не удалось обрезать файл; исходное содержимое восстановлено. Резервная копия: {backupPath}. Причина: {ex.Message}",
                ex);
        }
    }

    private static TrimPlan PrepareFromStream(
        string path,
        FileStream stream,
        byte percent,
        TextEncoding encoding,
        CancellationToken ct,
        Action<ulong, ulong>? progress)
    {
        if (percent is < 1 or > 100)
        {
            throw new ArgumentException("Процент обрезки должен быть от 1 до 100");
        }

        var (size, modified, identity) = ReadMeta(path, stream);
        ulong bom = (ulong)Math.Min(TextFormat.BomLength(encoding), (int)Math.Min(size, 8));
        ulong boundary = FindBoundary(stream.SafeFileHandle, size, bom, percent, encoding, ct, progress);
        ulong removable = boundary > bom ? boundary - bom : 0;

        return new TrimPlan(percent, size, removable, boundary, encoding, modified, identity);
    }

    private static ulong FindBoundary(
        SafeFileHandle handle,
        ulong size,
        ulong bom,
        byte percent,
        TextEncoding encoding,
        CancellationToken ct,
        Action<ulong, ulong>? progress)
    {
        ulong target = ((size / 100) * percent) + ((size % 100) * percent / 100);
        if (target < bom)
        {
            target = bom;
        }

        if (target >= size)
        {
            progress?.Invoke(0, 0);
            if (ct.IsCancellationRequested)
            {
                throw new TrimInterruptedException("Операция отменена; исходный файл не изменён");
            }
            return size;
        }

        int unit = TextFormat.UnitSize(encoding);
        ulong cursor = target;
        if (unit == 2 && ((cursor - bom) % 2) != 0)
        {
            cursor++;
        }

        if (cursor >= size)
        {
            return size;
        }

        if (IsLineStart(handle, cursor, bom, encoding))
        {
            progress?.Invoke(0, size - cursor);
            return cursor;
        }

        return FindBoundaryFrom(handle, cursor, size, bom, encoding, ct, progress);
    }

    private static bool IsLineStart(SafeFileHandle handle, ulong cursor, ulong bom, TextEncoding encoding)
    {
        if (cursor <= bom)
        {
            return true;
        }

        int unit = TextFormat.UnitSize(encoding);
        if (cursor < bom + (ulong)unit)
        {
            return false;
        }

        Span<byte> prev = stackalloc byte[2];
        if (LogIo.ReadAt(handle, cursor - (ulong)unit, prev[..unit]) < unit)
        {
            return false;
        }

        if (LogIo.IsNewlineUnit(prev[..unit], encoding))
        {
            return true;
        }

        if (!LogIo.IsCarriageReturnUnit(prev[..unit], encoding))
        {
            return false;
        }

        Span<byte> next = stackalloc byte[2];
        int n = LogIo.ReadAt(handle, cursor, next[..unit]);
        return n < unit || !LogIo.IsNewlineUnit(next[..unit], encoding);
    }

    private static ulong FindBoundaryFrom(
        SafeFileHandle handle,
        ulong cursor,
        ulong size,
        ulong bom,
        TextEncoding encoding,
        CancellationToken ct,
        Action<ulong, ulong>? progress)
    {
        int unit = TextFormat.UnitSize(encoding);
        bool le = encoding == TextEncoding.Utf16Le;
        var buf = new byte[BufferSize];
        ulong total = size - cursor;
        ulong pos = cursor;
        byte? carry = null;
        (ulong Start, int Width)? pendingCr = null;

        while (pos < size)
        {
            if (ct.IsCancellationRequested)
            {
                throw new TrimInterruptedException("Операция отменена; исходный файл не изменён");
            }

            int want = (int)Math.Min((ulong)buf.Length, size - pos);
            int read = LogIo.ReadAt(handle, pos, buf.AsSpan(0, want));
            if (read <= 0)
            {
                break;
            }

            int i = 0;
            if (unit == 2 && carry is byte cByte)
            {
                ushort u = le
                    ? (ushort)(cByte | (buf[0] << 8))
                    : (ushort)((cByte << 8) | buf[0]);
                ulong uStart = pos - 1;
                carry = null;
                i = 1;
                if (ProcessUnit(u, uStart, 2, ref pendingCr, out ulong end))
                {
                    return end;
                }
            }

            while (i + unit <= read)
            {
                ushort u = unit == 1
                    ? buf[i]
                    : (le ? BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(i)) : BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(i)));
                ulong uStart = pos + (ulong)i;
                i += unit;
                if (ProcessUnit(u, uStart, unit, ref pendingCr, out ulong end))
                {
                    return end;
                }
            }

            if (unit == 2 && i < read)
            {
                carry = buf[i];
            }

            pos += (ulong)read;
            progress?.Invoke(pos - cursor, total);
        }

        if (pendingCr is { Start: var crStart, Width: var crWidth })
        {
            return crStart + (ulong)crWidth;
        }

        return size;

        static bool ProcessUnit(ushort code, ulong uStart, int width, ref (ulong Start, int Width)? pendingCr, out ulong endOffset)
        {
            if (pendingCr is { Start: var crStart, Width: var crWidth })
            {
                pendingCr = null;
                if (code == '\n')
                {
                    endOffset = uStart + (ulong)width;
                    return true;
                }
                endOffset = crStart + (ulong)crWidth;
                return true;
            }

            if (code == '\n')
            {
                endOffset = uStart + (ulong)width;
                return true;
            }

            if (code == '\r')
            {
                pendingCr = (uStart, width);
            }

            endOffset = 0;
            return false;
        }
    }

    private static (ulong Size, DateTime ModifiedUtc, TrimFileIdentity Identity) ReadMeta(string path, FileStream stream)
    {
        FILE_ID_INFO idInfo;
        if (!GetFileInformationByHandleEx(stream.SafeFileHandle, FileIdInfoClass, &idInfo, (uint)sizeof(FILE_ID_INFO)))
        {
            int err = Marshal.GetLastPInvokeError();
            throw new IOException($"Не удалось прочитать идентификатор файла (Win32 {err})");
        }

        FILE_BASIC_INFO basic;
        DateTime modified;
        if (GetFileInformationByHandleEx(stream.SafeFileHandle, FileBasicInfoClass, &basic, (uint)sizeof(FILE_BASIC_INFO)) &&
            basic.LastWriteTime > 0)
        {
            modified = DateTime.FromFileTimeUtc(basic.LastWriteTime);
        }
        else
        {
            modified = File.GetLastWriteTimeUtc(path);
        }

        ulong size = (ulong)stream.Length;
        ReadOnlySpan<byte> idSpan = new ReadOnlySpan<byte>(idInfo.FileId, 16);
        ulong low = BinaryPrimitives.ReadUInt64LittleEndian(idSpan);
        ulong high = BinaryPrimitives.ReadUInt64LittleEndian(idSpan[8..]);
        return (size, modified, new TrimFileIdentity(idInfo.VolumeSerialNumber, low, high));
    }

    private static void ValidatePlan(string path, FileStream source, TrimPlan plan)
    {
        var (size, modified, identity) = ReadMeta(path, source);
        if (size != plan.OriginalSize || modified != plan.ModifiedUtc || identity != plan.Identity)
        {
            throw new IOException("Файл изменился после расчёта границы. Обновите позицию обрезки и повторите");
        }
        if (plan.BoundaryOffset > plan.OriginalSize)
        {
            throw new InvalidDataException("Граница обрезки находится за концом файла");
        }
    }

    private static string CreateBackupPath(string path, TrimHooks? hooks)
    {
        var dir = Path.GetDirectoryName(path);
        var name = Path.GetFileName(path);
        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(name))
        {
            throw new InvalidDataException("Не удалось определить имя файла");
        }

        long nanos = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000L;
        int pid = Environment.ProcessId;
        for (int attempt = 0; attempt <= 100; attempt++)
        {
            var candidate = hooks?.CustomBackupPath?.Invoke(path, attempt)
                ?? Path.Combine(dir, $"{name}.nettext-{nanos}-{pid}-{attempt}.bak");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new IOException("Не удалось подобрать имя резервной копии");
    }

    private static void CopyToBackup(
        FileStream source,
        FileStream backup,
        ulong totalSize,
        CancellationToken ct,
        Action<ulong, ulong>? progress,
        TrimHooks? hooks)
    {
        var buf = new byte[BufferSize];
        source.Position = 0;
        ulong copied = 0;

        while (copied < totalSize)
        {
            if (ct.IsCancellationRequested)
            {
                throw new TrimInterruptedException("Операция отменена; исходный файл не изменён");
            }

            int want = (int)Math.Min((ulong)buf.Length, totalSize - copied);
            int read = source.Read(buf, 0, want);
            if (read == 0)
            {
                throw new IOException("Файл неожиданно укоротился при создании копии");
            }

            backup.Write(buf, 0, read);
            copied += (ulong)read;
            hooks?.OnBackupChunkWritten?.Invoke((long)copied);
            progress?.Invoke(copied, totalSize);
        }
    }

    private static void ShiftPrefix(
        FileStream source,
        ulong writeStart,
        ulong readStart,
        ulong remaining,
        TrimHooks? hooks)
    {
        var buf = new byte[BufferSize];
        ulong copied = 0;

        while (copied < remaining)
        {
            int want = (int)Math.Min((ulong)buf.Length, remaining - copied);
            source.Position = (long)(readStart + copied);
            int read = source.Read(buf, 0, want);
            if (read == 0)
            {
                throw new IOException("Файл неожиданно укоротился при переносе строк");
            }

            source.Position = (long)(writeStart + copied);
            source.Write(buf, 0, read);
            copied += (ulong)read;
            hooks?.OnShiftChunkWritten?.Invoke((long)copied);
        }
    }

    private static void RestoreFromBackup(FileStream source, string backupPath)
    {
        using var backup = new FileStream(backupPath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize);
        var buf = new byte[BufferSize];
        source.Position = 0;
        long total = 0;
        while (true)
        {
            int read = backup.Read(buf, 0, buf.Length);
            if (read == 0)
            {
                break;
            }
            source.Write(buf, 0, read);
            total += read;
        }
        source.SetLength(total);
        source.Flush(flushToDisk: true);
    }

    private static void TryDeletePartialBackup(string backupPath)
    {
        try
        {
            if (File.Exists(backupPath))
            {
                File.Delete(backupPath);
            }
        }
        catch
        {
        }
    }
}
