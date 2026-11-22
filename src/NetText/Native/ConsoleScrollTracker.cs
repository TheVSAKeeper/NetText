using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace NetText.Native;

internal readonly record struct ConsoleBufferSnapshot(
    short Width,
    short Height,
    short CursorX,
    short CursorY,
    short WindowTop,
    short WindowBottom);

internal readonly record struct ConsoleEventBatch(
    long Scroll,
    int ScrollCount,
    int FirstScrollDelta,
    bool LayoutChanged,
    bool LeadingLayout,
    int BottomRegionRows,
    bool HistoryRedrawn,
    long EventVersion);

internal readonly record struct ConsoleReadPlan(
    bool Continuous,
    int FirstRow,
    int EndRow,
    int Advance,
    int? ExpectedNewRows,
    long ReportedScroll,
    bool AtBottom);

internal sealed class ConsoleLineDeduplicator
{
    public const int TailRows = 64;

    private ConsoleBufferSnapshot? _previous;
    private string[] _tail = [];
    private long _scrollAlreadyCaptured;

    public ConsoleReadPlan PrepareRead(ConsoleBufferSnapshot current, ConsoleEventBatch events, bool hasScrollTracker)
    {
        int end = current.CursorY;
        var previous = _previous.GetValueOrDefault();
        bool continuous = _previous.HasValue &&
            previous.Width == current.Width &&
            previous.Height == current.Height &&
            end >= previous.CursorY;
        int advance = continuous ? end - previous.CursorY : 0;

        bool atBottom = end == current.Height - 1;
        int viewportAdvance = current.WindowTop - previous.WindowTop;
        long reported = GetReportedScroll(
            viewportAdvance,
            events,
            current.CursorX <= previous.CursorX,
            previous.WindowBottom < previous.CursorY);
        long inferred = atBottom ? Math.Max(0L, reported - _scrollAlreadyCaptured) : 0;
        int expected = (int)Math.Min(end, advance + inferred);
        int first = continuous ? Math.Max(0, end - expected - TailRows) : 0;

        return new ConsoleReadPlan(continuous, first, end, advance, continuous ? expected : null, reported, atBottom);
    }

    public int FindBoundary(
        ReadOnlySpan<string> rows,
        int first,
        int? expected,
        int advance,
        bool atBottom,
        bool historyRedrawn,
        bool continuous,
        bool hasScrollTracker)
    {
        if (continuous && (!atBottom || historyRedrawn))
        {
            return Math.Max(0, rows.Length - advance);
        }

        if (_tail.Length == 0)
        {
            return 0;
        }

        if (expected is { } count && count <= rows.Length)
        {
            int boundary = rows.Length - count;
            int overlap = Math.Min(_tail.Length, boundary);
            if ((overlap == _tail.Length || first == 0) &&
                _tail.AsSpan(_tail.Length - overlap).SequenceEqual(rows.Slice(boundary - overlap, overlap)))
            {
                return boundary;
            }
        }

        int index = rows.LastIndexOf(_tail.AsSpan());
        if (index >= 0)
        {
            if (expected == 0 && index > 0 && rows[..(index + _tail.Length - 1)].IndexOf(_tail.AsSpan()) >= 0)
            {
                return rows.Length;
            }
            return index + _tail.Length;
        }

        if (first != 0)
        {
            return -1;
        }

        if (hasScrollTracker && expected is { } reported)
        {
            return Math.Max(0, rows.Length - reported);
        }

        for (int overlap = Math.Min(_tail.Length - 1, rows.Length); overlap > 0; overlap--)
        {
            if (_tail.AsSpan(_tail.Length - overlap).SequenceEqual(rows[..overlap]))
            {
                return overlap;
            }
        }

        return 0;
    }

    public string[] Commit(
        ConsoleBufferSnapshot snapshot,
        ReadOnlySpan<string> rows,
        int boundary,
        ConsoleReadPlan plan,
        bool hasScrollTracker)
    {
        int clampedBoundary = Math.Clamp(boundary, 0, rows.Length);
        var added = rows[clampedBoundary..].ToArray();
        _scrollAlreadyCaptured = plan.Continuous && plan.AtBottom && hasScrollTracker
            ? Math.Max(0L, _scrollAlreadyCaptured + added.Length - plan.Advance - plan.ReportedScroll)
            : 0;
        _previous = snapshot;
        int tailStart = Math.Max(0, rows.Length - TailRows);
        _tail = rows[tailStart..].ToArray();
        return added;
    }

    public string[] ProcessSnapshot(
        ConsoleBufferSnapshot snapshot,
        ReadOnlySpan<string> fullBufferRows,
        ConsoleEventBatch events = default,
        bool hasScrollTracker = false)
    {
        var plan = PrepareRead(snapshot, events, hasScrollTracker);
        if (plan.EndRow <= 0)
        {
            _previous = snapshot;
            return [];
        }

        var slice = fullBufferRows[plan.FirstRow..plan.EndRow];
        int boundary = FindBoundary(
            slice,
            plan.FirstRow,
            plan.ExpectedNewRows,
            plan.Advance,
            plan.AtBottom,
            events.HistoryRedrawn,
            plan.Continuous,
            hasScrollTracker);

        if (boundary < 0)
        {
            slice = fullBufferRows[..plan.EndRow];
            boundary = FindBoundary(
                slice,
                0,
                plan.ExpectedNewRows,
                plan.Advance,
                plan.AtBottom,
                events.HistoryRedrawn,
                plan.Continuous,
                hasScrollTracker);
        }

        return Commit(snapshot, slice, boundary, plan, hasScrollTracker);
    }

    public static long GetReportedScroll(
        int viewportAdvance,
        ConsoleEventBatch events,
        bool cursorReturnedToColumnStart,
        bool previousViewportExcludedCursor)
    {
        if (events.ScrollCount == 0) return 0;

        long scroll = events.Scroll;
        if (viewportAdvance == 0 && events.LeadingLayout)
        {
            if (events.ScrollCount > 1 || events.BottomRegionRows == 0 || !cursorReturnedToColumnStart)
            {
                scroll += events.FirstScrollDelta;
            }
            else
            {
                scroll = Math.Min(scroll, events.BottomRegionRows);
            }
        }

        long reported = Math.Max(0L, scroll - Math.Max(0, viewportAdvance));
        if ((events.LayoutChanged || previousViewportExcludedCursor) && events.BottomRegionRows > 0)
        {
            reported = Math.Min(reported, events.BottomRegionRows);
        }
        return reported;
    }
}

internal sealed unsafe partial class ConsoleScrollTracker : IDisposable
{
    internal const uint ConsoleUpdateRegion = 0x4002;
    internal const uint ConsoleUpdateScroll = 0x4004;
    internal const uint ConsoleLayout = 0x4005;

    [ThreadStatic]
    private static ConsoleScrollTracker? s_current;

    private readonly nint _consoleWindow;
    private readonly nint _hook;
    private int _bottomRow = -1;
    private long _scroll;
    private int _scrollCount;
    private int _firstScrollDelta;
    private bool _sawUpdate;
    private bool _layoutChanged;
    private bool _leadingLayout;
    private int _bottomRegionRows;
    private bool _historyRegionUpdated;
    private long _eventVersion;

    private ConsoleScrollTracker(nint window)
    {
        _consoleWindow = window;
        s_current = this;
        _hook = SetWinEventHook(ConsoleUpdateRegion, ConsoleLayout, 0, &WinEventCallback, 0, 0, 0);
        if (_hook == 0 && ReferenceEquals(s_current, this))
        {
            s_current = null;
        }
    }

    public static ConsoleScrollTracker? TryCreate()
    {
        nint window = GetConsoleWindow();
        if (window == 0) return null;
        var tracker = new ConsoleScrollTracker(window);
        return tracker._hook != 0 ? tracker : null;
    }

    public ConsoleEventBatch ReadEvents(int bottomRow)
    {
        _bottomRow = bottomRow;
        while (PeekMessageW(out var message, 0, 0, 0, 1))
        {
            DispatchMessageW(ref message);
        }

        return new ConsoleEventBatch(
            _scroll,
            _scrollCount,
            _firstScrollDelta,
            _layoutChanged,
            _leadingLayout,
            _bottomRegionRows,
            _historyRegionUpdated && _bottomRegionRows == 0 && _scrollCount == 0,
            _eventVersion);
    }

    public void ClearEvents()
    {
        _scroll = 0;
        _scrollCount = 0;
        _firstScrollDelta = 0;
        _sawUpdate = false;
        _layoutChanged = false;
        _leadingLayout = false;
        _bottomRegionRows = 0;
        _historyRegionUpdated = false;
    }

    public void Dispose()
    {
        if (_hook != 0)
        {
            UnhookWinEvent(_hook);
        }
        if (ReferenceEquals(s_current, this))
        {
            s_current = null;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void WinEventCallback(nint hook, uint eventType, nint window, int dx, int dy, uint thread, uint time)
    {
        s_current?.OnScroll(eventType, window, dx, dy);
    }

    private void OnScroll(uint eventType, nint window, int dx, int dy)
    {
        if (window != _consoleWindow) return;
        _eventVersion++;

        if (eventType == ConsoleLayout)
        {
            _layoutChanged = true;
            if (!_sawUpdate) _leadingLayout = true;
            return;
        }

        if (eventType == ConsoleUpdateScroll)
        {
            if (_scrollCount == 0) _firstScrollDelta = dy;
            if (_scrollCount < int.MaxValue) _scrollCount++;
            _scroll -= dy;
            _sawUpdate = true;
            return;
        }

        if (eventType != ConsoleUpdateRegion) return;
        _sawUpdate = true;
        if (_bottomRow < 0) return;

        short firstRow = (short)(dx >> 16);
        short lastRow = (short)(dy >> 16);
        if (firstRow > lastRow) (firstRow, lastRow) = (lastRow, firstRow);
        if (lastRow < _bottomRow)
        {
            _historyRegionUpdated = true;
            return;
        }
        if (firstRow > _bottomRow) return;
        int span = Math.Max(1, lastRow - firstRow + 1);
        _bottomRegionRows = (int)Math.Min(int.MaxValue, (long)_bottomRegionRows + span);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public nint Window;
        public uint Message;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public Point Point;
        public uint Private;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint SetWinEventHook(
        uint eventMin,
        uint eventMax,
        nint hmodWinEventProc,
        delegate* unmanaged[Stdcall]<nint, uint, nint, int, int, uint, uint, void> pfnWinEventProc,
        uint idProcess,
        uint idThread,
        uint dwFlags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnhookWinEvent(nint hWinEventHook);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetConsoleWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PeekMessageW(out NativeMessage lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [LibraryImport("user32.dll")]
    private static partial nint DispatchMessageW(ref NativeMessage lpMsg);
}
