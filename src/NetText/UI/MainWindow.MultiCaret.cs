using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using NetText.Core;
using NetText.Native;

namespace NetText.UI;

internal sealed unsafe partial class MainWindow
{
    private const int MaxMultiCarets = 10_000;
    private const nuint EditorSubclassId = 1;
    private const uint MK_CONTROL = 0x0008;
    private const uint WM_KILLFOCUS = 0x0008;
    private const int VK_MENU = 0x12;
    private const int VK_UP = 0x26;
    private const int VK_DOWN = 0x28;
    private const uint SCI_GETREADONLY = 2140;
    private const uint SCI_INDICATORCLEARRANGE = 2505;
    private const uint SCI_INDICATORFILLRANGE = 2504;
    private const uint SCI_SETINDICATORCURRENT = 2500;
    private const uint SCI_INDICSETSTYLE = 2080;
    private const uint SCI_INDICSETFORE = 2082;
    private const int INDIC_PLAIN = 0;

    private readonly Dictionary<ulong, (ulong Revision, int FirstLine, int LastLine, int LineCount)> _lineNumberCache = [];
    private readonly Dictionary<ulong, (ulong Revision, int FirstLine, int LastLine, int VisitedCount)> _linkIndicatorCache = [];
    private (ulong Id, ulong Revision, int Column)? _preferredVerticalColumn;
    private (nint Hwnd, int X, int Y)? _altClickStart;

    [GeneratedRegex(@"\b(?:https?://[^\s<>""'`\)\]]+|mailto:[^\s<>""'`\)\]]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlPattern();

    [LibraryImport("user32.dll")]
    private static partial nint GetClipboardData(uint uFormat);

    private void InstallEditorSubclass(nint editorHwnd)
    {
        if (editorHwnd == 0) return;
        Win32.SetWindowSubclass(editorHwnd, &EditorSubclassProc, EditorSubclassId, 0);
    }

    private void RemoveEditorSubclass(nint editorHwnd)
    {
        if (editorHwnd == 0) return;
        Win32.RemoveWindowSubclass(editorHwnd, &EditorSubclassProc, EditorSubclassId);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint EditorSubclassProc(nint hwnd, uint msg, nuint wParam, nint lParam, nuint uIdSubclass, nuint dwRefData)
    {
        var app = WorkspaceSession.Current.FindWindow(hwnd) ?? _creatingWindow;
        if (app is not null)
        {
            switch (msg)
            {
                case Win32.WM_MOUSEWHEEL:
                case Win32.WM_LBUTTONDOWN:
                case Win32.WM_LBUTTONUP:
                    if (app.TryHandleEditorMouse(hwnd, msg, wParam, lParam)) return 0;
                    break;

                case Win32.WM_KEYDOWN:
                    if (app.HasDocument && app.ActiveDocument.EditorHwnd == hwnd &&
                        app.HandleMultiCaretKey((int)wParam,
                            Win32.IsKeyDown(Win32.VK_CONTROL),
                            Win32.IsKeyDown(Win32.VK_SHIFT),
                            Win32.IsKeyDown(VK_MENU)))
                    {
                        return 0;
                    }
                    break;

                case Win32.WM_SETFOCUS:
                    ScintillaInterop.ApplyFocusSelection(hwnd, focused: true);
                    break;

                case WM_KILLFOCUS:
                    ScintillaInterop.ApplyFocusSelection(hwnd, focused: false);
                    break;

                case Win32.WM_NCDESTROY:
                    app.RemoveEditorSubclass(hwnd);
                    break;
            }
        }
        return Win32.DefSubclassProc(hwnd, msg, wParam, lParam);
    }

    private static void CollapseToPrimaryCaret(nint editorHwnd)
    {
        if (editorHwnd == 0) return;
        int count = (int)ScintillaInterop.Send(editorHwnd, ScintillaInterop.SCI_GETSELECTIONS, 0, 0);
        if (count <= 1) return;
        int main = Math.Clamp((int)ScintillaInterop.Send(editorHwnd, ScintillaInterop.SCI_GETMAINSELECTION, 0, 0), 0, count - 1);
        int anchor = (int)ScintillaInterop.Send(editorHwnd, ScintillaInterop.SCI_GETSELECTIONNANCHOR, (nuint)main, 0);
        int caret = (int)ScintillaInterop.Send(editorHwnd, ScintillaInterop.SCI_GETSELECTIONNCARET, (nuint)main, 0);
        ScintillaInterop.Send(editorHwnd, ScintillaInterop.SCI_SETSEL, (nuint)anchor, caret);
        ScintillaInterop.Send(editorHwnd, ScintillaInterop.SCI_SCROLLCARET, 0, 0);
    }

    private bool HandleMultiCaretKey(int vk, bool ctrl, bool shift, bool alt)
    {
        if (!HasDocument || _showHome || ActiveDocument.Log is not null || ActiveDocument.Placeholder)
            return false;

        var doc = ActiveDocument;
        nint hwnd = doc.EditorHwnd;
        if (hwnd == 0) return false;

        if (vk is not (VK_UP or VK_DOWN) || !ctrl || !alt)
        {
            _preferredVerticalColumn = null;
        }

        switch (vk)
        {
            case 'D' when ctrl && !shift && !alt:
                return SelectNextOccurrence(doc);

            case 'L' when ctrl && shift && !alt:
                return SelectAllOccurrences(doc);

            case VK_UP when ctrl && alt && !shift:
                return AddVerticalCaret(doc, -1);

            case VK_DOWN when ctrl && alt && !shift:
                return AddVerticalCaret(doc, 1);

            case Win32.VK_ESCAPE when !ctrl && !shift && !alt:
            {
                int count = (int)ScintillaInterop.Send(hwnd, ScintillaInterop.SCI_GETSELECTIONS, 0, 0);
                if (count > 1)
                {
                    CollapseToPrimaryCaret(hwnd);
                    UpdateActiveCursor();
                    UpdateEditorLinksAndLineNumbers(doc);
                    return true;
                }
                return false;
            }

            case 'X' when ctrl && !shift && !alt:
            {
                int count = (int)ScintillaInterop.Send(hwnd, ScintillaInterop.SCI_GETSELECTIONS, 0, 0);
                var (start, end, _) = ScintillaInterop.GetSelection(hwnd);
                bool readOnly = ScintillaInterop.Send(hwnd, SCI_GETREADONLY, 0, 0) != 0;
                if (count <= 1 && end <= start && !readOnly)
                {
                    ScintillaInterop.Send(hwnd, ScintillaInterop.SCI_LINECUT, 0, 0);
                    UpdateActiveCursor();
                    UpdateEditorLinksAndLineNumbers(doc);
                    return true;
                }
                return false;
            }

            case 'V' when ctrl && !shift && !alt:
                return TryMultiCaretLinePaste(doc);

            default:
                return false;
        }
    }

    private bool SelectNextOccurrence(Document doc)
    {
        nint hwnd = doc.EditorHwnd;
        int count = Math.Max(1, (int)ScintillaInterop.Send(hwnd, ScintillaInterop.SCI_GETSELECTIONS, 0, 0));
        int main = Math.Clamp((int)ScintillaInterop.Send(hwnd, ScintillaInterop.SCI_GETMAINSELECTION, 0, 0), 0, count - 1);
        int mainStart = (int)ScintillaInterop.Send(hwnd, ScintillaInterop.SCI_GETSELECTIONNSTART, (nuint)main, 0);
        int mainEnd = (int)ScintillaInterop.Send(hwnd, ScintillaInterop.SCI_GETSELECTIONNEND, (nuint)main, 0);

        if (mainEnd <= mainStart)
        {
            if (!TryGetWordRangeAt(hwnd, mainStart, out int wordStart, out int wordEnd)) return true;
            if (count == 1)
            {
                ScintillaInterop.Send(hwnd, ScintillaInterop.SCI_SETSEL, (nuint)wordStart, wordEnd);
            }
            else
            {
                ScintillaInterop.Send(hwnd, ScintillaInterop.SCI_SETSELECTIONNANCHOR, (nuint)main, wordStart);
                ScintillaInterop.Send(hwnd, ScintillaInterop.SCI_SETSELECTIONNCARET, (nuint)main, wordEnd);
            }
            ScintillaInterop.Send(hwnd, ScintillaInterop.SCI_SCROLLCARET, 0, 0);
            UpdateActiveCursor();
            UpdateEditorLinksAndLineNumbers(doc);
            return true;
        }

        if (count >= MaxMultiCarets)
        {
            _error = "Слишком много курсоров. Допустимо до 10.000.";
            InvalidateChrome();
            return true;
        }

        int totalLen = ScintillaInterop.GetByteLength(hwnd);
        if (totalLen <= 0) return true;

        var full = ScintillaInterop.ByteSpan(hwnd, 0, totalLen);
        var needle = full.Slice(mainStart, Math.Min(mainEnd - mainStart, totalLen - mainStart));
        if (needle.IsEmpty) return true;

        var existing = new (int Start, int End)[count];
        for (int i = 0; i < count; i++)
        {
            existing[i] = (
                (int)ScintillaInterop.Send(hwnd, ScintillaInterop.SCI_GETSELECTIONNSTART, (nuint)i, 0),
                (int)ScintillaInterop.Send(hwnd, ScintillaInterop.SCI_GETSELECTIONNEND, (nuint)i, 0));
        }
        Array.Sort(existing, (a, b) => a.Start.CompareTo(b.Start));

        int searchFrom = existing[^1].End;
        int next = FindNonOverlapping(full, needle, searchFrom, full.Length, existing);
        if (next < 0) next = FindNonOverlapping(full, needle, 0, existing[0].Start, existing);

        if (next >= 0)
        {
            ScintillaInterop.Send(hwnd, ScintillaInterop.SCI_ADDSELECTION, (nuint)(next + needle.Length), next);
            ScintillaInterop.Send(hwnd, ScintillaInterop.SCI_SCROLLCARET, 0, 0);
            UpdateActiveCursor();
            UpdateEditorLinksAndLineNumbers(doc);
        }
        return true;

        static int FindNonOverlapping(ReadOnlySpan<byte> text, ReadOnlySpan<byte> needle, int from, int limit, (int Start, int End)[] occupied)
        {
            int pos = Math.Max(0, from);
            int endLimit = Math.Min(text.Length, limit);
            while (pos <= endLimit - needle.Length)
            {
                int rel = text.Slice(pos, endLimit - pos).IndexOf(needle);
                if (rel < 0) return -1;
                int start = pos + rel;
                int end = start + needle.Length;
                if (!Overlaps(occupied, start, end)) return start;
                pos = start + 1;
            }
            return -1;
        }

        static bool Overlaps((int Start, int End)[] ranges, int start, int end)
        {
            foreach (var r in ranges)
            {
                if (r.End > start && r.Start < end) return true;
            }
            return false;
        }
    }

    private bool SelectAllOccurrences(Document doc)
    {
        nint hwnd = doc.EditorHwnd;
        int count = Math.Max(1, (int)ScintillaInterop.Send(hwnd, ScintillaInterop.SCI_GETSELECTIONS, 0, 0));
        int main = Math.Clamp((int)ScintillaInterop.Send(hwnd, ScintillaInterop.SCI_GETMAINSELECTION, 0, 0), 0, count - 1);
        int mainStart = (int)ScintillaInterop.Send(hwnd, ScintillaInterop.SCI_GETSELECTIONNSTART, (nuint)main, 0);
        int mainEnd = (int)ScintillaInterop.Send(hwnd, ScintillaInterop.SCI_GETSELECTIONNEND, (nuint)main, 0);

        if (mainEnd <= mainStart)
        {
            if (!TryGetWordRangeAt(hwnd, mainStart, out mainStart, out mainEnd)) return true;
        }

        int totalLen = ScintillaInterop.GetByteLength(hwnd);
        if (totalLen <= 0) return true;

        var full = ScintillaInterop.ByteSpan(hwnd, 0, totalLen);
        var needle = full.Slice(mainStart, Math.Min(mainEnd - mainStart, totalLen - mainStart));
        if (needle.IsEmpty) return true;

        var matches = new List<ByteRange> { new(mainStart, mainEnd) };
        Collect(full, needle, mainEnd, full.Length, matches);
        Collect(full, needle, 0, mainStart, matches);

        if (matches.Count > MaxMultiCarets)
        {
            _error = "Слишком много курсоров. Допустимо до 10.000.";
            InvalidateChrome();
            return true;
        }

        ScintillaInterop.Send(hwnd, ScintillaInterop.SCI_CLEARSELECTIONS, 0, 0);
        ScintillaInterop.Send(hwnd, ScintillaInterop.SCI_SETSELECTION, (nuint)matches[0].End, matches[0].Start);
        for (int i = 1; i < matches.Count; i++)
        {
            ScintillaInterop.Send(hwnd, ScintillaInterop.SCI_ADDSELECTION, (nuint)matches[i].End, matches[i].Start);
        }
        ScintillaInterop.Send(hwnd, ScintillaInterop.SCI_SETMAINSELECTION, 0, 0);
        ScintillaInterop.Send(hwnd, ScintillaInterop.SCI_SCROLLCARET, 0, 0);
        UpdateActiveCursor();
        UpdateEditorLinksAndLineNumbers(doc);
        return true;

        static void Collect(ReadOnlySpan<byte> text, ReadOnlySpan<byte> needle, int from, int limit, List<ByteRange> output)
        {
            int pos = from;
            while (pos <= limit - needle.Length && output.Count <= MaxMultiCarets)
            {
                int rel = text.Slice(pos, limit - pos).IndexOf(needle);
                if (rel < 0) break;
                int start = pos + rel;
                int end = start + needle.Length;
                output.Add(new ByteRange(start, end));
                pos = end;
            }
        }
    }

    private bool AddVerticalCaret(Document doc, int direction)
    {
        nint hwnd = doc.EditorHwnd;
        int count = Math.Max(1, (int)ScintillaInterop.Send(hwnd, ScintillaInterop.SCI_GETSELECTIONS, 0, 0));
        if (count >= MaxMultiCarets)
        {
            _error = "Слишком много курсоров. Допустимо до 10.000.";
            InvalidateChrome();
            return true;
        }

        int main = Math.Clamp((int)ScintillaInterop.Send(hwnd, ScintillaInterop.SCI_GETMAINSELECTION, 0, 0), 0, count - 1);
        int mainCaret = (int)ScintillaInterop.Send(hwnd, ScintillaInterop.SCI_GETSELECTIONNCARET, (nuint)main, 0);

        int preferredCol;
        if (_preferredVerticalColumn is { } cached && cached.Id == doc.Id && cached.Revision == doc.Revision)
        {
            preferredCol = cached.Column;
        }
        else
        {
            preferredCol = GetUtf8CharColumn(hwnd, mainCaret);
            _preferredVerticalColumn = (doc.Id, doc.Revision, preferredCol);
        }

        int extremeCaret = mainCaret;
        for (int i = 0; i < count; i++)
        {
            int c = (int)ScintillaInterop.Send(hwnd, ScintillaInterop.SCI_GETSELECTIONNCARET, (nuint)i, 0);
            if (direction < 0 ? c < extremeCaret : c > extremeCaret) extremeCaret = c;
        }

        int currentLine = ScintillaInterop.LineFromByte(hwnd, extremeCaret);
        int targetLine = currentLine + direction;
        int lineCount = ScintillaInterop.GetLineCount(hwnd);
        if (targetLine < 0 || targetLine >= lineCount) return true;

        int targetOffset = ByteOffsetAtUtf8CharColumn(hwnd, targetLine, preferredCol);
        ScintillaInterop.Send(hwnd, ScintillaInterop.SCI_ADDSELECTION, (nuint)targetOffset, targetOffset);
        ScintillaInterop.Send(hwnd, ScintillaInterop.SCI_SCROLLCARET, 0, 0);
        UpdateActiveCursor();
        UpdateEditorLinksAndLineNumbers(doc);
        return true;
    }

    private bool TryMultiCaretLinePaste(Document doc)
    {
        nint hwnd = doc.EditorHwnd;
        int count = (int)ScintillaInterop.Send(hwnd, ScintillaInterop.SCI_GETSELECTIONS, 0, 0);
        if (count <= 1 || ScintillaInterop.Send(hwnd, SCI_GETREADONLY, 0, 0) != 0) return false;

        string? clip = ReadClipboardUnicodeText(hwnd);
        if (string.IsNullOrEmpty(clip)) return false;

        int lineCount = 1;
        foreach (char ch in clip)
        {
            if (ch == '\n' && ++lineCount > count) break;
        }
        if (lineCount != count) return false;

        string[] lines = clip.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].EndsWith('\r')) lines[i] = lines[i][..^1];
        }

        int main = Math.Clamp((int)ScintillaInterop.Send(hwnd, ScintillaInterop.SCI_GETMAINSELECTION, 0, 0), 0, count - 1);
        var ordered = new (int OrigIndex, int Start, int End)[count];
        for (int i = 0; i < count; i++)
        {
            ordered[i] = (
                i,
                (int)ScintillaInterop.Send(hwnd, ScintillaInterop.SCI_GETSELECTIONNSTART, (nuint)i, 0),
                (int)ScintillaInterop.Send(hwnd, ScintillaInterop.SCI_GETSELECTIONNEND, (nuint)i, 0));
        }
        Array.Sort(ordered, (a, b) => a.Start.CompareTo(b.Start));

        var newOffsets = new int[count];
        int delta = 0;
        var utf8Lines = new byte[count][];
        for (int i = 0; i < count; i++)
        {
            utf8Lines[i] = Encoding.UTF8.GetBytes(lines[i]);
            int pos = ordered[i].Start + delta + utf8Lines[i].Length;
            newOffsets[i] = pos;
            delta += utf8Lines[i].Length - (ordered[i].End - ordered[i].Start);
        }

        ScintillaInterop.Send(hwnd, ScintillaInterop.SCI_BEGINUNDOACTION, 0, 0);
        try
        {
            for (int i = count - 1; i >= 0; i--)
            {
                byte[] bytes = utf8Lines[i];
                ScintillaInterop.Send(hwnd, ScintillaInterop.SCI_SETTARGETSTART, (nuint)ordered[i].Start, 0);
                ScintillaInterop.Send(hwnd, ScintillaInterop.SCI_SETTARGETEND, (nuint)ordered[i].End, 0);
                fixed (byte* ptr = bytes)
                {
                    ScintillaInterop.Send(hwnd, ScintillaInterop.SCI_REPLACETARGET, (nuint)bytes.Length,
                        bytes.Length == 0 ? 0 : (nint)ptr);
                }
            }

            ScintillaInterop.Send(hwnd, ScintillaInterop.SCI_CLEARSELECTIONS, 0, 0);
            int mainSortedIndex = 0;
            for (int i = 0; i < count; i++)
            {
                if (ordered[i].OrigIndex == main) mainSortedIndex = i;
                if (i == 0)
                    ScintillaInterop.Send(hwnd, ScintillaInterop.SCI_SETSELECTION, (nuint)newOffsets[i], newOffsets[i]);
                else
                    ScintillaInterop.Send(hwnd, ScintillaInterop.SCI_ADDSELECTION, (nuint)newOffsets[i], newOffsets[i]);
            }
            ScintillaInterop.Send(hwnd, ScintillaInterop.SCI_SETMAINSELECTION, (nuint)mainSortedIndex, 0);
            ScintillaInterop.Send(hwnd, ScintillaInterop.SCI_SCROLLCARET, 0, 0);
        }
        finally
        {
            ScintillaInterop.Send(hwnd, ScintillaInterop.SCI_ENDUNDOACTION, 0, 0);
        }

        doc.Dirty = true;
        doc.Revision++;
        UpdateActiveCursor();
        UpdateEditorLinksAndLineNumbers(doc);
        return true;
    }

    private bool TryHandleEditorMouse(nint editorHwnd, uint msg, nuint wParam, nint lParam)
    {
        var doc = _documents.Find(d => d.EditorHwnd == editorHwnd && d.Log is null);
        if (doc is null) return false;

        if (msg == Win32.WM_MOUSEWHEEL)
        {
            bool ctrlWheel = (wParam & MK_CONTROL) != 0 || Win32.IsKeyDown(Win32.VK_CONTROL);
            if (!ctrlWheel) return false;

            int delta = Win32.GetWheelDelta(wParam);
            if (delta != 0)
            {
                Zoom(delta > 0 ? 1.0f : -1.0f);
                _settings = _settings with { FontSize = _fontSize };
                try { _settings.Save(); }
                catch { }
                InvalidateChrome();
            }
            return true;
        }

        int x = Win32.GetXParam(lParam);
        int y = Win32.GetYParam(lParam);

        if (msg == Win32.WM_LBUTTONDOWN)
        {
            bool ctrl = Win32.IsKeyDown(Win32.VK_CONTROL);
            bool shift = Win32.IsKeyDown(Win32.VK_SHIFT);
            bool alt = Win32.IsKeyDown(VK_MENU);

            if (ctrl && shift && !alt)
            {
                int pos = (int)ScintillaInterop.Send(editorHwnd, ScintillaInterop.SCI_POSITIONFROMPOINTCLOSE, (nuint)x, y);
                if (pos >= 0 && TryFindUrlAtPosition(editorHwnd, pos, out string url))
                {
                    doc.VisitedLinks ??= new HashSet<string>(StringComparer.Ordinal);
                    if (doc.VisitedLinks.Add(url))
                    {
                        _linkIndicatorCache.Remove(doc.Id);
                        UpdateEditorLinksAndLineNumbers(doc);
                    }
                    TryOpenHyperlink(url);
                    return true;
                }
                return false;
            }

            if (ctrl && !shift && !alt)
            {
                int pos = (int)ScintillaInterop.Send(editorHwnd, ScintillaInterop.SCI_POSITIONFROMPOINTCLOSE, (nuint)x, y);
                if (pos >= 0)
                {
                    ToggleOrAddCaretAt(doc, pos);
                    return true;
                }
            }

            if (alt && !ctrl && !shift)
            {
                _altClickStart = (editorHwnd, x, y);
            }
            else
            {
                _altClickStart = null;
            }
        }
        else if (msg == Win32.WM_LBUTTONUP && _altClickStart is { } click && click.Hwnd == editorHwnd)
        {
            _altClickStart = null;
            if (Win32.IsKeyDown(VK_MENU) && Math.Abs(x - click.X) <= 2 && Math.Abs(y - click.Y) <= 2)
            {
                int pos = (int)ScintillaInterop.Send(editorHwnd, ScintillaInterop.SCI_POSITIONFROMPOINTCLOSE, (nuint)x, y);
                if (pos >= 0)
                {
                    ToggleOrAddCaretAt(doc, pos);
                    return true;
                }
            }
        }

        return false;
    }

    private void ToggleOrAddCaretAt(Document doc, int pos)
    {
        nint hwnd = doc.EditorHwnd;
        int count = Math.Max(1, (int)ScintillaInterop.Send(hwnd, ScintillaInterop.SCI_GETSELECTIONS, 0, 0));
        for (int i = 0; i < count; i++)
        {
            int start = (int)ScintillaInterop.Send(hwnd, ScintillaInterop.SCI_GETSELECTIONNSTART, (nuint)i, 0);
            int end = (int)ScintillaInterop.Send(hwnd, ScintillaInterop.SCI_GETSELECTIONNEND, (nuint)i, 0);
            if (pos >= start && pos <= end)
            {
                if (count > 1)
                {
                    ScintillaInterop.Send(hwnd, ScintillaInterop.SCI_DROPSELECTIONN, (nuint)i, 0);
                    Win32.SetFocus(hwnd);
                    UpdateActiveCursor();
                    UpdateEditorLinksAndLineNumbers(doc);
                }
                return;
            }
        }

        if (count >= MaxMultiCarets)
        {
            _error = "Слишком много курсоров. Допустимо до 10.000.";
            InvalidateChrome();
            return;
        }

        ScintillaInterop.Send(hwnd, ScintillaInterop.SCI_ADDSELECTION, (nuint)pos, pos);
        Win32.SetFocus(hwnd);
        UpdateActiveCursor();
        UpdateEditorLinksAndLineNumbers(doc);
    }

    private void UpdateEditorLinksAndLineNumbers(Document doc)
    {
        if (doc.Log is not null || doc.EditorHwnd == 0) return;

        nint hwnd = doc.EditorHwnd;
        doc.LastSelectedChars = ScintillaInterop.CountSelectedChars(hwnd, out int caretCount);
        doc.LastCaretCount = caretCount;

        int firstVis = ScintillaInterop.GetScroll(hwnd).FirstVisibleLine;
        int linesOnScreen = ScintillaInterop.LinesOnScreen(hwnd);
        int lineCount = ScintillaInterop.GetLineCount(hwnd);
        int firstDocLine = Math.Clamp(ScintillaInterop.DocumentLineFromVisible(hwnd, firstVis), 0, Math.Max(0, lineCount - 1));
        int lastDocLine = Math.Clamp(ScintillaInterop.DocumentLineFromVisible(hwnd, firstVis + linesOnScreen + 1), firstDocLine, Math.Max(0, lineCount - 1));

        UpdateGroupedLineNumbers(doc, hwnd, firstDocLine, lastDocLine, lineCount);
        UpdateVisibleHyperlinks(doc, hwnd, firstDocLine, lastDocLine);
    }

    private void UpdateGroupedLineNumbers(Document doc, nint hwnd, int firstDocLine, int lastDocLine, int lineCount)
    {
        if (_lineNumberCache.TryGetValue(doc.Id, out var cached) &&
            cached.Revision == doc.Revision &&
            cached.FirstLine == firstDocLine &&
            cached.LastLine == lastDocLine &&
            cached.LineCount == lineCount)
        {
            return;
        }

        _lineNumberCache[doc.Id] = (doc.Revision, firstDocLine, lastDocLine, lineCount);

        ScintillaInterop.Send(hwnd, ScintillaInterop.SCI_SETMARGINTYPEN, 0, ScintillaInterop.SC_MARGIN_RTEXT);
        byte* widthBuf = stackalloc byte[64];
        int written = LineNumbers.FormatUtf8(Math.Max(10, lineCount), new Span<byte>(widthBuf, 62));
        widthBuf[written] = (byte)' ';
        widthBuf[written + 1] = 0;
        int pxWidth = (int)ScintillaInterop.Send(hwnd, ScintillaInterop.SCI_TEXTWIDTH, ScintillaInterop.STYLE_LINENUMBER, (nint)widthBuf);
        int dpi = (int)Win32.GetDpiForWindow(hwnd);
        if (dpi == 0) dpi = 96;
        ScintillaInterop.Send(hwnd, ScintillaInterop.SCI_SETMARGINWIDTHN, 0, pxWidth + Win32.MulDiv(9, dpi, 96));

        byte* lineBuf = stackalloc byte[48];
        Span<byte> lineSpan = new(lineBuf, 47);
        for (int line = firstDocLine; line <= lastDocLine; line++)
        {
            int len = LineNumbers.FormatUtf8(line + 1, lineSpan);
            lineBuf[len] = 0;
            ScintillaInterop.Send(hwnd, ScintillaInterop.SCI_MARGINSETTEXT, (nuint)line, (nint)lineBuf);
            ScintillaInterop.Send(hwnd, ScintillaInterop.SCI_MARGINSETSTYLE, (nuint)line, ScintillaInterop.STYLE_LINENUMBER);
        }
    }

    private void UpdateVisibleHyperlinks(Document doc, nint hwnd, int firstDocLine, int lastDocLine)
    {
        int visitedCount = doc.VisitedLinks?.Count ?? 0;
        if (_linkIndicatorCache.TryGetValue(doc.Id, out var cached) &&
            cached.Revision == doc.Revision &&
            cached.FirstLine == firstDocLine &&
            cached.LastLine == lastDocLine &&
            cached.VisitedCount == visitedCount)
        {
            return;
        }

        _linkIndicatorCache[doc.Id] = (doc.Revision, firstDocLine, lastDocLine, visitedCount);

        int startByte = ScintillaInterop.LineStart(hwnd, firstDocLine);
        int endByte = ScintillaInterop.LineStart(hwnd, lastDocLine + 1);
        if (endByte <= startByte) endByte = ScintillaInterop.GetByteLength(hwnd);
        int spanLen = Math.Min(Math.Max(0, endByte - startByte), 262_144);

        ScintillaInterop.Send(hwnd, SCI_INDICSETSTYLE, ScintillaInterop.IndicLink, INDIC_PLAIN);
        ScintillaInterop.Send(hwnd, SCI_INDICSETFORE, ScintillaInterop.IndicLink, (nint)ThemePalette.Link);
        ScintillaInterop.Send(hwnd, SCI_INDICSETSTYLE, ScintillaInterop.IndicVisitedLink, INDIC_PLAIN);
        ScintillaInterop.Send(hwnd, SCI_INDICSETFORE, ScintillaInterop.IndicVisitedLink, (nint)ThemePalette.VisitedLink);

        ScintillaInterop.Send(hwnd, SCI_SETINDICATORCURRENT, ScintillaInterop.IndicLink, 0);
        ScintillaInterop.Send(hwnd, SCI_INDICATORCLEARRANGE, (nuint)startByte, spanLen);
        ScintillaInterop.Send(hwnd, SCI_SETINDICATORCURRENT, ScintillaInterop.IndicVisitedLink, 0);
        ScintillaInterop.Send(hwnd, SCI_INDICATORCLEARRANGE, (nuint)startByte, spanLen);

        if (spanLen == 0) return;

        var utf8 = ScintillaInterop.ByteSpan(hwnd, startByte, spanLen);
        if (utf8.IndexOf("://"u8) < 0 && utf8.IndexOf("mailto:"u8) < 0) return;

        string text = Encoding.UTF8.GetString(utf8);
        int charCursor = 0;
        int byteCursor = startByte;
        foreach (Match match in UrlPattern().Matches(text))
        {
            string url = TrimTrailingUrlPunctuation(match.Value);
            if (url.Length == 0) continue;

            byteCursor += Encoding.UTF8.GetByteCount(text.AsSpan(charCursor, match.Index - charCursor));
            int urlBytes = Encoding.UTF8.GetByteCount(url);
            int matchFullBytes = Encoding.UTF8.GetByteCount(text.AsSpan(match.Index, match.Length));
            charCursor = match.Index + match.Length;

            uint indicator = doc.VisitedLinks?.Contains(url) == true
                ? (uint)ScintillaInterop.IndicVisitedLink
                : (uint)ScintillaInterop.IndicLink;
            ScintillaInterop.Send(hwnd, SCI_SETINDICATORCURRENT, indicator, 0);
            ScintillaInterop.Send(hwnd, SCI_INDICATORFILLRANGE, (nuint)byteCursor, urlBytes);

            byteCursor += matchFullBytes;
        }
    }

    private static bool TryFindUrlAtPosition(nint hwnd, int bytePosition, out string url)
    {
        url = "";
        int line = ScintillaInterop.LineFromByte(hwnd, bytePosition);
        int lineStart = ScintillaInterop.LineStart(hwnd, line);
        var lineBytes = ScintillaInterop.LineBytes(hwnd, line);
        if (lineBytes.IsEmpty) return false;

        int byteInLine = Math.Clamp(bytePosition - lineStart, 0, lineBytes.Length);
        if (lineBytes.Length > 16_384) return false;

        string lineText = Encoding.UTF8.GetString(lineBytes);
        int charPos = Encoding.UTF8.GetCharCount(lineBytes[..byteInLine]);
        foreach (Match match in UrlPattern().Matches(lineText))
        {
            string trimmed = TrimTrailingUrlPunctuation(match.Value);
            if (trimmed.Length == 0) continue;
            if (charPos >= match.Index && charPos <= match.Index + trimmed.Length)
            {
                url = trimmed;
                return true;
            }
        }
        return false;
    }

    private static string TrimTrailingUrlPunctuation(string candidate)
    {
        int end = candidate.Length;
        while (end > 0 && candidate[end - 1] is '.' or ',' or ';' or ':' or '!' or '?')
        {
            end--;
        }
        return candidate[..end];
    }

    private static void TryOpenHyperlink(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeMailto))
        {
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch
        {
        }
    }

    private static bool TryGetWordRangeAt(nint hwnd, int pos, out int start, out int end)
    {
        int len = ScintillaInterop.GetByteLength(hwnd);
        pos = Math.Clamp(pos, 0, len);
        int s = (int)ScintillaInterop.Send(hwnd, ScintillaInterop.SCI_WORDSTARTPOSITION, (nuint)pos, 1);
        int e = (int)ScintillaInterop.Send(hwnd, ScintillaInterop.SCI_WORDENDPOSITION, (nuint)pos, 1);
        if (e <= s && pos > 0)
        {
            s = (int)ScintillaInterop.Send(hwnd, ScintillaInterop.SCI_WORDSTARTPOSITION, (nuint)(pos - 1), 1);
            e = (int)ScintillaInterop.Send(hwnd, ScintillaInterop.SCI_WORDENDPOSITION, (nuint)(pos - 1), 1);
        }
        start = s;
        end = e;
        return e > s;
    }

    private static int GetUtf8CharColumn(nint hwnd, int byteOffset)
    {
        int line = ScintillaInterop.LineFromByte(hwnd, byteOffset);
        int lineStart = ScintillaInterop.LineStart(hwnd, line);
        int len = Math.Max(0, byteOffset - lineStart);
        var slice = ScintillaInterop.ByteSpan(hwnd, lineStart, len);
        int chars = 0;
        foreach (byte b in slice)
        {
            if ((b & 0xC0) != 0x80) chars++;
        }
        return chars;
    }

    private static int ByteOffsetAtUtf8CharColumn(nint hwnd, int line, int targetCharColumn)
    {
        int lineStart = ScintillaInterop.LineStart(hwnd, line);
        var lineBytes = ScintillaInterop.LineBytes(hwnd, line);
        if (targetCharColumn <= 0 || lineBytes.IsEmpty) return lineStart;

        int chars = 0;
        for (int i = 0; i < lineBytes.Length; i++)
        {
            if ((lineBytes[i] & 0xC0) != 0x80)
            {
                if (chars == targetCharColumn) return lineStart + i;
                chars++;
            }
        }
        return lineStart + lineBytes.Length;
    }

    private static string? ReadClipboardUnicodeText(nint owner)
    {
        if (!Win32.OpenClipboard(owner)) return null;
        try
        {
            nint handle = GetClipboardData(Win32.CF_UNICODETEXT);
            if (handle == 0) return null;
            nint ptr = Win32.GlobalLock(handle);
            if (ptr == 0) return null;
            try
            {
                return Marshal.PtrToStringUni(ptr);
            }
            finally
            {
                Win32.GlobalUnlock(handle);
            }
        }
        finally
        {
            Win32.CloseClipboard();
        }
    }
}
