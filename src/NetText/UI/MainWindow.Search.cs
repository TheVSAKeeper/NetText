using System.Text;
using System.Text.RegularExpressions;
using NetText.Core;
using NetText.Native;

namespace NetText.UI;

internal sealed unsafe partial class MainWindow
{
    private const int IdTextSearchList = 2012;
    private const nuint SearchDebounceTimerId = 3;
    private const uint SearchDebounceMs = 250;

    private nint _textSearchList;
    private bool _replaceOpen;
    private bool _highlightCurrentOnly;
    private bool _searchListExpanded = true;
    private SearchHit[] _searchResults = [];
    private readonly Dictionary<ulong, ulong> _resultRevisions = [];
    private Regex? _visibleSearchRegex;
    private sealed record PausedNavigation(SearchCommand Command, SearchQuery Query, SearchHit Origin,
        SearchNavigationCursor Cursor, (ulong Id, ulong Revision)[] Sources);
    private PausedNavigation? _pausedNavigation;
    private readonly Dictionary<ulong, (ByteRange[] Visible, ByteRange[] Current, int Line, ulong Revision)> _editorIndicators = [];

    private void CreateSearchList()
    {
        _textSearchList = Win32.CreateWindowExW(0, "LISTBOX", "", Win32.WS_CHILD | Win32.WS_BORDER |
            Win32.WS_VSCROLL | Win32.WS_TABSTOP | Win32.LBS_NOTIFY | Win32.LBS_NOINTEGRALHEIGHT,
            0, 0, 0, 0, _hwnd, IdTextSearchList, _hInstance, null);
        Win32.SendMessageW(_textSearchList, Win32.WM_SETFONT, (nuint)_smallFont, 1);
        Win32.SetWindowTheme(_textSearchList, "DarkMode_Explorer", null);
    }

    private int GetSearchPanelLogicalHeight() =>
        _searchOpen ? (_searchListExpanded ? EditorSearchLogicalHeight : 72) : 0;

    private static bool IsAutomaticSearchSize(Document doc)
    {
        long size = doc.File?.Version?.Bytes ?? (long)ScintillaInterop.GetByteLength(doc.EditorHwnd);
        return size < EditorSearch.AutomaticSearchMaxBytes;
    }

    private void ScheduleAutoSearch()
    {
        CancelAutoSearch();
        if (!_searchOpen || _showHome || !HasDocument) return;
        var doc = ActiveDocument;
        if (doc.Log is not null || string.IsNullOrEmpty(_searchQuery)) return;
        if (!IsAutomaticSearchSize(doc))
        {
            _searchStatus = "Нажмите «Найти» для поиска по большому файлу";
            InvalidateChrome();
            return;
        }
        if (_hwnd != 0)
        {
            Win32.SetTimer(_hwnd, SearchDebounceTimerId, SearchDebounceMs, 0);
        }
    }

    private void CancelAutoSearch()
    {
        if (_hwnd != 0)
        {
            Win32.KillTimer(_hwnd, SearchDebounceTimerId);
        }
    }

    private void OnSearchDebounceTimer()
    {
        CancelAutoSearch();
        if (!_searchOpen || _showHome || !HasDocument || _searchInFlight || _busy is not null) return;
        var doc = ActiveDocument;
        if (doc.Log is not null || string.IsNullOrEmpty(_searchQuery) || !IsAutomaticSearchSize(doc)) return;
        RunEditorSearch(SearchCommand.List);
    }

    private void RunExplicitSearch()
    {
        CancelAutoSearch();
        RunEditorSearch(SearchCommand.List);
    }

    private void ToggleHighlightCurrentOnly()
    {
        _highlightCurrentOnly = !_highlightCurrentOnly;
        ClearEditorIndicators();
        if (HasDocument && !_showHome && ActiveDocument.Log is null)
        {
            UpdateEditorIndicators(ActiveDocument);
        }
        InvalidateChrome();
    }

    private void ToggleSearchList()
    {
        _searchListExpanded = !_searchListExpanded;
        LayoutAndSyncControls();
        InvalidateChrome();
    }

    private void FindAllToMultiCarets()
    {
        CancelAutoSearch();
        if (!HasDocument || _busy is not null || _searchInFlight || _showHome) return;
        var active = ActiveDocument;
        if (active.Log is not null || active.Placeholder || active.EditorHwnd == 0) return;

        int byteLength = ScintillaInterop.GetByteLength(active.EditorHwnd);
        if (byteLength > EditorSearch.MaxDocumentBytes)
        {
            _searchStatus = "Поиск и замена доступны в документах до 64 МиБ.";
            InvalidateChrome();
            return;
        }

        var query = new SearchQuery(_searchQuery, _searchRegex, _searchCase, _searchWhole, AllTabs: false);
        var snapshot = ScintillaInterop.CopyUtf8BytesNative(active.EditorHwnd);
        ulong revision = active.Revision;
        long gen = ++_searchGen;
        _searchInFlight = true;
        _searchStatus = "Поиск…";
        InvalidateChrome();

        try
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                FindAllResult result;
                using (snapshot)
                {
                    result = EditorSearch.FindAllRanges(snapshot.Span, query,
                        EditorSearch.MaxMultiCaretMatches, TimeSpan.FromMilliseconds(EditorSearch.FindAllTimeoutMs));
                }

                DispatchUi(() =>
                {
                    if (gen != _searchGen) return;
                    _searchInFlight = false;

                    if (_busy is not null || _showHome || _activeId != active.Id || active.Revision != revision)
                    {
                        _searchStatus = "Текст или вкладка изменились. Повторите поиск.";
                        InvalidateChrome();
                        return;
                    }

                    if (!string.IsNullOrEmpty(result.Error))
                    {
                        _searchStatus = result.Error;
                        InvalidateChrome();
                        return;
                    }

                    if (result.TimedOut)
                    {
                        _searchStatus = "Истекло время поиска. Уточните запрос.";
                        InvalidateChrome();
                        return;
                    }

                    if (result.TotalMatches > EditorSearch.MaxMultiCaretMatches)
                    {
                        _searchStatus = $"Найдено {LineNumbers.Format(result.TotalMatches)} · максимум {LineNumbers.Format(EditorSearch.MaxMultiCaretMatches)} курсоров";
                        InvalidateChrome();
                        return;
                    }

                    if (result.Ranges.Count == 0)
                    {
                        _searchStatus = "Совпадений нет";
                        InvalidateChrome();
                        return;
                    }

                    var ranges = result.Ranges;
                    ScintillaInterop.Send(active.EditorHwnd, ScintillaInterop.SCI_CLEARSELECTIONS, 0, 0);
                    ScintillaInterop.Send(active.EditorHwnd, ScintillaInterop.SCI_SETSELECTION, (nuint)ranges[0].End, ranges[0].Start);
                    for (int i = 1; i < ranges.Count; i++)
                    {
                        ScintillaInterop.Send(active.EditorHwnd, ScintillaInterop.SCI_ADDSELECTION, (nuint)ranges[i].End, ranges[i].Start);
                    }
                    ScintillaInterop.Send(active.EditorHwnd, ScintillaInterop.SCI_SETMAINSELECTION, 0, 0);
                    ScintillaInterop.Send(active.EditorHwnd, ScintillaInterop.SCI_SCROLLCARET, 0, 0);

                    _searchStatus = $"Выделено {LineNumbers.Format(ranges.Count)}";
                    UpdateActiveCursor();
                    UpdateEditorLinksAndLineNumbers(active);
                    FocusActiveEditor();
                    InvalidateChrome();
                });
            });
        }
        catch
        {
            snapshot.Dispose();
            _searchInFlight = false;
            throw;
        }
    }

    private void PaintUpgradedEditorSearch(nint hdc, int width, int top)
    {
        int pad = S(12);
        int gap = S(4);
        int btnH = S(26);
        int qW = Math.Clamp(width / 4, S(145), S(320));
        int x = pad + qW + S(8);
        int y1 = top + S(5);
        int y2 = top + S(36);
        bool busy = _busy is not null || _searchInFlight;
        bool replaceDisabled = busy || (HasDocument && ActiveDocument.Placeholder);

        x = DrawButton(hdc, x, y1, btnH, "Найти", HitKind.SearchRun, 0, busy, false) + gap;
        x = DrawButton(hdc, x, y1, btnH, "Найти все", HitKind.SearchFindAll, 0, busy || replaceDisabled, false) + gap;
        x = DrawButton(hdc, x, y1, btnH, ".*", HitKind.SearchRegex, 0, false, _searchRegex) + gap;
        x = DrawButton(hdc, x, y1, btnH, "Aa", HitKind.SearchCase, 0, false, _searchCase) + gap;
        x = DrawButton(hdc, x, y1, btnH, "Целое слово", HitKind.SearchWhole, 0, false, _searchWhole) + gap;
        x = DrawButton(hdc, x, y1, btnH, "Все вкладки", HitKind.SearchAllTabs, 0, false, _searchAllTabs) + gap;
        x = DrawButton(hdc, x, y1, btnH, "Только текущее", HitKind.SearchHighlight, 0, false, _highlightCurrentOnly) + gap;
        x = DrawButton(hdc, x, y1, btnH, _searchListExpanded ? "Список ▾" : "Список ▸", HitKind.SearchList, 0, false, _searchListExpanded) + gap;
        DrawButtonFixed(hdc, width - pad - S(28), y1, S(28), btnH, "×", HitKind.SearchClose, 0, false, false);

        int x2 = _replaceOpen ? pad + qW + S(8) : pad;
        x2 = DrawButton(hdc, x2, y2, btnH, "Замена", HitKind.SearchToggleReplace, 0, false, _replaceOpen) + gap;
        if (_replaceOpen)
        {
            x2 = DrawButton(hdc, x2, y2, btnH, "Заменить", HitKind.SearchReplaceOne, 0, replaceDisabled, false) + gap;
            x2 = DrawButton(hdc, x2, y2, btnH, "Заменить всё", HitKind.SearchReplaceAll, 0, replaceDisabled, false) + S(10);
        }

        var stRect = new Win32.RECT { Left = x2, Top = y2, Right = Math.Max(x2, width - pad), Bottom = y2 + btnH };
        Win32.SelectObject(hdc, _smallFont);
        Win32.SetTextColor(hdc, ThemePalette.Muted);
        Win32.DrawTextW(hdc, _searchStatus, -1, &stRect, Win32.DT_LEFT | Win32.DT_VCENTER | Win32.DT_SINGLELINE | Win32.DT_END_ELLIPSIS | Win32.DT_NOPREFIX);
    }

    private void ShowSearchResults(SearchQuery query, SearchOutcome.Found found,
        IEnumerable<(ulong Id, ulong Rev, SearchUtf8Source Src)> snapshots)
    {
        _searchResults = found.Results?.ToArray() ?? [];
        _resultRevisions.Clear();
        foreach (var item in snapshots) _resultRevisions[item.Id] = item.Rev;
        var rows = new List<string>(_searchResults.Length);
        foreach (var hit in _searchResults)
        {
            var doc = _documents.Find(item => item.Id == hit.Tab);
            if (doc is null) continue;
            int line = ScintillaInterop.LineFromByte(doc.EditorHwnd, hit.Range.Start);
            var text = ScintillaInterop.LineBytes(doc.EditorHwnd, line);
            int bytes = Math.Min(text.Length, 180);
            rows.Add($"{doc.Name} · {LineNumbers.Format(line + 1)} · {Encoding.UTF8.GetString(text[..bytes]).Replace('\0', ' ')}");
        }
        ToolDialog.SetItems(_textSearchList, rows);
        _visibleSearchRegex = EditorSearch.TryCompile(query, out var regex, out _) ? regex : null;
        _searchStatus = found.Partial ? $"Найдено как минимум {LineNumbers.Format(found.Total)}, истёк лимит времени" :
            $"Совпадений: {LineNumbers.Format(found.Total)}" + (found.Total > _searchResults.Length ? $" · первые {_searchResults.Length}" : "");
        ClearEditorIndicators();
        if (HasDocument && !_showHome && ActiveDocument.Log is null) UpdateEditorIndicators(ActiveDocument);
    }

    private void ChooseTextSearchResult()
    {
        int index = ToolDialog.Selection(_textSearchList);
        if ((uint)index >= (uint)_searchResults.Length) return;
        var hit = _searchResults[index];
        var doc = _documents.Find(item => item.Id == hit.Tab);
        if (doc is null || !_resultRevisions.TryGetValue(hit.Tab, out ulong revision) || doc.Revision != revision)
        {
            _searchStatus = "Текст изменился. Повторите поиск.";
            InvalidateChrome();
            return;
        }
        var query = new SearchQuery(_searchQuery, _searchRegex, _searchCase, _searchWhole, _searchAllTabs);
        _lastSearchHit = (query, hit);
        SelectTab(doc.Id);
        ScintillaInterop.SetSelection(doc.EditorHwnd, hit.Range.Start, hit.Range.End, false);
        ClearEditorIndicators();
        UpdateEditorIndicators(doc);
    }

    private void ClearSearchResults()
    {
        _searchResults = [];
        _resultRevisions.Clear();
        _visibleSearchRegex = null;
        _pausedNavigation = null;
        ToolDialog.SetItems(_textSearchList, []);
        ClearEditorIndicators();
    }

    private void ApplyNavigation(SearchCommand command, SearchQuery query, SearchHit origin,
        SearchOutcome.Navigated outcome, (ulong Id, ulong Revision)[] revisions)
    {
        _pausedNavigation = outcome.Cursor is { } cursor ? new PausedNavigation(command, query, origin, cursor, revisions) : null;
        if (outcome.Hit is not { } hit || _documents.Find(item => item.Id == hit.Tab) is not { } doc)
        {
            _searchStatus = _pausedNavigation is null ? "Совпадений нет" : "Переход приостановлен. Нажмите F3 ещё раз.";
            return;
        }
        _lastSearchHit = (query, hit);
        _visibleSearchRegex ??= EditorSearch.TryCompile(query, out var regex, out _) ? regex : null;
        foreach (var (id, revision) in revisions) _resultRevisions.TryAdd(id, revision);
        _resultRevisions[doc.Id] = doc.Revision;
        SelectTab(hit.Tab);
        ScintillaInterop.SetSelection(doc.EditorHwnd, hit.Range.Start, hit.Range.End, false);
        ClearEditorIndicators();
        UpdateEditorIndicators(doc);
        _searchStatus = "Текущее совпадение";
    }

    private void ClearEditorIndicators()
    {
        foreach (var doc in _documents)
        {
            if (!_editorIndicators.TryGetValue(doc.Id, out var old)) continue;
            ScintillaInterop.SetSearchIndicator(doc.EditorHwnd, false, old.Visible, []);
            ScintillaInterop.SetSearchIndicator(doc.EditorHwnd, true, old.Current, []);
        }
        _editorIndicators.Clear();
    }

    private void UpdateEditorIndicators(Document doc)
    {
        if (!_searchOpen || _showHome || doc.Log is not null || _visibleSearchRegex is null) return;
        int first = ScintillaInterop.GetScroll(doc.EditorHwnd).FirstVisibleLine;
        if (_editorIndicators.TryGetValue(doc.Id, out var previous) &&
            previous.Line == first && previous.Revision == doc.Revision) return;
        var active = _lastSearchHit is { Hit: var hit } && hit.Tab == doc.Id &&
            _resultRevisions.TryGetValue(doc.Id, out ulong revision) && revision == doc.Revision ? new[] { hit.Range } : [];
        var visible = new List<ByteRange>();
        if (!_highlightCurrentOnly)
        {
            int lines = ScintillaInterop.LinesOnScreen(doc.EditorHwnd);
            int firstDocumentLine = ScintillaInterop.DocumentLineFromVisible(doc.EditorHwnd, first);
            int lastDocumentLine = ScintillaInterop.DocumentLineFromVisible(doc.EditorHwnd, first + lines + 1);
            int start = ScintillaInterop.LineStart(doc.EditorHwnd, firstDocumentLine);
            int end = ScintillaInterop.LineStart(doc.EditorHwnd, lastDocumentLine + 1);
            if (end <= start) end = ScintillaInterop.GetByteLength(doc.EditorHwnd);
            var text = Encoding.UTF8.GetString(ScintillaInterop.ByteSpan(doc.EditorHwnd, start, Math.Min(end - start, 262144)));
            int chars = 0;
            int bytes = start;
            try
            {
                foreach (var match in _visibleSearchRegex.EnumerateMatches(text.AsSpan()))
                {
                    int offset = bytes + Encoding.UTF8.GetByteCount(text.AsSpan(chars, match.Index - chars));
                    bytes = offset + Encoding.UTF8.GetByteCount(text.AsSpan(match.Index, match.Length));
                    chars = match.Index + match.Length;
                    if (bytes > offset) visible.Add(new ByteRange(offset, bytes));
                    if (visible.Count >= 4096) break;
                }
            }
            catch (RegexMatchTimeoutException) { }
        }
        var ranges = visible.ToArray();
        _editorIndicators[doc.Id] = (ranges, active, first, doc.Revision);
        ScintillaInterop.SetSearchIndicator(doc.EditorHwnd, false, previous.Visible, ranges);
        ScintillaInterop.SetSearchIndicator(doc.EditorHwnd, true, previous.Current, active);
    }
}
