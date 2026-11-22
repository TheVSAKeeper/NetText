using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text;
using NetText.Core;
using NetText.Native;

namespace NetText.UI;

internal sealed unsafe partial class MainWindow
{
    private const int IdHistoryQuery = 2010;
    private const int IdHistoryList = 2011;
    private const int HistoryPageSize = 8;

    private enum HomeSectionKind
    {
        None = 0,
        Recent = 1,
        Closed = 2,
        Drafts = 3,
    }

    private sealed record HomeRowEntry(
        HomeSectionKind Section,
        RecentFile? File,
        ulong ClosedTabId,
        string Title,
        string? Subtitle,
        ulong? Lines,
        DateTimeOffset? Worked,
        long? Bytes,
        bool HasSavedDraft);

    private readonly HashSet<HomeSectionKind> _collapsedHomeSections = [];
    private readonly Dictionary<HomeSectionKind, int> _homeSectionPages = new()
    {
        [HomeSectionKind.Recent] = 0,
        [HomeSectionKind.Closed] = 0,
        [HomeSectionKind.Drafts] = 0,
    };
    private readonly List<HomeRowEntry> _homeRows = [];
    private readonly ConcurrentDictionary<string, long?> _homeFileSizes = new(StringComparer.OrdinalIgnoreCase);

    private bool _showHome;
    private bool _restoringSession;
    private nint _historyQuery;
    private nint _historyList;
    private int _historyPage;
    private string _homeLastQuery = "";

    private void CreateHomeControls()
    {
        _historyQuery = CreateEdit(IdHistoryQuery, "Поиск по имени или пути");
        _historyList = 0;
    }

    private void ToggleHome()
    {
        if (_showHome && _documents.Count > 0)
        {
            if (HasDocument) SelectTab(_activeId);
            else FocusEmptyGroup(_activeGroup);
        }
        else
        {
            _showHome = true;
            UpdateHistory();
            LayoutAndSyncControls();
            Win32.SetFocus(_historyQuery);
            InvalidateChrome();
        }
    }

    private void HomeSearchChanged()
    {
        _historyPage = 0;
        _homeSectionPages[HomeSectionKind.Recent] = 0;
        _homeSectionPages[HomeSectionKind.Closed] = 0;
        _homeSectionPages[HomeSectionKind.Drafts] = 0;
        UpdateHistory();
    }

    private void UpdateHistory()
    {
        string query = _historyQuery != 0 ? ReadControlText(_historyQuery).Trim() : "";
        if (!string.Equals(_homeLastQuery, query, StringComparison.Ordinal))
        {
            _homeLastQuery = query;
            _homeSectionPages[HomeSectionKind.Recent] = 0;
            _homeSectionPages[HomeSectionKind.Closed] = 0;
            _homeSectionPages[HomeSectionKind.Drafts] = 0;
        }
        InvalidateChrome();
    }

    private void OpenHistorySelection()
    {
        BuildHomeSections(out var recent, out var closedFiles, out var drafts, out _);
        if (recent.Length > 0)
        {
            var f = recent[0].File!;
            OpenPaths([f.Path], f.Windows1251, f.ViewAsLog);
            return;
        }
        if (closedFiles.Length > 0)
        {
            ReopenClosedTab(closedFiles[0].ClosedTabId);
            return;
        }
        if (drafts.Length > 0)
        {
            ReopenClosedTab(drafts[0].ClosedTabId);
        }
    }

    private void BuildHomeSections(
        out HomeRowEntry[] recentRows,
        out HomeRowEntry[] closedFileRows,
        out HomeRowEntry[] draftRows,
        out long totalDraftDiskBytes)
    {
        string query = _historyQuery != 0 ? ReadControlText(_historyQuery).Trim() : "";
        bool Matches(string? text) => string.IsNullOrEmpty(query) ||
            (!string.IsNullOrEmpty(text) && text.Contains(query, StringComparison.OrdinalIgnoreCase));

        var recentList = new List<HomeRowEntry>();
        foreach (var file in _recent.History)
        {
            string name = FileName(file.Path);
            if (!Matches(file.Path) && !Matches(name)) continue;
            long? size = ResolveCachedFileSize(file.Path);
            recentList.Add(new HomeRowEntry(
                HomeSectionKind.Recent,
                file,
                0,
                name,
                DisplayPath(file.Path),
                file.Lines,
                LastWorked(file),
                size,
                HasSavedDraft: false));
        }

        var closedFilesList = new List<HomeRowEntry>();
        var draftsList = new List<HomeRowEntry>();
        long totalDraftBytes = 0;

        for (int i = _closed.Count - 1; i >= 0; i--)
        {
            var (tab, bytes) = _closed[i];
            if (SessionJournal.IsKnownEmptyUntitledDraft(tab, bytes)) continue;

            if (tab.File is { } target)
            {
                string name = !string.IsNullOrWhiteSpace(tab.CustomTitle) ? tab.CustomTitle : FileName(target.Path);
                string full = DisplayPath(target.Path);
                if (!Matches(full) && !Matches(name)) continue;
                var recentInfo = _recent.History.FirstOrDefault(item => string.Equals(item.Path, target.Path, StringComparison.OrdinalIgnoreCase));
                long? size = ResolveCachedFileSize(target.Path) ?? target.Version?.Bytes;
                closedFilesList.Add(new HomeRowEntry(
                    HomeSectionKind.Closed,
                    null,
                    tab.Id,
                    name,
                    full,
                    tab.LineCount ?? recentInfo?.Lines,
                    tab.LastWorked ?? (recentInfo is null ? null : LastWorked(recentInfo)),
                    size,
                    HasSavedDraft: bytes is { Length: > 0 }));
            }
            else
            {
                long byteLen = bytes?.LongLength ?? 0;
                totalDraftBytes += byteLen;
                string title = !string.IsNullOrWhiteSpace(tab.CustomTitle)
                    ? tab.CustomTitle
                    : ClosedTabLabel(tab, bytes);
                if (!Matches(title)) continue;
                draftsList.Add(new HomeRowEntry(
                    HomeSectionKind.Drafts,
                    null,
                    tab.Id,
                    title,
                    null,
                    _settings.ShowDraftLineCount ? tab.LineCount : null,
                    tab.LastWorked,
                    byteLen,
                    HasSavedDraft: bytes is { Length: > 0 }));
            }
        }

        recentRows = [.. recentList];
        closedFileRows = [.. closedFilesList];
        draftRows = [.. draftsList];
        totalDraftDiskBytes = totalDraftBytes;
    }

    private long? ResolveCachedFileSize(string path)
    {
        if (_homeFileSizes.TryGetValue(path, out var cached)) return cached;
        _homeFileSizes[path] = null;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            long? size = null;
            try
            {
                var info = new FileInfo(path);
                if (info.Exists) size = info.Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
            }
            _homeFileSizes[path] = size;
            DispatchUi(() =>
            {
                if (_showHome) InvalidateChrome();
            });
        });
        return null;
    }

    private void LayoutHome(Win32.RECT client, int top, int bottom)
    {
        if (_historyQuery != 0)
        {
            Win32.ShowWindow(_historyQuery, _showHome ? Win32.SW_SHOW : Win32.SW_HIDE);
        }
        if (_historyList != 0)
        {
            Win32.ShowWindow(_historyList, Win32.SW_HIDE);
        }
        if (!_showHome || _historyQuery == 0) return;

        int maxW = S(900);
        int contentW = Math.Max(0, Math.Min(maxW, client.Width - S(36)));
        int left = Math.Max(S(18), (client.Width - contentW) / 2);
        int queryY = top + S(48);
        Win32.MoveWindow(_historyQuery, left, queryY, contentW, S(28), true);
    }

    private void PaintHome(nint hdc, Win32.RECT body)
    {
        var fillBody = body;
        Win32.FillRect(hdc, &fillBody, _panelBrush);
        if (body.Width <= 0 || body.Height <= 0) return;

        int maxW = S(900);
        int contentW = Math.Max(0, Math.Min(maxW, body.Width - S(36)));
        int left = Math.Max(S(18), (body.Width - contentW) / 2);
        int right = left + contentW;

        var titleRect = new Win32.RECT { Left = left, Top = body.Top + S(12), Right = right, Bottom = body.Top + S(42) };
        Win32.SelectObject(hdc, _uiFont);
        Win32.SetTextColor(hdc, ThemePalette.Text);
        Win32.DrawTextW(hdc, "История файлов", -1, &titleRect, Win32.DT_LEFT | Win32.DT_VCENTER | Win32.DT_SINGLELINE | Win32.DT_NOPREFIX);

        BuildHomeSections(out var recentRows, out var closedFileRows, out var draftRows, out long totalDraftBytes);
        _homeRows.Clear();

        int y = body.Top + S(86);
        int sectionGap = S(10);

        PaintSection(HomeSectionKind.Recent, "Недавние файлы", recentRows, LineNumbers.Format(recentRows.Length));
        PaintSection(HomeSectionKind.Closed, "Закрытые файлы", closedFileRows, LineNumbers.Format(closedFileRows.Length));
        string ghostSummary = $"{LineNumbers.Format(draftRows.Length)}  ·  Всего {FormatHistorySize(totalDraftBytes)} на диске";
        PaintSection(HomeSectionKind.Drafts, "Призрачные вкладки", draftRows, ghostSummary);

        void PaintSection(HomeSectionKind kind, string title, HomeRowEntry[] items, string summaryText)
        {
            if (y >= body.Bottom - S(16)) return;

            int headerH = S(30);
            var headerRect = new Win32.RECT { Left = left, Top = y, Right = right, Bottom = y + headerH };
            bool collapsed = _collapsedHomeSections.Contains(kind);
            bool headerHover = _hoverKind == HitKind.HomeSectionToggle && _hoverTabId == (ulong)kind;
            FillRoundRect(hdc, headerRect, S(5), headerHover ? ThemePalette.HoverBg : ThemePalette.Field, ThemePalette.Border);

            string chevron = collapsed ? "▸  " : "▾  ";
            var hTitleRect = new Win32.RECT { Left = left + S(10), Top = y, Right = right - S(10), Bottom = y + headerH };
            Win32.SelectObject(hdc, _uiFont);
            Win32.SetTextColor(hdc, ThemePalette.Text);
            Win32.DrawTextW(hdc, chevron + title, -1, &hTitleRect, Win32.DT_LEFT | Win32.DT_VCENTER | Win32.DT_SINGLELINE | Win32.DT_NOPREFIX);

            Win32.SelectObject(hdc, _smallFont);
            Win32.SetTextColor(hdc, ThemePalette.Muted);
            Win32.DrawTextW(hdc, summaryText, -1, &hTitleRect, Win32.DT_RIGHT | Win32.DT_VCENTER | Win32.DT_SINGLELINE | Win32.DT_NOPREFIX);
            _hits.Add(new HitRect(headerRect, HitKind.HomeSectionToggle, (ulong)kind));

            y += headerH + S(4);
            if (collapsed)
            {
                y += sectionGap;
                return;
            }

            int pageCount = Math.Max(1, (items.Length + HistoryPageSize - 1) / HistoryPageSize);
            int page = Math.Clamp(_homeSectionPages[kind], 0, pageCount - 1);
            _homeSectionPages[kind] = page;

            if (items.Length == 0)
            {
                if (y + S(24) <= body.Bottom)
                {
                    var emptyRect = new Win32.RECT { Left = left + S(12), Top = y, Right = right - S(12), Bottom = y + S(24) };
                    Win32.SelectObject(hdc, _smallFont);
                    Win32.SetTextColor(hdc, ThemePalette.Muted);
                    Win32.DrawTextW(hdc, "Список пуст", -1, &emptyRect, Win32.DT_LEFT | Win32.DT_VCENTER | Win32.DT_SINGLELINE | Win32.DT_NOPREFIX);
                }
                y += S(28) + sectionGap;
                return;
            }

            int rowH = S(28);
            int deleteW = S(26);
            int sizeW = S(78);
            int metaW = S(180);

            foreach (var entry in items.Skip(page * HistoryPageSize).Take(HistoryPageSize))
            {
                if (y + rowH > body.Bottom - S(8)) break;

                int rowIdx = _homeRows.Count;
                _homeRows.Add(entry);

                var rowRect = new Win32.RECT { Left = left, Top = y, Right = right - deleteW - S(4), Bottom = y + rowH };
                bool rowHover = _hoverKind == HitKind.HomeRowOpen && _hoverTabId == (ulong)rowIdx;
                if (rowHover)
                {
                    FillRoundRect(hdc, rowRect, S(4), ThemePalette.HoverBg, ThemePalette.Border);
                }

                int textRight = rowRect.Right - S(8);
                string sizeText = entry.Bytes is { } bytes ? FormatHistorySize(bytes) : "–";
                var sizeRect = new Win32.RECT { Left = textRight - sizeW, Top = y, Right = textRight, Bottom = y + rowH };
                Win32.SelectObject(hdc, _smallFont);
                Win32.SetTextColor(hdc, ThemePalette.Muted);
                Win32.DrawTextW(hdc, sizeText, -1, &sizeRect, Win32.DT_RIGHT | Win32.DT_VCENTER | Win32.DT_SINGLELINE | Win32.DT_NOPREFIX);
                textRight -= sizeW + S(8);

                string meta = HistoryMetadata(entry.Lines, entry.Worked).TrimStart(' ', '·');
                if (!string.IsNullOrEmpty(meta) && textRight - metaW > rowRect.Left + S(120))
                {
                    var metaRect = new Win32.RECT { Left = textRight - metaW, Top = y, Right = textRight, Bottom = y + rowH };
                    Win32.DrawTextW(hdc, meta, -1, &metaRect, Win32.DT_RIGHT | Win32.DT_VCENTER | Win32.DT_SINGLELINE | Win32.DT_END_ELLIPSIS | Win32.DT_NOPREFIX);
                    textRight -= metaW + S(8);
                }

                string mainLabel = entry.Subtitle is null ? entry.Title : $"{entry.Title}  ·  {entry.Subtitle}";
                var nameRect = new Win32.RECT { Left = rowRect.Left + S(10), Top = y, Right = Math.Max(rowRect.Left + S(20), textRight), Bottom = y + rowH };
                Win32.SelectObject(hdc, _uiFont);
                Win32.SetTextColor(hdc, ThemePalette.Text);
                Win32.DrawTextW(hdc, mainLabel, -1, &nameRect, Win32.DT_LEFT | Win32.DT_VCENTER | Win32.DT_SINGLELINE | Win32.DT_END_ELLIPSIS | Win32.DT_NOPREFIX);
                _hits.Add(new HitRect(rowRect, HitKind.HomeRowOpen, (ulong)rowIdx));

                DrawButtonFixed(hdc, right - deleteW, y + S(1), deleteW, rowH - S(2), "×", HitKind.HomeRowDelete, (ulong)rowIdx, false, false);
                y += rowH + S(2);
            }

            if (pageCount > 1 && y + S(28) <= body.Bottom)
            {
                int navH = S(24);
                int prevW = S(72);
                int nextW = S(72);
                int navX = right - prevW - nextW - S(86);
                DrawButtonFixed(hdc, navX, y, prevW, navH, "Назад", HitKind.HomeSectionPrev, (ulong)kind, page == 0, false);
                var pageRect = new Win32.RECT { Left = navX + prevW + S(6), Top = y, Right = right - nextW - S(6), Bottom = y + navH };
                Win32.SelectObject(hdc, _smallFont);
                Win32.SetTextColor(hdc, ThemePalette.Muted);
                Win32.DrawTextW(hdc, $"{page + 1} из {pageCount}", -1, &pageRect, Win32.DT_CENTER | Win32.DT_VCENTER | Win32.DT_SINGLELINE | Win32.DT_NOPREFIX);
                DrawButtonFixed(hdc, right - nextW, y, nextW, navH, "Дальше", HitKind.HomeSectionNext, (ulong)kind, page + 1 >= pageCount, false);
                y += navH + S(4);
            }

            y += sectionGap;
        }
    }

    private bool HandleHomeHit(HitRect hit)
    {
        switch (hit.Kind)
        {
            case HitKind.HomeSectionToggle:
            {
                var kind = (HomeSectionKind)hit.TabId;
                if (!_collapsedHomeSections.Remove(kind))
                {
                    _collapsedHomeSections.Add(kind);
                }
                InvalidateChrome();
                return true;
            }
            case HitKind.HomeSectionPrev:
            {
                var kind = (HomeSectionKind)hit.TabId;
                if (_homeSectionPages.TryGetValue(kind, out int page) && page > 0)
                {
                    _homeSectionPages[kind] = page - 1;
                    InvalidateChrome();
                }
                return true;
            }
            case HitKind.HomeSectionNext:
            {
                var kind = (HomeSectionKind)hit.TabId;
                _homeSectionPages[kind] = _homeSectionPages.GetValueOrDefault(kind) + 1;
                InvalidateChrome();
                return true;
            }
            case HitKind.HomeRowOpen:
            {
                int idx = (int)hit.TabId;
                if ((uint)idx >= (uint)_homeRows.Count) return true;
                var row = _homeRows[idx];
                if (row.File is { } file)
                {
                    OpenPaths([file.Path], file.Windows1251, file.ViewAsLog);
                }
                else
                {
                    ReopenClosedTab(row.ClosedTabId);
                }
                return true;
            }
            case HitKind.HomeRowDelete:
            {
                int idx = (int)hit.TabId;
                if ((uint)idx >= (uint)_homeRows.Count) return true;
                var row = _homeRows[idx];
                if (row.Section == HomeSectionKind.Recent && row.File is { } file)
                {
                    try { _recent.Remove(file.Path); }
                    catch (Exception error) { _error = RecentWarning(error); }
                    UpdateHistory();
                    return true;
                }

                if (row.HasSavedDraft && row.Section == HomeSectionKind.Drafts)
                {
                    int answer = Win32.MessageBoxW(
                        _hwnd,
                        $"Сохранённый текст «{row.Title}» будет удалён.",
                        "Удалить черновик?",
                        Win32.MB_YESNO | Win32.MB_ICONWARNING);
                    if (answer != Win32.IDYES) return true;
                }

                _closed.RemoveAll(item => item.Tab.Id == row.ClosedTabId);
                QueueSessionTick();
                UpdateHistory();
                return true;
            }
            default:
                return false;
        }
    }

    internal static string FormatHistorySize(long bytes)
    {
        string[] units = ["Б", "КиБ", "МиБ", "ГиБ", "ТиБ", "ПиБ"];
        double value = Math.Max(0, bytes);
        int unit = 0;
        while (value >= 1024 && unit + 1 < units.Length)
        {
            value /= 1024;
            unit++;
        }
        return $"{value.ToString("0.#", CultureInfo.GetCultureInfo("ru-RU"))} {units[unit]}";
    }

    private static ulong? KnownLineCount(Document document) => document.Log is { } log
        ? (log.Index.ScannedTo >= log.Bookmark.Size ? log.Index.LineCount : null)
        : document.Placeholder ? document.StoredLineCount : (ulong)ScintillaInterop.GetLineCount(document.EditorHwnd);

    private static DateTimeOffset? LastWorked(RecentFile file) => file.Position?.LastWorked is { } worked &&
        (file.OpenedAt is null || worked > file.OpenedAt) ? worked : file.OpenedAt;

    internal static string HistoryMetadata(ulong? lines, DateTimeOffset? when, DateTimeOffset? now = null)
    {
        string result = lines is { } count ? $" · {count:N0} стр." : "";
        if (when is not { } last) return result;
        TimeSpan age = (now ?? DateTimeOffset.UtcNow) - last;
        string time = age.TotalMinutes < 1 ? "только что" :
            age.TotalHours < 1 ? $"{(int)age.TotalMinutes} мин назад" :
            age.TotalDays < 1 ? $"{(int)age.TotalHours} ч назад" :
            last.LocalDateTime.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);
        return result + " · " + time;
    }

    private void SaveRecentPositions()
    {
        var positions = new List<RecentFile>();
        foreach (var doc in _documents)
        {
            if (doc.File is null) continue;
            var (position, _) = SnapshotDocument(doc, includeDraft: false);
            positions.Add(RecentFile.From(doc.File.Path, doc.Format.Encoding, doc.Log is not null) with
            {
                Position = position,
                Lines = position.LineCount,
            });
        }
        try { _recent.RecordPositions(positions); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private void UpdateUntitledName(Document doc)
    {
        if (doc.File is null && doc.Log is null && string.IsNullOrWhiteSpace(doc.CustomTitle))
        {
            doc.Name = _settings.UntitledName(ScintillaInterop.LineBytes(doc.EditorHwnd, 0));
        }
    }
}
