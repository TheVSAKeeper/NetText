using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using NetText.Native;

namespace NetText.UI;

internal sealed unsafe class LogViewport : IDisposable
{
    internal const int TabWidth = 4;
    private const nuint DragScrollTimerId = 1;
    private const uint DragScrollIntervalMs = 50;

    private readonly record struct VisualLine(int RowIndex, int CharStart, int CharLen);
    internal readonly record struct TextPos(int Row, int Col);

    private static bool _classRegistered;

    private readonly nint _hwnd;
    private readonly nint _bgBrush;
    private readonly nint _selBrush;
    private readonly nint _searchBrush;
    private readonly GdiBackBuffer _backBuffer = new();
    private readonly Action<LogDestination> _onNavigate;
    private readonly Action<float>? _onZoom;
    private readonly GCHandle _selfHandle;
    private Action? _afterPaint;

    private nint _font;
    private int _dpi;
    private float _fontSizePt;
    private int _charWidthPx = 8;
    private int _lineHeightPx = 22;
    private bool _wrap;

    private string[] _rows = [];
    private string[] _displayRows = [];
    private Regex? _highlightRegex;
    private VisualLine[] _visualLines = [];
    private int _visualCount;
    private int _maxColsNoWrap;
    private int _topVisualLine;
    private int _leftChar;

    private bool _selecting;
    private TextPos? _anchor;
    private TextPos? _caret;
    private int _dragX;
    private int _dragY;
    private uint _lastClickTick;
    private int _lastClickX;
    private int _lastClickY;
    private int _clickCount;

    public nint Hwnd => _hwnd;

    public void SetAfterPaint(Action? afterPaint) => _afterPaint = afterPaint;

    public LogViewport(
        nint parent,
        nint hInstance,
        int dpi,
        float fontSizePt,
        bool wrap,
        Action<LogDestination> onNavigate,
        Action<float>? onZoom = null)
    {
        _dpi = Math.Max(96, dpi);
        _fontSizePt = Math.Clamp(fontSizePt, 9.0f, 36.0f);
        _wrap = wrap;
        _onNavigate = onNavigate;
        _onZoom = onZoom;
        _bgBrush = Win32.CreateSolidBrush(ThemePalette.Bg);
        _selBrush = Win32.CreateSolidBrush(ThemePalette.SelectionBg);
        _searchBrush = Win32.CreateSolidBrush(ThemePalette.SearchMatchBg);
        _selfHandle = GCHandle.Alloc(this);

        EnsureClassRegistered(hInstance);
        _hwnd = Win32.CreateWindowExW(
            0,
            "NetTextLogViewport",
            "",
            Win32.WS_CHILD | Win32.WS_CLIPCHILDREN | Win32.WS_TABSTOP | Win32.WS_VSCROLL | Win32.WS_HSCROLL,
            0, 0, 100, 100,
            parent,
            0,
            hInstance,
            (void*)GCHandle.ToIntPtr(_selfHandle));

        if (_hwnd == 0)
        {
            _selfHandle.Free();
            Win32.DeleteObject(_bgBrush);
            Win32.DeleteObject(_selBrush);
            Win32.DeleteObject(_searchBrush);
            throw new InvalidOperationException("Не удалось создать область просмотра лога");
        }

        RebuildFont();
    }

    public void SetWrap(bool wrap)
    {
        if (_wrap == wrap) return;
        _wrap = wrap;
        _leftChar = 0;
        RebuildVisualLayout();
        Win32.InvalidateRect(_hwnd, null, false);
    }

    public void SetDpi(int dpi)
    {
        int safe = Math.Max(96, dpi);
        if (_dpi == safe) return;
        _dpi = safe;
        RebuildFont();
        RebuildVisualLayout();
        Win32.InvalidateRect(_hwnd, null, false);
    }

    public void SetFontSize(float fontSizePt)
    {
        float clamped = Math.Clamp(fontSizePt, 9.0f, 36.0f);
        if (Math.Abs(_fontSizePt - clamped) < 0.01f) return;
        _fontSizePt = clamped;
        RebuildFont();
        RebuildVisualLayout();
        Win32.InvalidateRect(_hwnd, null, false);
    }

    public void SetSearchHighlight(Regex? regex)
    {
        if (ReferenceEquals(_highlightRegex, regex)) return;
        _highlightRegex = regex;
        Win32.InvalidateRect(_hwnd, null, false);
    }

    public void SetUtf8Text(ReadOnlySpan<byte> utf8, bool scrollToBottom = false)
    {
        string text = utf8.IsEmpty ? "" : Encoding.UTF8.GetString(utf8);
        _rows = text.Length == 0 ? [] : text.Split('\n');
        for (int i = 0; i < _rows.Length; i++)
        {
            if (_rows[i].EndsWith('\r'))
            {
                _rows[i] = _rows[i][..^1];
            }
        }

        _displayRows = new string[_rows.Length];
        for (int i = 0; i < _rows.Length; i++)
        {
            _displayRows[i] = ExpandTabs(_rows[i], TabWidth);
        }

        _topVisualLine = 0;
        _leftChar = 0;
        _anchor = null;
        _caret = null;
        RebuildVisualLayout();
        if (scrollToBottom)
        {
            Win32.RECT rc;
            Win32.GetClientRect(_hwnd, &rc);
            int visibleRows = Math.Max(1, (rc.Height - S(8)) / _lineHeightPx);
            _topVisualLine = Math.Max(0, _visualCount - visibleRows);
            UpdateScrollbars(rc);
        }
        Win32.InvalidateRect(_hwnd, null, false);
    }

    public void ScrollScreen(bool forward)
    {
        Win32.RECT rc;
        Win32.GetClientRect(_hwnd, &rc);
        int visibleRows = Math.Max(1, (rc.Height - S(8)) / _lineHeightPx);
        int step = Math.Max(1, visibleRows - 1);
        int maxTop = Math.Max(0, _visualCount - visibleRows);
        if (!forward)
        {
            if (_topVisualLine == 0)
            {
                _onNavigate(LogDestination.Previous);
                return;
            }
            _topVisualLine = Math.Max(0, _topVisualLine - step);
        }
        else
        {
            if (_topVisualLine >= maxTop)
            {
                _onNavigate(LogDestination.Next);
                return;
            }
            _topVisualLine = Math.Min(maxTop, _topVisualLine + step);
        }
        UpdateScrollbars(rc);
        Win32.InvalidateRect(_hwnd, null, false);
    }

    internal static string ExpandTabs(string line, int tabWidth = TabWidth)
    {
        int firstTab = line.IndexOf('\t');
        if (firstTab < 0)
        {
            return line;
        }

        var sb = new StringBuilder(line.Length + 16);
        int col = 0;
        int i = 0;
        while (i < line.Length)
        {
            char c = line[i++];
            if (c == '\t')
            {
                int spaces = tabWidth - (col % tabWidth);
                sb.Append(' ', spaces);
                col += spaces;
            }
            else
            {
                sb.Append(c);
                col++;
            }
        }
        return sb.ToString();
    }

    internal static int RawColToExpanded(ReadOnlySpan<char> raw, int rawCol, int tabWidth = TabWidth)
    {
        int limit = Math.Clamp(rawCol, 0, raw.Length);
        int col = 0;
        for (int i = 0; i < limit; i++)
        {
            col += raw[i] == '\t' ? (tabWidth - (col % tabWidth)) : 1;
        }
        return col;
    }

    internal static int ExpandedColToRaw(ReadOnlySpan<char> raw, int expandedCol, int tabWidth = TabWidth)
    {
        int target = Math.Max(0, expandedCol);
        int col = 0;
        for (int i = 0; i < raw.Length; i++)
        {
            if (col >= target)
            {
                return i;
            }

            if (raw[i] == '\t')
            {
                int adv = tabWidth - (col % tabWidth);
                if (target < col + adv)
                {
                    return (target - col) <= (adv / 2) ? i : i + 1;
                }
                col += adv;
            }
            else
            {
                col++;
            }
        }
        return raw.Length;
    }

    internal static (int Start, int End) WordBoundsAt(ReadOnlySpan<char> line, int col)
    {
        if (line.IsEmpty)
        {
            return (0, 0);
        }

        int idx = Math.Clamp(col, 0, line.Length - 1);
        if (idx > 0 && idx < line.Length && char.IsLowSurrogate(line[idx]) && char.IsHighSurrogate(line[idx - 1]))
        {
            idx--;
        }

        static int CharClass(char c)
        {
            if (char.IsLetterOrDigit(c) || c == '_') return 1;
            if (char.IsWhiteSpace(c)) return 2;
            return 3;
        }

        int cls = CharClass(line[idx]);
        int start = idx;
        while (start > 0 && CharClass(line[start - 1]) == cls)
        {
            start--;
        }

        int end = idx + 1;
        while (end < line.Length && CharClass(line[end]) == cls)
        {
            end++;
        }
        return (start, end);
    }

    private int S(int px) => Win32.MulDiv(px, _dpi, 96);

    private void RebuildFont()
    {
        if (_font != 0)
        {
            Win32.DeleteObject(_font);
            _font = 0;
        }

        int pt = Math.Max(8, (int)MathF.Round(_fontSizePt));
        int height = -Win32.MulDiv(pt, _dpi, 72);
        _font = Win32.CreateFontW(height, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0x31, "Consolas");
        nint hdc = Win32.GetDC(_hwnd);
        if (hdc != 0)
        {
            nint old = Win32.SelectObject(hdc, _font);
            Win32.TEXTMETRICW tm;
            if (Win32.GetTextMetricsW(hdc, &tm))
            {
                _charWidthPx = Math.Max(5, tm.tmAveCharWidth);
                _lineHeightPx = Math.Max(14, tm.tmHeight + S(4));
            }
            Win32.SelectObject(hdc, old);
            Win32.ReleaseDC(_hwnd, hdc);
        }
    }

    private void RebuildVisualLayout()
    {
        Win32.RECT rc;
        Win32.GetClientRect(_hwnd, &rc);
        int padX = S(10);
        int cols = Math.Max(8, (rc.Width - padX * 2) / Math.Max(1, _charWidthPx));

        _visualCount = 0;
        _maxColsNoWrap = 0;
        if (_visualLines.Length < _displayRows.Length * 2)
        {
            _visualLines = new VisualLine[Math.Max(64, _displayRows.Length * 2)];
        }

        for (int r = 0; r < _displayRows.Length; r++)
        {
            string line = _displayRows[r];
            _maxColsNoWrap = Math.Max(_maxColsNoWrap, line.Length);
            if (!_wrap || line.Length <= cols)
            {
                AppendVisual(new VisualLine(r, 0, line.Length));
                continue;
            }

            int pos = 0;
            while (pos < line.Length)
            {
                int take = Math.Min(cols, line.Length - pos);
                if (pos + take < line.Length && char.IsHighSurrogate(line[pos + take - 1]))
                {
                    take = Math.Max(1, take - 1);
                }
                AppendVisual(new VisualLine(r, pos, take));
                pos += take;
            }
        }

        UpdateScrollbars(rc);
    }

    private void AppendVisual(VisualLine vl)
    {
        if (_visualCount == _visualLines.Length)
        {
            Array.Resize(ref _visualLines, _visualLines.Length * 2);
        }
        _visualLines[_visualCount++] = vl;
    }

    private void UpdateScrollbars(Win32.RECT rc)
    {
        int visibleRows = Math.Max(1, (rc.Height - S(8)) / _lineHeightPx);
        _topVisualLine = Math.Clamp(_topVisualLine, 0, Math.Max(0, _visualCount - visibleRows));

        var vsi = new Win32.SCROLLINFO
        {
            cbSize = (uint)Marshal.SizeOf<Win32.SCROLLINFO>(),
            fMask = Win32.SIF_ALL,
            nMin = 0,
            nMax = Math.Max(0, _visualCount - 1),
            nPage = (uint)visibleRows,
            nPos = _topVisualLine,
        };
        Win32.SetScrollInfo(_hwnd, Win32.SB_VERT, &vsi, true);

        int padX = S(10);
        int visibleCols = Math.Max(1, (rc.Width - padX * 2) / _charWidthPx);
        int hMax = _wrap ? 0 : Math.Max(0, _maxColsNoWrap - 1);
        _leftChar = _wrap ? 0 : Math.Clamp(_leftChar, 0, Math.Max(0, _maxColsNoWrap - visibleCols));

        var hsi = new Win32.SCROLLINFO
        {
            cbSize = (uint)Marshal.SizeOf<Win32.SCROLLINFO>(),
            fMask = Win32.SIF_ALL,
            nMin = 0,
            nMax = hMax,
            nPage = (uint)visibleCols,
            nPos = _leftChar,
        };
        Win32.SetScrollInfo(_hwnd, Win32.SB_HORZ, &hsi, true);
    }

    private static int MeasureSpanWidth(nint hdc, string text, int start, int length)
    {
        if (length <= 0 || start >= text.Length) return 0;
        int safeLen = Math.Min(length, text.Length - start);
        if (safeLen <= 0) return 0;
        Win32.SIZE sz;
        fixed (char* pText = text)
        {
            if (Win32.GetTextExtentPoint32W(hdc, pText + start, safeLen, &sz))
            {
                return sz.cx;
            }
        }
        return safeLen * 8;
    }

    private void Paint(nint hdc, Win32.RECT rc)
    {
        if (rc.Width <= 0 || rc.Height <= 0) return;

        nint memDc = _backBuffer.Ensure(hdc, rc.Width, rc.Height);
        if (memDc == 0) return;

        Win32.FillRect(memDc, &rc, _bgBrush);
        Win32.SetBkMode(memDc, Win32.TRANSPARENT);
        Win32.SetTextColor(memDc, ThemePalette.Text);
        nint oldFont = Win32.SelectObject(memDc, _font);

        try
        {
            int padX = S(10);
            int padY = S(4);
            int maxVis = (rc.Height - padY) / _lineHeightPx + 1;
            var (selStart, selEnd) = OrderedSelection();
            var rx = _highlightRegex;

            for (int i = 0; i < maxVis; i++)
            {
                int vi = _topVisualLine + i;
                if ((uint)vi >= (uint)_visualCount) break;

                var vl = _visualLines[vi];
                string rawRow = _rows[vl.RowIndex];
                string dispRow = _displayRows[vl.RowIndex];
                int y = padY + i * _lineHeightPx;

                int skipCols = _wrap ? 0 : Math.Min(_leftChar, vl.CharLen);
                int drawStart = vl.CharStart + skipCols;
                int drawLen = vl.CharLen - skipCols;
                int visEnd = vl.CharStart + vl.CharLen;

                if (rx is not null && rawRow.Length > 0 && drawLen > 0)
                {
                    foreach (ValueMatch m in rx.EnumerateMatches(rawRow))
                    {
                        if (m.Length <= 0) continue;
                        int exp0 = RawColToExpanded(rawRow, m.Index);
                        int exp1 = RawColToExpanded(rawRow, m.Index + m.Length);
                        int hi0 = Math.Max(drawStart, exp0);
                        int hi1 = Math.Min(visEnd, exp1);
                        if (hi1 > hi0)
                        {
                            int x0 = padX + MeasureSpanWidth(memDc, dispRow, drawStart, hi0 - drawStart);
                            int x1 = padX + MeasureSpanWidth(memDc, dispRow, drawStart, hi1 - drawStart);
                            var mRc = new Win32.RECT { Left = x0, Top = y, Right = x1, Bottom = y + _lineHeightPx };
                            Win32.FillRect(memDc, &mRc, _searchBrush);
                        }
                    }
                }

                if (selStart is { } ss && selEnd is { } se && ss != se &&
                    vl.RowIndex >= ss.Row && vl.RowIndex <= se.Row)
                {
                    int c0 = vl.RowIndex == ss.Row ? RawColToExpanded(rawRow, ss.Col) : 0;
                    int c1 = vl.RowIndex == se.Row ? RawColToExpanded(rawRow, se.Col) : dispRow.Length;
                    int hi0 = Math.Max(drawStart, c0);
                    int hi1 = Math.Min(visEnd, c1);
                    if (hi1 > hi0)
                    {
                        int x0 = padX + MeasureSpanWidth(memDc, dispRow, drawStart, hi0 - drawStart);
                        int x1 = padX + MeasureSpanWidth(memDc, dispRow, drawStart, hi1 - drawStart);
                        var selRc = new Win32.RECT { Left = x0, Top = y, Right = x1, Bottom = y + _lineHeightPx };
                        Win32.FillRect(memDc, &selRc, _selBrush);
                    }
                }

                if (drawLen > 0)
                {
                    var lineRect = new Win32.RECT { Left = padX, Top = y, Right = rc.Width - padX, Bottom = y + _lineHeightPx };
                    fixed (char* pText = dispRow)
                    {
                        Win32.ExtTextOutW(memDc, padX, y + S(2), Win32.ETO_CLIPPED, &lineRect, pText + drawStart, (uint)drawLen, null);
                    }
                }
            }

            Win32.BitBlt(hdc, 0, 0, rc.Width, rc.Height, memDc, 0, 0, Win32.SRCCOPY);
            Measurements.RecordFirstFramePresented();
            if (_rows.Length > 0)
            {
                Measurements.RecordFirstLogFrame();
                _afterPaint?.Invoke();
            }
        }
        finally
        {
            Win32.SelectObject(memDc, oldFont);
        }
    }

    private (TextPos? Start, TextPos? End) OrderedSelection()
    {
        if (_anchor is not { } a || _caret is not { } b) return (null, null);
        bool aFirst = a.Row < b.Row || (a.Row == b.Row && a.Col <= b.Col);
        return aFirst ? (a, b) : (b, a);
    }

    private TextPos HitTest(int x, int y)
    {
        if (_visualCount == 0) return new TextPos(0, 0);
        int visRow = Math.Clamp(_topVisualLine + Math.Max(0, y - S(4)) / _lineHeightPx, 0, _visualCount - 1);
        var vl = _visualLines[visRow];
        string rawRow = _rows[vl.RowIndex];
        string dispRow = _displayRows[vl.RowIndex];

        int drawStart = vl.CharStart + (_wrap ? 0 : Math.Min(_leftChar, vl.CharLen));
        int drawLen = vl.CharLen - (_wrap ? 0 : Math.Min(_leftChar, vl.CharLen));
        int targetPx = x - S(10);

        int expCol;
        if (targetPx <= 0 || drawLen <= 0)
        {
            expCol = drawStart;
        }
        else
        {
            nint hdc = Win32.GetDC(_hwnd);
            if (hdc != 0)
            {
                nint old = Win32.SelectObject(hdc, _font);
                try
                {
                    int lo = 0;
                    int hi = drawLen;
                    while (lo < hi)
                    {
                        int mid = lo + ((hi - lo) / 2);
                        int w = MeasureSpanWidth(hdc, dispRow, drawStart, mid);
                        if (w < targetPx) lo = mid + 1;
                        else hi = mid;
                    }

                    int rightIdx = lo;
                    int leftIdx = Math.Max(0, rightIdx - 1);
                    int wRight = MeasureSpanWidth(hdc, dispRow, drawStart, rightIdx);
                    int wLeft = MeasureSpanWidth(hdc, dispRow, drawStart, leftIdx);
                    int chosen = (rightIdx > 0 && targetPx - wLeft < wRight - targetPx) ? leftIdx : rightIdx;
                    expCol = drawStart + chosen;
                }
                finally
                {
                    Win32.SelectObject(hdc, old);
                    Win32.ReleaseDC(_hwnd, hdc);
                }
            }
            else
            {
                expCol = Math.Clamp(drawStart + (targetPx + _charWidthPx / 2) / _charWidthPx, drawStart, drawStart + drawLen);
            }
        }

        if (expCol > 0 && expCol < dispRow.Length && char.IsLowSurrogate(dispRow[expCol]) && char.IsHighSurrogate(dispRow[expCol - 1]))
        {
            expCol--;
        }

        int rawCol = ExpandedColToRaw(rawRow, expCol, TabWidth);
        return new TextPos(vl.RowIndex, rawCol);
    }

    private void SelectWordAt(TextPos pos)
    {
        if ((uint)pos.Row >= (uint)_rows.Length) return;
        string line = _rows[pos.Row];
        var (start, end) = WordBoundsAt(line, pos.Col);
        _anchor = new TextPos(pos.Row, start);
        _caret = new TextPos(pos.Row, end);
    }

    private void SelectLineAt(int row)
    {
        if ((uint)row >= (uint)_rows.Length) return;
        _anchor = new TextPos(row, 0);
        _caret = new TextPos(row, _rows[row].Length);
    }

    private void StopSelecting()
    {
        if (!_selecting) return;
        _selecting = false;
        Win32.KillTimer(_hwnd, DragScrollTimerId);
        Win32.ReleaseCapture();
    }

    private void CopySelection()
    {
        var (ss, se) = OrderedSelection();
        if (ss is not { } start || se is not { } end || start == end || _rows.Length == 0)
        {
            return;
        }

        var sb = new StringBuilder();
        for (int r = start.Row; r <= end.Row && r < _rows.Length; r++)
        {
            string line = _rows[r];
            int c0 = r == start.Row ? Math.Clamp(start.Col, 0, line.Length) : 0;
            int c1 = r == end.Row ? Math.Clamp(end.Col, 0, line.Length) : line.Length;
            if (r > start.Row) sb.Append("\r\n");
            if (c1 > c0) sb.Append(line, c0, c1 - c0);
        }

        string text = sb.ToString();
        if (text.Length == 0 || !Win32.OpenClipboard(_hwnd)) return;
        try
        {
            Win32.EmptyClipboard();
            nuint bytes = (nuint)((text.Length + 1) * sizeof(char));
            nint hMem = Win32.GlobalAlloc(Win32.GMEM_MOVEABLE, bytes);
            if (hMem == 0) return;
            nint ptr = Win32.GlobalLock(hMem);
            if (ptr == 0) { Win32.GlobalFree(hMem); return; }
            try
            {
                text.AsSpan().CopyTo(new Span<char>((void*)ptr, text.Length));
                ((char*)ptr)[text.Length] = '\0';
            }
            finally
            {
                Win32.GlobalUnlock(hMem);
            }
            if (Win32.SetClipboardData(Win32.CF_UNICODETEXT, hMem) == 0)
            {
                Win32.GlobalFree(hMem);
            }
        }
        finally
        {
            Win32.CloseClipboard();
        }
    }

    private nint HandleMessage(uint msg, nuint wParam, nint lParam)
    {
        switch (msg)
        {
            case Win32.WM_PAINT:
            {
                Win32.PAINTSTRUCT ps;
                nint hdc = Win32.BeginPaint(_hwnd, &ps);
                Win32.RECT rc;
                Win32.GetClientRect(_hwnd, &rc);
                Paint(hdc, rc);
                Win32.EndPaint(_hwnd, &ps);
                return 0;
            }

            case Win32.WM_ERASEBKGND:
                return 1;

            case Win32.WM_SIZE:
                RebuildVisualLayout();
                Win32.InvalidateRect(_hwnd, null, false);
                return 0;

            case Win32.WM_MOUSEWHEEL:
            {
                int delta = Win32.GetWheelDelta(wParam);
                if (Win32.IsKeyDown(Win32.VK_CONTROL))
                {
                    _onZoom?.Invoke(delta > 0 ? 1.0f : -1.0f);
                    return 0;
                }

                int step = delta > 0 ? -3 : 3;
                Win32.RECT rc;
                Win32.GetClientRect(_hwnd, &rc);
                int visibleRows = Math.Max(1, (rc.Height - S(8)) / _lineHeightPx);
                int maxTop = Math.Max(0, _visualCount - visibleRows);
                int next = _topVisualLine + step;
                if (next < 0)
                {
                    if (_topVisualLine == 0) _onNavigate(LogDestination.Previous);
                    else _topVisualLine = 0;
                }
                else if (next > maxTop)
                {
                    if (_topVisualLine >= maxTop) _onNavigate(LogDestination.Next);
                    else _topVisualLine = maxTop;
                }
                else
                {
                    _topVisualLine = next;
                }
                UpdateScrollbars(rc);
                Win32.InvalidateRect(_hwnd, null, false);
                return 0;
            }

            case Win32.WM_VSCROLL:
            {
                int code = (int)(wParam & 0xFFFF);
                Win32.RECT rc;
                Win32.GetClientRect(_hwnd, &rc);
                int visibleRows = Math.Max(1, (rc.Height - S(8)) / _lineHeightPx);
                int maxTop = Math.Max(0, _visualCount - visibleRows);
                int next = _topVisualLine;
                switch (code)
                {
                    case Win32.SB_LINEUP: next--; break;
                    case Win32.SB_LINEDOWN: next++; break;
                    case Win32.SB_PAGEUP: next -= visibleRows; break;
                    case Win32.SB_PAGEDOWN: next += visibleRows; break;
                    case Win32.SB_TOP: next = 0; break;
                    case Win32.SB_BOTTOM: next = maxTop; break;
                    case Win32.SB_THUMBTRACK:
                    case Win32.SB_THUMBPOSITION:
                    {
                        var si = new Win32.SCROLLINFO { cbSize = (uint)Marshal.SizeOf<Win32.SCROLLINFO>(), fMask = Win32.SIF_TRACKPOS };
                        if (Win32.GetScrollInfo(_hwnd, Win32.SB_VERT, &si)) next = si.nTrackPos;
                        break;
                    }
                }
                _topVisualLine = Math.Clamp(next, 0, maxTop);
                UpdateScrollbars(rc);
                Win32.InvalidateRect(_hwnd, null, false);
                return 0;
            }

            case Win32.WM_HSCROLL:
            {
                int code = (int)(wParam & 0xFFFF);
                Win32.RECT rc;
                Win32.GetClientRect(_hwnd, &rc);
                int visibleCols = Math.Max(1, (rc.Width - S(20)) / _charWidthPx);
                int maxLeft = Math.Max(0, _maxColsNoWrap - visibleCols);
                int next = _leftChar;
                switch (code)
                {
                    case Win32.SB_LINEUP: next -= 4; break;
                    case Win32.SB_LINEDOWN: next += 4; break;
                    case Win32.SB_PAGEUP: next -= visibleCols; break;
                    case Win32.SB_PAGEDOWN: next += visibleCols; break;
                    case Win32.SB_THUMBTRACK:
                    case Win32.SB_THUMBPOSITION:
                    {
                        var si = new Win32.SCROLLINFO { cbSize = (uint)Marshal.SizeOf<Win32.SCROLLINFO>(), fMask = Win32.SIF_TRACKPOS };
                        if (Win32.GetScrollInfo(_hwnd, Win32.SB_HORZ, &si)) next = si.nTrackPos;
                        break;
                    }
                }
                _leftChar = _wrap ? 0 : Math.Clamp(next, 0, maxLeft);
                UpdateScrollbars(rc);
                Win32.InvalidateRect(_hwnd, null, false);
                return 0;
            }

            case Win32.WM_LBUTTONDOWN:
            {
                Win32.SetFocus(_hwnd);
                int x = Win32.GetXParam(lParam);
                int y = Win32.GetYParam(lParam);
                uint now = unchecked((uint)Environment.TickCount64);
                bool streak = unchecked(now - _lastClickTick) <= 550 &&
                              Math.Abs(x - _lastClickX) <= 4 &&
                              Math.Abs(y - _lastClickY) <= 4;
                _clickCount = streak ? ((_clickCount % 3) + 1) : 1;
                _lastClickTick = now;
                _lastClickX = x;
                _lastClickY = y;

                var pos = HitTest(x, y);
                if (_clickCount == 3)
                {
                    SelectLineAt(pos.Row);
                }
                else
                {
                    _selecting = true;
                    _dragX = x;
                    _dragY = y;
                    Win32.SetCapture(_hwnd);
                    Win32.SetTimer(_hwnd, DragScrollTimerId, DragScrollIntervalMs, 0);
                    _anchor = pos;
                    _caret = pos;
                }
                Win32.InvalidateRect(_hwnd, null, false);
                return 0;
            }

            case Win32.WM_LBUTTONDBLCLK:
            {
                Win32.SetFocus(_hwnd);
                int x = Win32.GetXParam(lParam);
                int y = Win32.GetYParam(lParam);
                _clickCount = 2;
                _lastClickTick = unchecked((uint)Environment.TickCount64);
                _lastClickX = x;
                _lastClickY = y;
                var pos = HitTest(x, y);
                SelectWordAt(pos);
                Win32.InvalidateRect(_hwnd, null, false);
                return 0;
            }

            case Win32.WM_MOUSEMOVE:
                if (_selecting)
                {
                    _dragX = Win32.GetXParam(lParam);
                    _dragY = Win32.GetYParam(lParam);
                    _caret = HitTest(_dragX, _dragY);
                    Win32.InvalidateRect(_hwnd, null, false);
                }
                return 0;

            case Win32.WM_TIMER:
                if (wParam == DragScrollTimerId && _selecting)
                {
                    Win32.RECT rc;
                    Win32.GetClientRect(_hwnd, &rc);
                    int visibleRows = Math.Max(1, (rc.Height - S(8)) / _lineHeightPx);
                    int maxTop = Math.Max(0, _visualCount - visibleRows);
                    bool scrolled = false;

                    if (_dragY < S(4) && _topVisualLine > 0)
                    {
                        _topVisualLine = Math.Max(0, _topVisualLine - 2);
                        scrolled = true;
                    }
                    else if (_dragY >= rc.Height && _topVisualLine < maxTop)
                    {
                        _topVisualLine = Math.Min(maxTop, _topVisualLine + 2);
                        scrolled = true;
                    }

                    if (!_wrap)
                    {
                        int visibleCols = Math.Max(1, (rc.Width - S(20)) / _charWidthPx);
                        int maxLeft = Math.Max(0, _maxColsNoWrap - visibleCols);
                        if (_dragX < S(10) && _leftChar > 0)
                        {
                            _leftChar = Math.Max(0, _leftChar - 3);
                            scrolled = true;
                        }
                        else if (_dragX > rc.Width - S(10) && _leftChar < maxLeft)
                        {
                            _leftChar = Math.Min(maxLeft, _leftChar + 3);
                            scrolled = true;
                        }
                    }

                    if (scrolled)
                    {
                        _caret = HitTest(_dragX, _dragY);
                        UpdateScrollbars(rc);
                        Win32.InvalidateRect(_hwnd, null, false);
                    }
                }
                return 0;

            case Win32.WM_LBUTTONUP:
            case Win32.WM_CAPTURECHANGED:
                StopSelecting();
                return 0;

            case Win32.WM_KEYDOWN:
            {
                int vk = (int)wParam;
                bool ctrl = Win32.IsKeyDown(Win32.VK_CONTROL);
                if (ctrl && vk == 'A' && _rows.Length > 0)
                {
                    _anchor = new TextPos(0, 0);
                    _caret = new TextPos(_rows.Length - 1, _rows[^1].Length);
                    Win32.InvalidateRect(_hwnd, null, false);
                    return 0;
                }
                if (ctrl && (vk == 'C' || vk == Win32.VK_INSERT))
                {
                    CopySelection();
                    return 0;
                }
                if (vk == 0x21)
                {
                    ScrollScreen(false);
                    return 0;
                }
                if (vk == 0x22)
                {
                    ScrollScreen(true);
                    return 0;
                }
                if (ctrl && vk == 0x24)
                {
                    _onNavigate(LogDestination.Start);
                    return 0;
                }
                if (ctrl && vk == 0x23)
                {
                    _onNavigate(LogDestination.End);
                    return 0;
                }
                break;
            }

            case Win32.WM_NCDESTROY:
                StopSelecting();
                DisposeResources();
                Win32.SetWindowLongPtrW(_hwnd, Win32.GWLP_USERDATA, 0);
                break;
        }

        return Win32.DefWindowProcW(_hwnd, msg, wParam, lParam);
    }

    private static void EnsureClassRegistered(nint hInstance)
    {
        if (_classRegistered) return;
        fixed (char* cls = "NetTextLogViewport")
        {
            var wc = new Win32.WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<Win32.WNDCLASSEXW>(),
                style = Win32.CS_DBLCLKS,
                lpfnWndProc = &WndProcNative,
                hInstance = hInstance,
                hCursor = Win32.LoadCursorW(0, (nint)Win32.IDC_IBEAM),
                lpszClassName = cls,
            };
            Win32.RegisterClassExW(&wc);
        }
        _classRegistered = true;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint WndProcNative(nint hwnd, uint msg, nuint wParam, nint lParam)
    {
        if (msg == Win32.WM_NCCREATE)
        {
            var cs = (CREATESTRUCTW*)lParam;
            if (cs != null && cs->lpCreateParams != null)
            {
                Win32.SetWindowLongPtrW(hwnd, Win32.GWLP_USERDATA, (nint)cs->lpCreateParams);
            }
            return Win32.DefWindowProcW(hwnd, msg, wParam, lParam);
        }

        nint userData = Win32.GetWindowLongPtrW(hwnd, Win32.GWLP_USERDATA);
        if (userData != 0 && GCHandle.FromIntPtr(userData).Target is LogViewport view)
        {
            return view.HandleMessage(msg, wParam, lParam);
        }

        return Win32.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CREATESTRUCTW
    {
        public void* lpCreateParams;
        public nint hInstance;
        public nint hMenu;
        public nint hwndParent;
        public int cy;
        public int cx;
        public int y;
        public int x;
        public int style;
        public char* lpszName;
        public char* lpszClass;
        public uint dwExStyle;
    }

    private void DisposeResources()
    {
        _backBuffer.Dispose();
        if (_font != 0) { Win32.DeleteObject(_font); _font = 0; }
        if (_bgBrush != 0) { Win32.DeleteObject(_bgBrush); }
        if (_selBrush != 0) { Win32.DeleteObject(_selBrush); }
        if (_searchBrush != 0) { Win32.DeleteObject(_searchBrush); }
        if (_selfHandle.IsAllocated) { _selfHandle.Free(); }
    }

    public void Dispose()
    {
        if (_hwnd != 0)
        {
            Win32.DestroyWindow(_hwnd);
        }
    }
}
