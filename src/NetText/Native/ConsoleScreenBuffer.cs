using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NetText.Native;

internal sealed partial class ConsoleScreenBuffer : IDisposable
{
    private const uint GenericRead = 0x80000000;
    private const uint FileShareReadWrite = 0x00000003;
    private const uint OpenExisting = 3;
    private const ushort CommonLvbTrailingByte = 0x0200;
    private const int MaxReadCells = 8192;

    private readonly SafeFileHandle _output;
    private readonly ConsoleScrollTracker? _scrollTracker;
    private readonly ConsoleLineDeduplicator _deduplicator = new();

    public ConsoleScreenBuffer(int processId)
    {
        FreeConsole();
        if (!AttachConsole((uint)processId))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Не удалось подключиться к консоли процесса.");
        }

        SafeFileHandle? output = null;
        try
        {
            if (!SetConsoleCtrlHandler(0, true))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Не удалось настроить обработку Ctrl+C.");
            }

            output = CreateFileW("CONOUT$", GenericRead, FileShareReadWrite, 0, OpenExisting, 0, 0);
            if (output.IsInvalid)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Не удалось открыть буфер консоли.");
            }

            _output = output;
            _scrollTracker = ConsoleScrollTracker.TryCreate();
        }
        catch
        {
            output?.Dispose();
            FreeConsole();
            throw;
        }
    }

    public string[] ReadNewRows(CancellationToken token)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            token.ThrowIfCancellationRequested();
            var before = GetBufferInfo();
            var scrollBatch = _scrollTracker?.ReadEvents(before.Size.Y - 1) ?? default;
            var snapshot = ToSnapshot(before);
            var plan = _deduplicator.PrepareRead(snapshot, scrollBatch, _scrollTracker is not null);

            string[]? lastRow = plan.AtBottom && plan.EndRow > 0
                ? ReadRows(before.Size.X, plan.EndRow - 1, plan.EndRow, token)
                : null;
            string[]? rows = ReadRows(before.Size.X, plan.FirstRow, plan.EndRow, token);
            if (rows is null) continue;

            int boundary = _deduplicator.FindBoundary(
                rows,
                plan.FirstRow,
                plan.ExpectedNewRows,
                plan.Advance,
                plan.AtBottom,
                scrollBatch.HistoryRedrawn,
                plan.Continuous,
                _scrollTracker is not null);

            if (boundary < 0)
            {
                rows = ReadRows(before.Size.X, 0, plan.EndRow, token);
                if (rows is null) continue;
                boundary = _deduplicator.FindBoundary(
                    rows,
                    0,
                    plan.ExpectedNewRows,
                    plan.Advance,
                    plan.AtBottom,
                    scrollBatch.HistoryRedrawn,
                    plan.Continuous,
                    _scrollTracker is not null);
            }

            var after = GetBufferInfo();
            var eventsAfter = _scrollTracker?.ReadEvents(after.Size.Y - 1) ?? default;
            bool moreEvents = eventsAfter.EventVersion != scrollBatch.EventVersion;
            if (moreEvents || before.Size != after.Size || before.Cursor != after.Cursor ||
                before.Window.Top != after.Window.Top ||
                (lastRow is { Length: > 0 } && rows.Length > 0 && lastRow[0] != rows[^1]))
            {
                continue;
            }

            string[] added = _deduplicator.Commit(snapshot, rows, boundary, plan, _scrollTracker is not null);
            _scrollTracker?.ClearEvents();
            return added;
        }

        return [];
    }

    private BufferInfo GetBufferInfo()
    {
        if (!GetConsoleScreenBufferInfo(_output, out var info))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Не удалось прочитать буфер консоли.");
        }
        return info;
    }

    private unsafe string[]? ReadRows(short width, int first, int end, CancellationToken token)
    {
        int rowCount = end - first;
        if (rowCount <= 0 || width <= 0) return [];

        var rows = new string[rowCount];
        int chunkRows = Math.Min(rows.Length, Math.Max(1, MaxReadCells / width));
        var cells = new CharInfo[width * chunkRows];
        var text = new char[width];

        fixed (CharInfo* buffer = cells)
        {
            for (int start = first; start < end; start += chunkRows)
            {
                token.ThrowIfCancellationRequested();
                short height = (short)Math.Min(chunkRows, end - start);
                var expectedArea = new SmallRect(0, (short)start, (short)(width - 1), (short)(start + height - 1));
                var area = expectedArea;

                if (!ReadConsoleOutputW(_output, buffer, new Coord(width, height), default, ref area))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "Не удалось прочитать вывод консоли.");
                }
                if (area != expectedArea)
                {
                    return null;
                }

                for (int row = 0; row < height; row++)
                {
                    int length = 0;
                    for (int column = 0; column < width; column++)
                    {
                        var cell = cells[row * width + column];
                        if ((cell.Attributes & CommonLvbTrailingByte) != 0) continue;
                        text[length++] = cell.UnicodeChar is 0 or '\r' or '\n' ? ' ' : (char)cell.UnicodeChar;
                    }
                    while (length > 0 && text[length - 1] == ' ') length--;
                    rows[start - first + row] = new string(text, 0, length);
                }
            }
        }

        return rows;
    }

    public void Dispose()
    {
        _scrollTracker?.Dispose();
        _output.Dispose();
        FreeConsole();
    }

    private static ConsoleBufferSnapshot ToSnapshot(BufferInfo info) => new(
        info.Size.X,
        info.Size.Y,
        info.Cursor.X,
        info.Cursor.Y,
        info.Window.Top,
        info.Window.Bottom);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Coord(short X, short Y);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct SmallRect(short Left, short Top, short Right, short Bottom);

    [StructLayout(LayoutKind.Sequential)]
    private struct BufferInfo
    {
        public Coord Size;
        public Coord Cursor;
        public ushort Attributes;
        public SmallRect Window;
        public Coord MaximumWindowSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CharInfo
    {
        public ushort UnicodeChar;
        public ushort Attributes;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(uint dwProcessId);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FreeConsole();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetConsoleCtrlHandler(nint handlerRoutine, [MarshalAs(UnmanagedType.Bool)] bool add);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        nint lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        nint hTemplateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetConsoleScreenBufferInfo(SafeFileHandle hConsoleOutput, out BufferInfo lpConsoleScreenBufferInfo);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool ReadConsoleOutputW(
        SafeFileHandle hConsoleOutput,
        CharInfo* lpBuffer,
        Coord dwBufferSize,
        Coord dwBufferCoord,
        ref SmallRect lpReadRegion);
}
