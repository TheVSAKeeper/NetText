using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using NetText.Core;
using NetText.Native;

namespace NetText.UI;

internal sealed unsafe class MainWindow
{
    private const int ToolbarLogicalHeight = 46;
    private const int TabBarLogicalHeight = 43;
    private const int StatusLogicalHeight = 31;
    private const int EditorSearchLogicalHeight = 76;
    private const int LogToolbarLogicalHeight = 112;
    private const int LogSearchLogicalHeight = 152;
    private const float MinFontSize = 9.0f;
    private const float MaxFontSize = 35.0f;
    private const int MaxClosedTabs = SessionJournal.MaxClosedTabs;

    private const nuint FastTimerId = 1;
    private const nuint SlowTimerId = 2;

    private const int IdSearchQueryEdit = 2001;
    private const int IdSearchReplaceEdit = 2002;
    private const int IdLogJumpEdit = 2003;
    private const int IdLogTrimEdit = 2004;
    private const int IdLogSearchEdit = 2005;
    private const int IdLogSearchList = 2006;

    private enum HitKind
    {
        None = 0,
        BtnNew = 1,
        BtnOpen = 2,
        BtnRecent = 3,
        BtnLogMenu = 4,
        BtnOpen1251 = 5,
        BtnSave = 6,
        BtnSaveAs = 7,
        BtnFindText = 8,
        BtnFormatJson = 9,
        BtnWrap = 10,
        BtnZoomOut = 11,
        BtnZoomIn = 12,
        TabSelect = 13,
        TabClose = 14,
        BtnReopen = 15,
        BtnGoToLine = 16,
        SearchPrev = 17,
        SearchNext = 18,
        SearchRegex = 19,
        SearchCase = 20,
        SearchWhole = 21,
        SearchAllTabs = 22,
        SearchClose = 23,
        SearchReplaceOne = 24,
        SearchReplaceAll = 25,
        LogStart = 26,
        LogPrev = 27,
        LogNext = 28,
        LogEnd = 29,
        LogJumpPercent = 30,
        LogSearchOpen = 31,
        LogAutoRefresh = 32,
        LogFollowEnd = 33,
        LogRefreshNow = 34,
        LogIndexToggle = 35,
        LogHideStart = 36,
        LogPhysicalTrim = 37,
        LogRevealAll = 38,
        LogCancelTrim = 39,
        LogFindNew = 40,
        LogFindMore = 41,
        LogFindCancel = 42,
        LogFindClose = 43,
        LogFindRegex = 44,
        LogFindCase = 45,
        LogFindWhole = 46,
        BtnNewTab = 47,
    }

    private readonly record struct HitRect(Win32.RECT Rect, HitKind Kind, ulong TabId = 0);

    private sealed class Document
    {
        public ulong Id;
        public string Name = "Без имени";
        public FileTarget? File;
        public TextFormat Format = TextFormat.Default;
        public nint EditorHwnd;
        public bool Dirty;
        public bool Placeholder;
        public bool PendingEditorReady;
        public DiskStatusKind DiskStatus = DiskStatusKind.Current;
        public int CursorLine;
        public int CursorColumn;
        public ulong Revision;
        public ulong? DraftRevision;
        public LogViewerState? Log;
        public LogViewport? Viewport;
    }

    private static MainWindow? _instance;

    private readonly List<Document> _documents = [];
    private readonly List<(SessionTab Tab, byte[]? DraftBytes)> _closed = [];
    private readonly List<HitRect> _hits = [];
    private ulong _lastSyncedHitLogId;
    private int _lastSyncedHitCount = -1;
    private readonly ConcurrentQueue<Action> _uiQueue = new();
    private readonly SingleInstance? _singleInstance;
    private readonly SessionWriter _sessionWriter = new();
    private readonly GdiBackBuffer _topChromeBuffer = new();
    private readonly GdiBackBuffer _statusBuffer = new();
    private SessionJournal? _sessionJournal;

    private RecentFiles _recent;
    private ulong _activeId;
    private ulong _nextId = 1;
    private string? _busy;
    private string? _error;
    private bool _wrap;
    private float _fontSize = 15.0f;
    private int _tabScrollX;
    private int _fastTicks;
    private bool _diskCheckInFlight;
    private bool _sessionShutdownDone;
    private bool _closingForSession;
    private int _e2eStage;
    private bool _e2eFirstPainted;
    private bool _e2eAwaitPaint;
    private long _e2eStageTicks;
    private ulong _e2eLogDocId;

    private bool _searchOpen;
    private string _searchQuery = "";
    private string _searchReplacement = "";
    private bool _searchRegex;
    private bool _searchCase;
    private bool _searchWhole;
    private bool _searchAllTabs;
    private string _searchStatus = "Введите текст поиска";
    private (SearchQuery Query, SearchHit Hit)? _lastSearchHit;
    private long _searchGen;
    private bool _searchInFlight;

    private nint _hwnd;
    private nint _hInstance;
    private uint _dpi = 96;
    private nint _uiFont;
    private nint _smallFont;
    private nint _bgBrush;
    private nint _panelBrush;
    private nint _tabBarBrush;

    private nint _searchQueryEdit;
    private nint _searchReplaceEdit;
    private nint _logJumpEdit;
    private nint _logTrimEdit;
    private nint _logSearchEdit;
    private nint _logSearchList;
    private bool _suppressEditEvents;

    private HitKind _hoverKind;
    private ulong _hoverTabId;
    private bool _trackingMouse;
    private ulong _dragTabId;
    private int _dragStartX;
    private bool _draggingTab;

    public MainWindow(string[] startupPaths, SingleInstance? singleInstance)
    {
        _singleInstance = singleInstance;
        var (recent, recentError) = RecentFiles.Load();
        _recent = recent;
        if (recentError is not null)
        {
            _error = RecentWarning(recentError);
        }

        RecoveredSession? recovered = null;
        try
        {
            _sessionJournal = SessionJournal.Open();
            recovered = _sessionJournal.Restore();
            if (recovered?.Warning is not null)
            {
                _error ??= recovered.Warning;
            }
        }
        catch (Exception ex)
        {
            _error ??= $"Сохранение сессии недоступно. {ex.Message}";
        }

        _instance = this;
        _hInstance = Win32.GetModuleHandleW(null);
        CreateWindowAndShow(startupPaths, recovered);
    }

    public nint Hwnd => _hwnd;

    public bool TryHandleKeyMessage(nint targetHwnd, int vk)
    {
        if (targetHwnd == _searchQueryEdit)
        {
            if (vk == Win32.VK_RETURN)
            {
                RunEditorSearch(Win32.IsKeyDown(Win32.VK_SHIFT) ? SearchCommand.Previous : SearchCommand.Next);
                return true;
            }
            if (vk == Win32.VK_ESCAPE)
            {
                CloseEditorSearch();
                return true;
            }
        }
        else if (targetHwnd == _searchReplaceEdit)
        {
            if (vk == Win32.VK_RETURN)
            {
                RunEditorSearch(SearchCommand.Replace);
                return true;
            }
            if (vk == Win32.VK_ESCAPE)
            {
                CloseEditorSearch();
                return true;
            }
        }
        else if (targetHwnd == _logJumpEdit && vk == Win32.VK_RETURN)
        {
            ActiveDocument.Log?.JumpPercent();
            return true;
        }
        else if (targetHwnd == _logSearchEdit)
        {
            if (vk == Win32.VK_RETURN)
            {
                ActiveDocument.Log?.StartSearch(false);
                return true;
            }
            if (vk == Win32.VK_ESCAPE && ActiveDocument.Log is { } log)
            {
                log.SearchOpen = false;
                LayoutAndSyncControls();
                FocusActiveEditor();
                return true;
            }
        }

        bool ctrl = Win32.IsKeyDown(Win32.VK_CONTROL);
        bool shift = Win32.IsKeyDown(Win32.VK_SHIFT);

        if (_documents.Count > 0 && ActiveDocument.Log is { } activeLog)
        {
            if (ctrl && vk == Win32.VK_HOME)
            {
                activeLog.Navigate(LogDestination.Start);
                return true;
            }
            if (ctrl && vk == Win32.VK_END)
            {
                activeLog.Navigate(LogDestination.End);
                return true;
            }
            if (!ctrl && vk == Win32.VK_PRIOR)
            {
                ActiveDocument.Viewport?.ScrollScreen(false);
                return true;
            }
            if (!ctrl && vk == Win32.VK_NEXT)
            {
                ActiveDocument.Viewport?.ScrollScreen(true);
                return true;
            }
        }

        if (!ctrl)
        {
            if (vk == Win32.VK_F3)
            {
                RunEditorSearch(shift ? SearchCommand.Previous : SearchCommand.Next);
                return true;
            }
            return false;
        }

        switch (vk)
        {
            case 'N' when !shift:
                NewFile();
                return true;
            case 'O' when !shift:
                OpenPicker(false, false);
                return true;
            case 'S' when !shift:
                Save(false);
                return true;
            case 'S' when shift:
                Save(true);
                return true;
            case 'W' when !shift:
                CloseTab(_activeId);
                return true;
            case 'T' when shift:
                Reopen();
                return true;
            case 'G' when !shift:
                GoToLine();
                return true;
            case 'J' when shift:
                FormatJson();
                return true;
            case 'F' when !shift:
                OpenSearch(false);
                return true;
            case 'H' when !shift:
                OpenSearch(true);
                return true;
            case Win32.VK_OEM_PLUS:
            case Win32.VK_ADD:
                Zoom(1.0f);
                return true;
            case Win32.VK_OEM_MINUS:
            case Win32.VK_SUBTRACT:
                Zoom(-1.0f);
                return true;
            default:
                return false;
        }
    }

    private void CreateWindowAndShow(string[] startupPaths, RecoveredSession? recovered)
    {
        _bgBrush = Win32.CreateSolidBrush(ThemePalette.Bg);
        _panelBrush = Win32.CreateSolidBrush(ThemePalette.Panel);
        _tabBarBrush = Win32.CreateSolidBrush(ThemePalette.TabBar);

        fixed (char* className = "NetTextMainWindow")
        {
            var wc = new Win32.WNDCLASSEXW
            {
                cbSize = (uint)sizeof(Win32.WNDCLASSEXW),
                style = Win32.CS_HREDRAW | Win32.CS_VREDRAW | Win32.CS_DBLCLKS,
                lpfnWndProc = &MainWndProc,
                hInstance = _hInstance,
                hCursor = Win32.LoadCursorW(0, Win32.IDC_ARROW),
                hbrBackground = _bgBrush,
                lpszClassName = className,
            };
            Win32.RegisterClassExW(&wc);
        }

        int initW = recovered is not null ? (int)MathF.Round(Math.Max(760, recovered.State.Window.Width)) : 1080;
        int initH = recovered is not null ? (int)MathF.Round(Math.Max(350, recovered.State.Window.Height)) : 700;
        if (recovered is not null)
        {
            _wrap = recovered.State.Wrap;
            _fontSize = Math.Clamp(recovered.State.FontSize, MinFontSize, MaxFontSize);
        }

        int screenW = Win32.GetSystemMetrics(Win32.SM_CXSCREEN);
        int screenH = Win32.GetSystemMetrics(Win32.SM_CYSCREEN);
        int initX = Math.Max(0, (screenW - initW) / 2);
        int initY = Math.Max(0, (screenH - initH) / 2);

        _hwnd = Win32.CreateWindowExW(
            0,
            "NetTextMainWindow",
            "NetText",
            Win32.WS_OVERLAPPEDWINDOW | Win32.WS_CLIPCHILDREN,
            initX, initY, initW, initH,
            0, 0, _hInstance, null);

        if (_hwnd == 0)
        {
            throw new InvalidOperationException("Не удалось открыть окно NetText");
        }

        _dpi = Win32.GetDpiForWindow(_hwnd);
        if (_dpi == 0) _dpi = 96;

        int scaledW = S(initW);
        int scaledH = S(initH);
        Win32.MoveWindow(_hwnd, Math.Max(0, (screenW - scaledW) / 2), Math.Max(0, (screenH - scaledH) / 2), scaledW, scaledH, false);

        Win32.EnableDarkTitleBar(_hwnd);
        Win32.DragAcceptFiles(_hwnd, true);
        RebuildFonts();
        CreateChildInputControls();

        if (recovered is not null)
        {
            RestoreSession(recovered);
        }
        else if (startupPaths.Length == 0)
        {
            AddDocument(null);
        }

        if (startupPaths.Length > 0)
        {
            Measurements.DumpMemoryProfile("before-open-paths");
            OpenPaths(startupPaths, false, false);
        }

        Win32.ShowWindow(_hwnd, recovered?.State.Window.Maximized == true ? (int)Win32.SW_MAXIMIZE : Win32.SW_SHOW);
        Measurements.RecordWindowVisible();
        Win32.UpdateWindow(_hwnd);
        LayoutAndSyncControls();
        FocusActiveEditor();

        _singleInstance?.SetWakeupCallback(() => Win32.PostMessageW(_hwnd, Win32.WM_APP_IPC, 0, 0));
        Win32.SetTimer(_hwnd, FastTimerId, 125, 0);
        Win32.SetTimer(_hwnd, SlowTimerId, 1000, 0);
    }

    private void CreateChildInputControls()
    {
        _searchQueryEdit = CreateEdit(IdSearchQueryEdit, "Текст поиска");
        _searchReplaceEdit = CreateEdit(IdSearchReplaceEdit, "Замена");
        _logJumpEdit = CreateEdit(IdLogJumpEdit, "0–100");
        _logTrimEdit = CreateEdit(IdLogTrimEdit, "50");
        Win32.SetWindowTextW(_logTrimEdit, "50");
        _logSearchEdit = CreateEdit(IdLogSearchEdit, "Текст в логе");

        _logSearchList = Win32.CreateWindowExW(
            0,
            "LISTBOX",
            "",
            Win32.WS_CHILD | Win32.WS_BORDER | Win32.WS_VSCROLL | Win32.LBS_NOTIFY | Win32.LBS_NOINTEGRALHEIGHT,
            0, 0, 100, 80,
            _hwnd,
            IdLogSearchList,
            _hInstance,
            null);
        Win32.SendMessageW(_logSearchList, Win32.WM_SETFONT, (nuint)_smallFont, 1);
    }

    private nint CreateEdit(int id, string placeholder)
    {
        var edit = Win32.CreateWindowExW(
            0,
            "EDIT",
            "",
            Win32.WS_CHILD | Win32.WS_BORDER | Win32.WS_TABSTOP | Win32.ES_AUTOHSCROLL,
            0, 0, 100, 24,
            _hwnd,
            id,
            _hInstance,
            null);
        Win32.SendMessageW(edit, Win32.WM_SETFONT, (nuint)_uiFont, 1);
        Win32.SendMessageW(edit, Win32.EM_SETCUEBANNER, 1, placeholder);
        return edit;
    }

    private float Scale => _dpi / 96.0f;
    private int S(int px) => (int)MathF.Round(px * Scale);

    private void RebuildFonts()
    {
        if (_uiFont != 0) Win32.DeleteObject(_uiFont);
        if (_smallFont != 0) Win32.DeleteObject(_smallFont);

        _uiFont = Win32.CreateFontW(
            -S(14), 0, 0, 0, Win32.FW_NORMAL, 0, 0, 0,
            Win32.DEFAULT_CHARSET, 0, 0, Win32.CLEARTYPE_QUALITY, Win32.DEFAULT_PITCH, "Segoe UI");
        _smallFont = Win32.CreateFontW(
            -S(12), 0, 0, 0, Win32.FW_NORMAL, 0, 0, 0,
            Win32.DEFAULT_CHARSET, 0, 0, Win32.CLEARTYPE_QUALITY, Win32.DEFAULT_PITCH, "Segoe UI");

        foreach (var h in new[] { _searchQueryEdit, _searchReplaceEdit, _logJumpEdit, _logTrimEdit, _logSearchEdit })
        {
            if (h != 0) Win32.SendMessageW(h, Win32.WM_SETFONT, (nuint)_uiFont, 1);
        }
        if (_logSearchList != 0) Win32.SendMessageW(_logSearchList, Win32.WM_SETFONT, (nuint)_smallFont, 1);
    }

    private Document ActiveDocument =>
        _documents.Find(d => d.Id == _activeId) ?? throw new InvalidOperationException("active tab must exist");

    private void AddDocument(NativeOpenedFile? opened)
    {
        ulong id = _nextId++;
        var target = opened?.Target;
        var format = opened?.Format ?? TextFormat.Default;
        var name = target is null ? "Без имени" : FileName(target.Path);

        var editorHwnd = ScintillaInterop.CreateEditorControl(_hwnd, _hInstance, (int)MathF.Round(_fontSize), _wrap);
        if (opened is not null)
        {
            ScintillaInterop.SetTextUtf8(editorHwnd, opened.Span);
        }
        ScintillaInterop.ApplyLanguage(editorHwnd, target?.Path, (int)MathF.Round(_fontSize));

        var doc = new Document
        {
            Id = id,
            Name = name,
            File = target,
            Format = format,
            EditorHwnd = editorHwnd,
            PendingEditorReady = opened is not null,
            Dirty = false,
        };
        _documents.Add(doc);
        SelectTab(id);
        if (doc.PendingEditorReady)
        {
            Win32.InvalidateRect(editorHwnd, null, false);
        }
    }

    private void AddLogDocument(FileTarget target, bool windows1251, LogBookmark? restoredBookmark, bool restored)
    {
        ulong id = _nextId++;
        LogViewerState? state = null;
        var viewport = new LogViewport(
            _hwnd,
            _hInstance,
            (int)_dpi,
            _fontSize,
            _wrap,
            nav => state?.Navigate(nav),
            delta => Zoom(delta));
        var editorHwnd = viewport.Hwnd;

        state = new LogViewerState(
            target.Path,
            windows1251,
            restoredBookmark,
            DispatchUi,
            () =>
            {
                if (state is not null)
                {
                    viewport.SetSearchHighlight(state.SearchOpen ? state.HighlightRegex : null);
                }
                if (_documents.Count > 0 && ActiveDocument.Id == id)
                {
                    SyncLogInputsAndHits();
                    LayoutAndSyncControls();
                    InvalidateChrome();
                }
                if (Measurements.IsE2EEnabled && _e2eLogDocId == id)
                {
                    AdvanceE2EProbe();
                }
            },
            (utf8, scrollToBottom) =>
            {
                viewport.SetUtf8Text(utf8, scrollToBottom);
            },
            () =>
            {
                Win32.RECT rc;
                Win32.GetClientRect(_hwnd, &rc);
                return Math.Max(300, rc.Height);
            },
            (pct, rem, orig) =>
            {
                string detail = $"Запрошено {pct}%. Из «{FileName(target.Path)}» будет удалено {rem} байт из {orig}. Перед записью NetText сохранит полную резервную копию рядом с логом.";
                return Win32.MessageBoxW(_hwnd, detail, "Удалить начало исходного файла?", Win32.MB_YESNO | Win32.MB_ICONWARNING) == Win32.IDYES;
            });

        var doc = new Document
        {
            Id = id,
            Name = FileName(target.Path),
            File = target,
            Format = new TextFormat(windows1251 ? TextEncoding.Windows1251 : TextEncoding.Utf8, LineEndings.Lf),
            EditorHwnd = editorHwnd,
            Log = state,
            Viewport = viewport,
        };
        _documents.Add(doc);
        if (Measurements.IsE2EEnabled && _e2eLogDocId == 0)
        {
            _e2eLogDocId = id;
            _e2eStage = 1;
            viewport.SetAfterPaint(() => OnE2ELogPainted(id));
        }
        SelectTab(id);
        state.StartInitialLoad(restored);
    }

    private void SelectTab(ulong id)
    {
        var target = _documents.Find(d => d.Id == id);
        if (target is null)
        {
            return;
        }

        _activeId = id;
        if (target.Log is not null)
        {
            _searchOpen = false;
        }

        foreach (var doc in _documents)
        {
            Win32.ShowWindow(doc.EditorHwnd, doc.Id == id ? Win32.SW_SHOW : Win32.SW_HIDE);
        }

        EnsureTabVisible(id);
        SyncLogInputsAndHits();
        LayoutAndSyncControls();
        FocusActiveEditor();
        InvalidateChrome();
    }

    private void FocusActiveEditor()
    {
        if (_documents.Count > 0)
        {
            Win32.SetFocus(ActiveDocument.EditorHwnd);
        }
    }

    private void NewFile()
    {
        if (_busy is not null)
        {
            return;
        }
        _error = null;
        AddDocument(null);
    }

    private void CloseTab(ulong id)
    {
        if (_busy is not null)
        {
            return;
        }

        var doc = _documents.Find(d => d.Id == id);
        if (doc is null || doc.Log?.IsWritingFile == true)
        {
            return;
        }

        if (doc.Dirty && (doc.File is not null || _sessionJournal is null) && !ConfirmDiscard(doc))
        {
            return;
        }

        RemoveTab(id);
    }

    private void RemoveTab(ulong id)
    {
        int index = _documents.FindIndex(d => d.Id == id);
        if (index < 0)
        {
            return;
        }

        var doc = _documents[index];
        var (tab, draftBytes) = SnapshotDocument(doc, includeDraft: doc.File is null);

        _documents.RemoveAt(index);
        doc.Log?.Dispose();
        Win32.DestroyWindow(doc.EditorHwnd);

        if (_closed.Count >= MaxClosedTabs)
        {
            _closed.RemoveAt(0);
        }
        _closed.Add((tab, draftBytes));

        if (_documents.Count == 0)
        {
            AddDocument(null);
        }
        else if (id == _activeId)
        {
            ulong nextId = _documents[Math.Min(index, _documents.Count - 1)].Id;
            SelectTab(nextId);
        }
        else
        {
            InvalidateChrome();
        }
    }

    private void Reopen()
    {
        if (_closed.Count > 0)
        {
            ReopenClosedTab(_closed[^1].Tab.Id);
        }
    }

    private void ReopenClosedTab(ulong id)
    {
        if (_busy is not null)
        {
            return;
        }

        int index = _closed.FindIndex(c => c.Tab.Id == id);
        if (index < 0)
        {
            return;
        }

        var (tab, draftBytes) = _closed[index];
        if (tab.File is { } file)
        {
            bool win1251 = string.Equals(tab.Format.Encoding, nameof(TextEncoding.Windows1251), StringComparison.OrdinalIgnoreCase);
            OpenPaths([file.Path], win1251, tab.Log is not null, tab);
            return;
        }

        _closed.RemoveAt(index);
        string? draft = draftBytes is null ? null : Encoding.UTF8.GetString(draftBytes);
        ulong newId = RestoreTextTab(tab, draft);
        SelectTab(newId);
    }

    private static string ClosedTabLabel(SessionTab tab, byte[]? draftBytes)
    {
        if (tab.File is { } file)
        {
            return DisplayPath(file.Path);
        }

        if (draftBytes is null || draftBytes.Length == 0)
        {
            return "Без имени";
        }

        string text = Encoding.UTF8.GetString(draftBytes);
        var sb = new StringBuilder(60);
        foreach (char ch in text)
        {
            if (sb.Length >= 60) break;
            sb.Append(char.IsWhiteSpace(ch) ? ' ' : ch);
        }
        string preview = sb.ToString().Trim();
        return preview.Length == 0 ? "Без имени" : $"Без имени – {preview}";
    }

    private bool ConfirmDiscard(Document? doc)
    {
        string detail = doc is null
            ? "В открытых вкладках есть несохранённый текст."
            : $"Вкладка «{doc.Name}» содержит несохранённый текст.";
        return Win32.MessageBoxW(_hwnd, detail, "Закрыть без сохранения?", Win32.MB_YESNO | Win32.MB_ICONWARNING) == Win32.IDYES;
    }

    private void ReorderTabs(ulong draggedId, ulong targetId)
    {
        int from = _documents.FindIndex(d => d.Id == draggedId);
        int to = _documents.FindIndex(d => d.Id == targetId);
        if (from >= 0 && to >= 0 && from != to)
        {
            var tab = _documents[from];
            _documents.RemoveAt(from);
            _documents.Insert(to, tab);
            InvalidateChrome();
        }
    }

    private void ToggleWrap()
    {
        _wrap = !_wrap;
        foreach (var doc in _documents)
        {
            if (doc.Viewport is { } vp) vp.SetWrap(_wrap);
            else ScintillaInterop.SetWrap(doc.EditorHwnd, _wrap);
        }
        InvalidateChrome();
    }

    private void Zoom(float delta)
    {
        float next = Math.Clamp(_fontSize + delta, MinFontSize, MaxFontSize);
        if (Math.Abs(next - _fontSize) < 0.01f)
        {
            return;
        }

        _fontSize = next;
        int pt = (int)MathF.Round(_fontSize);
        foreach (var doc in _documents)
        {
            if (doc.Log is null)
            {
                ScintillaInterop.ApplyLanguage(doc.EditorHwnd, doc.File?.Path, pt);
            }
            else
            {
                doc.Viewport?.SetFontSize(_fontSize);
            }
        }
    }

    private void GoToLine()
    {
        if (_busy is not null || ActiveDocument.Log is not null)
        {
            return;
        }

        var doc = ActiveDocument;
        int lineCount = ScintillaInterop.GetLineCount(doc.EditorHwnd);
        var line = GoToLineDialog.Show(_hwnd, lineCount);
        if (line is int zeroBased)
        {
            ScintillaInterop.GoToLine(doc.EditorHwnd, zeroBased);
            UpdateActiveCursor();
            FocusActiveEditor();
        }
    }

    private void FormatJson()
    {
        if (_busy is not null) return;
        var doc = ActiveDocument;
        if (doc.Placeholder || doc.Log is not null) return;

        var (selStart, selEnd, _) = ScintillaInterop.GetSelection(doc.EditorHwnd);
        int totalLen = ScintillaInterop.GetByteLength(doc.EditorHwnd);
        int rangeStart = selEnd > selStart ? selStart : 0;
        int rangeEnd = selEnd > selStart ? selEnd : totalLen;
        ReadOnlySpan<byte> newline = doc.Format.LineEndingsLabel == "CRLF" ? "\r\n"u8 : "\n"u8;

        _error = null;
        if (!ScintillaInterop.TryFormatDocumentJsonUtf8(doc.EditorHwnd, rangeStart, rangeEnd, newline, out bool changed, out var err))
        {
            _error = err;
            InvalidateChrome();
            return;
        }

        if (changed)
        {
            doc.Dirty = true;
            doc.Revision++;
        }

        FocusActiveEditor();
        InvalidateChrome();
    }

    private void OpenSearch(bool replace)
    {
        var doc = ActiveDocument;
        if (doc.Log is { } log)
        {
            log.SearchOpen = true;
            LayoutAndSyncControls();
            Win32.SetFocus(_logSearchEdit);
            Win32.SendMessageW(_logSearchEdit, Win32.EM_SETSEL, 0, -1);
            InvalidateChrome();
            return;
        }

        _searchOpen = true;
        LayoutAndSyncControls();
        var focusTarget = replace && !_searchAllTabs ? _searchReplaceEdit : _searchQueryEdit;
        Win32.SetFocus(focusTarget);
        Win32.SendMessageW(focusTarget, Win32.EM_SETSEL, 0, -1);
        InvalidateChrome();
    }

    private void CloseEditorSearch()
    {
        _searchOpen = false;
        _searchGen++;
        _searchInFlight = false;
        _lastSearchHit = null;
        LayoutAndSyncControls();
        FocusActiveEditor();
        InvalidateChrome();
    }

    private void ResetEditorSearch()
    {
        _searchGen++;
        _searchInFlight = false;
        _lastSearchHit = null;
        _searchStatus = _searchAllTabs
            ? "Поиск по всем вкладкам. Замена доступна в текущей вкладке."
            : "Enter – найти следующее совпадение";
        InvalidateChrome();
    }

    private void RunEditorSearch(SearchCommand command)
    {
        if (_documents.Count == 0) return;
        var active = ActiveDocument;
        if (active.Log is not null)
        {
            return;
        }

        if ((command is SearchCommand.Replace or SearchCommand.ReplaceAll) &&
            (_busy is not null || _searchInFlight || _searchAllTabs || active.Placeholder))
        {
            return;
        }

        if (!_searchOpen && command is SearchCommand.Next or SearchCommand.Previous)
        {
            OpenSearch(false);
        }

        var query = new SearchQuery(_searchQuery, _searchRegex, _searchCase, _searchWhole, _searchAllTabs);
        var (selStart, selEnd, _) = ScintillaInterop.GetSelection(active.EditorHwnd);
        var origin = new SearchHit(active.Id, new ByteRange(selStart, selEnd));
        bool skipCurrent = _lastSearchHit is { Query: var lq, Hit: var lh } && lq == query && lh == origin;
        var replacement = _searchReplacement;

        var snapshots = new List<(ulong Id, ulong Rev, SearchUtf8Source Src)>();
        foreach (var d in _documents)
        {
            if (d.Log is null && (query.AllTabs || d.Id == active.Id))
            {
                var (buf, len) = ScintillaInterop.CopyUtf8BytesPooled(d.EditorHwnd);
                snapshots.Add((d.Id, d.Revision, new SearchUtf8Source(d.Id, buf, len)));
            }
        }

        long gen = ++_searchGen;
        _searchInFlight = true;
        _searchStatus = "Поиск…";
        InvalidateChrome();

        ThreadPool.QueueUserWorkItem(_ =>
        {
            bool ok;
            SearchOutcome outcome;
            string error;
            try
            {
                var srcList = new List<SearchUtf8Source>(snapshots.Count);
                for (int i = 0; i < snapshots.Count; i++)
                {
                    srcList.Add(snapshots[i].Src);
                }

                ok = EditorSearch.TryRunUtf8(
                    query,
                    command,
                    srcList,
                    origin,
                    skipCurrent,
                    replacement,
                    out outcome,
                    out error);
            }
            finally
            {
                foreach (var snap in snapshots)
                {
                    if (snap.Src.Buffer.Length > 0)
                    {
                        System.Buffers.ArrayPool<byte>.Shared.Return(snap.Src.Buffer);
                    }
                }
            }

            DispatchUi(() =>
            {
                if (gen != _searchGen)
                {
                    if (outcome is SearchOutcome.ReplacedUtf8 { FromPool: true, Buffer: var staleBuf })
                    {
                        System.Buffers.ArrayPool<byte>.Shared.Return(staleBuf);
                    }
                    return;
                }
                _searchInFlight = false;

                bool stale = _busy is not null || ActiveDocument.Id != active.Id;
                if (!stale)
                {
                    foreach (var snap in snapshots)
                    {
                        var cur = _documents.Find(d => d.Id == snap.Id);
                        if (cur is null || cur.Revision != snap.Rev)
                        {
                            stale = true;
                            break;
                        }
                    }
                }

                if (stale)
                {
                    if (outcome is SearchOutcome.ReplacedUtf8 { FromPool: true, Buffer: var staleBuf })
                    {
                        System.Buffers.ArrayPool<byte>.Shared.Return(staleBuf);
                    }
                    _searchStatus = "Текст или вкладка изменились. Повторите поиск.";
                    InvalidateChrome();
                    return;
                }

                if (!ok)
                {
                    _searchStatus = error;
                    InvalidateChrome();
                    return;
                }

                switch (outcome)
                {
                    case SearchOutcome.Found found:
                        if (found.Hit is SearchHit hit)
                        {
                            SelectTab(hit.Tab);
                            var targetDoc = _documents.Find(d => d.Id == hit.Tab);
                            if (targetDoc is not null)
                            {
                                ScintillaInterop.SetSelection(targetDoc.EditorHwnd, hit.Range.Start, hit.Range.End, false);
                                UpdateActiveCursor();
                            }
                            _lastSearchHit = (query, hit);
                            _searchStatus = $"Совпадений: {found.Total}";
                        }
                        else
                        {
                            _lastSearchHit = null;
                            _searchStatus = "Совпадений нет";
                        }
                        break;

                    case SearchOutcome.Replaced rep:
                        ScintillaInterop.ReplaceByteRange(active.EditorHwnd, rep.Range.Start, rep.Range.End, rep.Text);
                        active.Dirty = true;
                        active.Revision++;
                        _lastSearchHit = null;
                        _searchStatus = $"Заменено: {rep.Count}";
                        FocusActiveEditor();
                        break;

                    case SearchOutcome.ReplacedUtf8 repUtf8:
                        try
                        {
                            ScintillaInterop.ReplaceByteRangeUtf8(
                                active.EditorHwnd,
                                repUtf8.Range.Start,
                                repUtf8.Range.End,
                                repUtf8.Buffer.AsSpan(0, repUtf8.Length));
                        }
                        finally
                        {
                            if (repUtf8.FromPool && repUtf8.Buffer.Length > 0)
                            {
                                System.Buffers.ArrayPool<byte>.Shared.Return(repUtf8.Buffer);
                            }
                        }
                        active.Dirty = true;
                        active.Revision++;
                        _lastSearchHit = null;
                        _searchStatus = $"Заменено: {repUtf8.Count}";
                        FocusActiveEditor();
                        break;

                    case SearchOutcome.NoReplacements:
                        _lastSearchHit = null;
                        _searchStatus = "Совпадений нет";
                        break;
                }

                InvalidateChrome();
            });
        });
    }

    private void OpenPicker(bool windows1251, bool viewAsLog)
    {
        if (_busy is not null)
        {
            return;
        }

        _error = null;
        var buffer = new char[65536];
        var filter = "Текстовые файлы\0*.txt;*.log;*.md;*.json;*.xml;*.csv;*.rs;*.cs;*.py;*.ps1\0Все файлы\0*.*\0\0";

        fixed (char* pBuf = buffer)
        fixed (char* pFilter = filter)
        fixed (char* pTitle = "Открыть")
        {
            var ofn = new Win32.OPENFILENAMEW
            {
                lStructSize = (uint)sizeof(Win32.OPENFILENAMEW),
                hwndOwner = _hwnd,
                lpstrFilter = pFilter,
                nFilterIndex = 2,
                lpstrFile = pBuf,
                nMaxFile = (uint)buffer.Length,
                lpstrTitle = pTitle,
                Flags = Win32.OFN_ALLOWMULTISELECT | Win32.OFN_EXPLORER | Win32.OFN_FILEMUSTEXIST | Win32.OFN_PATHMUSTEXIST | Win32.OFN_NOCHANGEDIR,
            };

            if (!Win32.GetOpenFileNameW(&ofn))
            {
                return;
            }
        }

        var paths = ParseMultiSelectBuffer(buffer);
        if (paths.Length > 0)
        {
            OpenPaths(paths, windows1251, viewAsLog);
        }
    }

    private static string[] ParseMultiSelectBuffer(char[] buffer)
    {
        int i = 0;
        var parts = new List<string>();
        while (i < buffer.Length && buffer[i] != '\0')
        {
            int end = Array.IndexOf(buffer, '\0', i);
            if (end < 0) break;
            parts.Add(new string(buffer, i, end - i));
            i = end + 1;
        }

        if (parts.Count == 0) return [];
        if (parts.Count == 1) return [parts[0]];

        string dir = parts[0];
        var result = new string[parts.Count - 1];
        for (int idx = 1; idx < parts.Count; idx++)
        {
            result[idx - 1] = Path.Combine(dir, parts[idx]);
        }
        return result;
    }

    private void OpenPaths(string[] paths, bool windows1251, bool viewAsLog, SessionTab? closedTab = null)
    {
        if (paths.Length == 0 || _busy is not null)
        {
            return;
        }

        _busy = "Открытие…";
        _error = null;
        var recentCopy = _recent.Clone();
        InvalidateChrome();

        ThreadPool.QueueUserWorkItem(_ =>
        {
            var loadedItems = new List<(NativeOpenedFile? Editor, FileTarget? LogTarget)>();
            var recentBatch = new List<RecentFile>();
            string? firstError = null;
            int failures = 0;

            foreach (var rawPath in paths)
            {
                var name = FileName(rawPath);
                try
                {
                    var fullPath = Path.GetFullPath(rawPath);
                    var version = FileVersion.At(fullPath)
                        ?? throw new FileNotFoundException("Файл не найден", fullPath);

                    if (viewAsLog || version.Bytes > Files.MaxEditorBytes)
                    {
                        var target = new FileTarget(fullPath, version);
                        loadedItems.Add((null, target));
                        recentBatch.Add(RecentFile.From(
                            fullPath,
                            windows1251 ? TextEncoding.Windows1251 : TextEncoding.Utf8,
                            viewAsLog: true));
                    }
                    else
                    {
                        var opened = Files.OpenForEditor(fullPath, windows1251);
                        loadedItems.Add((opened, null));
                        recentBatch.Add(RecentFile.From(opened.Target.Path, opened.Format.Encoding, viewAsLog: false));
                    }
                }
                catch (Exception ex)
                {
                    failures++;
                    firstError ??= $"Не удалось открыть «{name}». {ex.Message}";
                }
            }

            Exception? historyError = null;
            if (recentBatch.Count > 0)
            {
                try
                {
                    recentCopy.Record(recentBatch);
                }
                catch (Exception ex)
                {
                    historyError = ex;
                }
            }

            DispatchUi(() =>
            {
                if (closedTab is not null && loadedItems.Count > 0)
                {
                    _closed.RemoveAll(c => c.Tab.Id == closedTab.Id);
                }

                foreach (var item in loadedItems)
                {
                    if (item.LogTarget is { } logTarget)
                    {
                        ShowLog(logTarget, windows1251, closedTab?.Log, closedTab?.Log is not null);
                    }
                    else if (item.Editor is { } opened)
                    {
                        using (opened)
                        {
                            ShowOpened(opened, closedTab);
                        }
                    }
                }

                _busy = null;
                _recent = recentCopy;
                if (firstError is not null)
                {
                    _error = failures == 1 ? firstError : $"{firstError} Ошибок при открытии: {failures}";
                }
                else if (historyError is not null)
                {
                    _error = RecentWarning(historyError);
                }

                if (_documents.Count == 0)
                {
                    AddDocument(null);
                }

                InvalidateChrome();
            });
        });
    }

    private void ShowOpened(NativeOpenedFile opened, SessionTab? restore = null)
    {
        var existing = _documents.Find(d =>
            d.Log is null &&
            d.File is not null &&
            string.Equals(d.File.Path, opened.Target.Path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            if (Files.ShouldReplaceOpenedDocument(
                existing.Dirty,
                existing.Placeholder,
                existing.File,
                opened.Target,
                existing.Format.Encoding,
                opened.Format.Encoding))
            {
                ScintillaInterop.SetTextUtf8(existing.EditorHwnd, opened.Span);
                ScintillaInterop.ApplyLanguage(existing.EditorHwnd, opened.Target.Path, (int)MathF.Round(_fontSize));
                ScintillaInterop.SetSavePoint(existing.EditorHwnd);
                existing.File = opened.Target;
                existing.Format = opened.Format;
                existing.Placeholder = false;
                existing.PendingEditorReady = true;
                existing.DiskStatus = DiskStatusKind.Current;
                existing.Dirty = false;
                Win32.InvalidateRect(existing.EditorHwnd, null, false);
            }
            SelectTab(existing.Id);
            return;
        }

        RemoveInitialCleanUntitledTab();
        AddDocument(opened);
        if (restore is not null && _documents.Count > 0)
        {
            var doc = _documents[^1];
            ScintillaInterop.SetSelection(doc.EditorHwnd, restore.Selection.Start, restore.Selection.End, restore.Reversed);
            if (restore.Scroll.Length >= 2)
            {
                ScintillaInterop.SetScroll(doc.EditorHwnd, (int)restore.Scroll[0], (int)restore.Scroll[1]);
            }
        }
    }

    private void ShowLog(FileTarget target, bool windows1251, LogBookmark? restoredBookmark, bool restored)
    {
        var existing = _documents.Find(d =>
            d.Log is not null &&
            d.File is not null &&
            string.Equals(d.File.Path, target.Path, StringComparison.OrdinalIgnoreCase) &&
            d.Log.Windows1251 == windows1251);
        if (existing is not null)
        {
            SelectTab(existing.Id);
            return;
        }

        RemoveInitialCleanUntitledTab();
        AddLogDocument(target, windows1251, restoredBookmark, restored);
    }

    private void RemoveInitialCleanUntitledTab()
    {
        if (_documents.Count == 1 && _documents[0].File is null && !_documents[0].Dirty && !_documents[0].Placeholder && _documents[0].Log is null)
        {
            _documents[0].Log?.Dispose();
            Win32.DestroyWindow(_documents[0].EditorHwnd);
            _documents.Clear();
        }
    }

    private void Save(bool saveAs)
    {
        if (_busy is not null) return;
        var doc = ActiveDocument;
        if (doc.Log is not null) return;

        _error = null;
        if (!saveAs && doc.File is not null)
        {
            WriteFile(doc.Id, doc.File);
            return;
        }

        string initialDir = doc.File is not null ? Path.GetDirectoryName(doc.File.Path) ?? "" : "";
        string suggested = doc.File is not null ? doc.Name : "Без имени.txt";
        var buffer = new char[4096];
        suggested.CopyTo(buffer);
        var filter = "Все файлы\0*.*\0Текстовые файлы (*.txt)\0*.txt\0\0";

        string? pickedPath = null;
        fixed (char* pBuf = buffer)
        fixed (char* pFilter = filter)
        fixed (char* pDir = string.IsNullOrEmpty(initialDir) ? null : initialDir)
        fixed (char* pTitle = "Сохранить как")
        {
            var ofn = new Win32.OPENFILENAMEW
            {
                lStructSize = (uint)sizeof(Win32.OPENFILENAMEW),
                hwndOwner = _hwnd,
                lpstrFilter = pFilter,
                nFilterIndex = 1,
                lpstrFile = pBuf,
                nMaxFile = (uint)buffer.Length,
                lpstrInitialDir = pDir,
                lpstrTitle = pTitle,
                Flags = Win32.OFN_EXPLORER | Win32.OFN_PATHMUSTEXIST | Win32.OFN_OVERWRITEPROMPT | Win32.OFN_NOCHANGEDIR,
            };

            if (!Win32.GetSaveFileNameW(&ofn)) return;
            int end = Array.IndexOf(buffer, '\0');
            pickedPath = end >= 0 ? new string(buffer, 0, end) : new string(buffer);
        }

        try
        {
            var target = FileTarget.FromPicker(pickedPath);
            WriteFile(doc.Id, target);
        }
        catch (Exception ex)
        {
            _error = $"Не удалось выбрать файл. {ex.Message}";
            InvalidateChrome();
        }
    }

    private void WriteFile(ulong id, FileTarget target)
    {
        var other = _documents.Find(d =>
            d.Id != id &&
            d.File is not null &&
            string.Equals(d.File.Path, target.Path, StringComparison.OrdinalIgnoreCase));
        if (other is not null)
        {
            _error = $"Файл «{other.Name}» уже открыт в другой вкладке";
            InvalidateChrome();
            return;
        }

        var doc = _documents.Find(d => d.Id == id);
        if (doc is null || doc.Log is not null) return;

        if (doc.File is not null && string.Equals(doc.File.Path, target.Path, StringComparison.OrdinalIgnoreCase))
        {
            target = target with { Version = doc.File.Version };
        }

        var snapshot = ScintillaInterop.CopyUtf8Bytes(doc.EditorHwnd);
        var format = doc.Format;
        var recentCopy = _recent.Clone();
        _busy = "Сохранение…";
        InvalidateChrome();

        ThreadPool.QueueUserWorkItem(_ =>
        {
            FileTarget? savedTarget = null;
            Exception? saveError = null;
            Exception? historyError = null;

            try
            {
                savedTarget = Files.Save(target, format, snapshot);
                try
                {
                    recentCopy.Record([RecentFile.From(savedTarget.Path, format.Encoding, false)]);
                }
                catch (Exception ex)
                {
                    historyError = ex;
                }
            }
            catch (Exception ex)
            {
                saveError = ex;
            }

            DispatchUi(() =>
            {
                _busy = null;
                _recent = recentCopy;
                if (saveError is not null)
                {
                    _error = $"Не удалось сохранить файл. {saveError.Message}";
                }
                else if (savedTarget is not null)
                {
                    if (historyError is not null) _error = RecentWarning(historyError);

                    var targetDoc = _documents.Find(d => d.Id == id);
                    if (targetDoc is not null)
                    {
                        bool pathChanged = targetDoc.File is null ||
                            !string.Equals(targetDoc.File.Path, savedTarget.Path, StringComparison.OrdinalIgnoreCase);
                        targetDoc.Name = FileName(savedTarget.Path);
                        targetDoc.File = savedTarget;
                        targetDoc.Placeholder = false;
                        targetDoc.DiskStatus = DiskStatusKind.Current;
                        ScintillaInterop.SetSavePoint(targetDoc.EditorHwnd);
                        targetDoc.Dirty = false;
                        if (pathChanged)
                        {
                            ScintillaInterop.ApplyLanguage(targetDoc.EditorHwnd, savedTarget.Path, (int)MathF.Round(_fontSize));
                        }
                    }
                }
                InvalidateChrome();
            });
        });
    }

    private void ShowRecentMenu(Win32.RECT anchor)
    {
        if (_busy is not null) return;
        var menu = Win32.CreatePopupMenu();
        var closedMenu = Win32.CreatePopupMenu();
        const int ClosedCmdBase = 1000;
        try
        {
            if (_closed.Count == 0)
            {
                Win32.AppendMenuW(closedMenu, Win32.MF_STRING | Win32.MF_GRAYED | Win32.MF_DISABLED, 0, "Закрытых вкладок нет");
            }
            else
            {
                for (int i = _closed.Count - 1; i >= 0; i--)
                {
                    var (tab, draft) = _closed[i];
                    Win32.AppendMenuW(closedMenu, Win32.MF_STRING, (nuint)(ClosedCmdBase + i), ClosedTabLabel(tab, draft));
                }
            }
            Win32.AppendMenuW(menu, Win32.MF_POPUP, (nuint)closedMenu, "Закрытые вкладки");
            Win32.AppendMenuW(menu, Win32.MF_SEPARATOR, 0, "");

            var entries = _recent.Entries;
            if (entries.Count == 0)
            {
                Win32.AppendMenuW(menu, Win32.MF_STRING | Win32.MF_GRAYED | Win32.MF_DISABLED, 0, "Недавних файлов пока нет");
            }
            else
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    var entry = entries[i];
                    var name = FileName(entry.Path);
                    var dir = Path.GetDirectoryName(entry.Path) ?? "";
                    Win32.AppendMenuW(menu, Win32.MF_STRING, (nuint)(i + 1), $"{name} – {DisplayPath(dir)}");
                }
            }

            var pt = new Win32.POINT { X = anchor.Left, Y = anchor.Bottom };
            Win32.ClientToScreen(_hwnd, &pt);
            int cmd = Win32.TrackPopupMenuEx(menu, Win32.TPM_LEFTALIGN | Win32.TPM_TOPALIGN | Win32.TPM_RETURNCMD, pt.X, pt.Y, _hwnd, null);
            if (cmd > 0 && cmd <= entries.Count)
            {
                var chosen = entries[cmd - 1];
                OpenPaths([chosen.Path], chosen.Windows1251, chosen.ViewAsLog);
            }
            else if (cmd >= ClosedCmdBase && cmd - ClosedCmdBase < _closed.Count)
            {
                ReopenClosedTab(_closed[cmd - ClosedCmdBase].Tab.Id);
            }
        }
        finally
        {
            Win32.DestroyMenu(menu);
        }
    }

    private void ShowLogMenu(Win32.RECT anchor)
    {
        if (_busy is not null) return;
        var menu = Win32.CreatePopupMenu();
        try
        {
            Win32.AppendMenuW(menu, Win32.MF_STRING, 1, "Открыть как лог");
            Win32.AppendMenuW(menu, Win32.MF_STRING, 2, "Открыть лог в Windows-1251");
            var pt = new Win32.POINT { X = anchor.Left, Y = anchor.Bottom };
            Win32.ClientToScreen(_hwnd, &pt);
            int cmd = Win32.TrackPopupMenuEx(menu, Win32.TPM_LEFTALIGN | Win32.TPM_TOPALIGN | Win32.TPM_RETURNCMD, pt.X, pt.Y, _hwnd, null);
            if (cmd == 1) OpenPicker(false, true);
            else if (cmd == 2) OpenPicker(true, true);
        }
        finally
        {
            Win32.DestroyMenu(menu);
        }
    }

    private void ProcessIncomingIpc()
    {
        if (_singleInstance is null || _busy is not null) return;
        while (_singleInstance.TryReceive(out var req) && req is not null)
        {
            if (req.Activate)
            {
                Win32.ShowWindow(_hwnd, Win32.SW_SHOW);
                Win32.SetForegroundWindow(_hwnd);
            }
            var abs = req.ResolveAbsolutePaths();
            if (abs.Length > 0)
            {
                OpenPaths(abs, false, false);
            }
        }
    }

    private void OnFastTimer()
    {
        _fastTicks++;

        ProcessIncomingIpc();

        if (_documents.Count > 0 && ActiveDocument.Log is { } activeLog)
        {
            if (activeLog.IndexProgress is not null || activeLog.PrepProgress is not null || activeLog.TrimProgress is not null || activeLog.SearchProgress is not null)
            {
                InvalidateChrome();
            }
        }

        if ((_fastTicks % 4) == 0)
        {
            foreach (var doc in _documents)
            {
                doc.Log?.MonitorTick500Ms();
            }
        }
    }

    private void OnSlowTimer()
    {
        CheckDiskStatusTick();
        QueueSessionTick();
        Measurements.DumpMemoryProfile($"t={_fastTicks * 125}ms");
    }

    private void CheckDiskStatusTick()
    {
        if (_diskCheckInFlight) return;
        var targets = new List<(ulong Id, FileTarget Target)>();
        foreach (var doc in _documents)
        {
            if (doc.Log is null && doc.File is { } f)
            {
                targets.Add((doc.Id, f));
            }
        }
        if (targets.Count == 0) return;

        _diskCheckInFlight = true;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            var results = new List<(ulong Id, FileTarget Target, DiskStatusKind Status)>(targets.Count);
            foreach (var (id, target) in targets)
            {
                results.Add((id, target, DiskStatus.Check(target)));
            }
            DispatchUi(() =>
            {
                _diskCheckInFlight = false;
                bool changed = false;
                foreach (var (id, target, status) in results)
                {
                    var doc = _documents.Find(d => d.Id == id);
                    if (doc is not null && doc.Log is null && doc.File == target && doc.DiskStatus != status)
                    {
                        doc.DiskStatus = status;
                        changed = true;
                    }
                }
                if (changed) InvalidateChrome();
            });
        });
    }

    private static (SessionTab Tab, byte[]? DraftBytes) SnapshotDocument(Document doc, bool includeDraft)
    {
        SessionFileTarget? sFile = doc.File is null
            ? null
            : new SessionFileTarget(
                doc.File.Path,
                doc.File.Version is { } v ? new SessionVersion(v.Bytes, v.ModifiedUtc, v.CreatedUtc) : null);

        var sFmt = new SessionFormat(
            doc.Format.Encoding.ToString(),
            doc.Format.Endings.ToString(),
            doc.Format.Encoding is TextEncoding.Utf8Bom or TextEncoding.Utf16Le or TextEncoding.Utf16Be);

        if (doc.Log is { } log)
        {
            return (
                new SessionTab(
                    doc.Id,
                    sFile,
                    sFmt,
                    new SessionSelection(0, 0),
                    false,
                    [0f, 0f],
                    null,
                    log.Bookmark),
                null);
        }

        var (selStart, selEnd, reversed) = ScintillaInterop.GetSelection(doc.EditorHwnd);
        var (sx, sy) = ScintillaInterop.GetScroll(doc.EditorHwnd);
        if (includeDraft && doc.Dirty)
        {
            doc.DraftRevision = (doc.DraftRevision ?? 0) + 1;
            var bytes = ScintillaInterop.CopyUtf8Bytes(doc.EditorHwnd);
            return (
                new SessionTab(
                    doc.Id,
                    sFile,
                    sFmt,
                    new SessionSelection(selStart, selEnd),
                    reversed,
                    [sx, sy],
                    doc.DraftRevision,
                    null),
                bytes);
        }

        return (
            new SessionTab(
                doc.Id,
                sFile,
                sFmt,
                new SessionSelection(selStart, selEnd),
                reversed,
                [sx, sy],
                null,
                null),
            null);
    }

    private SessionCapture CaptureSession()
    {
        Win32.RECT winRect;
        Win32.GetWindowRect(_hwnd, &winRect);
        float w = Math.Max(760, winRect.Width / Scale);
        float h = Math.Max(350, winRect.Height / Scale);
        bool maximized = Win32.IsZoomed(_hwnd);

        var tabs = new List<SessionTab>(_documents.Count);
        var payloads = new List<byte[]?>(_documents.Count);
        int activeIdx = 0;

        for (int i = 0; i < _documents.Count; i++)
        {
            var doc = _documents[i];
            if (doc.Id == _activeId) activeIdx = i;
            var (tab, draftBytes) = SnapshotDocument(doc, includeDraft: true);
            tabs.Add(tab);
            payloads.Add(draftBytes);
        }

        List<SessionTab>? closedTabs = _closed.Count > 0 ? new List<SessionTab>(_closed.Count) : null;
        List<byte[]?>? closedPayloads = _closed.Count > 0 ? new List<byte[]?>(_closed.Count) : null;
        if (_closed.Count > 0)
        {
            for (int i = 0; i < _closed.Count; i++)
            {
                closedTabs!.Add(_closed[i].Tab);
                closedPayloads!.Add(_closed[i].DraftBytes);
            }
        }

        var state = new SessionState(tabs, activeIdx, new SessionWindow(w, h, maximized), _wrap, _fontSize, closedTabs);
        return new SessionCapture(state, payloads, closedPayloads);
    }

    private void QueueSessionTick()
    {
        if (_sessionJournal is null || _closingForSession) return;
        var queued = _sessionWriter.Queue(CaptureSession());
        if (queued is not { Revision: var rev, Capture: var cap }) return;

        var journal = _sessionJournal;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            bool ok = false;
            try
            {
                lock (journal)
                {
                    journal.Commit(rev, cap);
                }
                ok = true;
            }
            catch
            {
            }

            DispatchUi(() =>
            {
                _sessionWriter.Complete(rev, cap, ok);
            });
        });
    }

    private bool BeginSessionShutdown()
    {
        if (_sessionShutdownDone) return true;
        if (_busy is not null || _documents.Any(d => d.Log?.IsWritingFile == true))
        {
            return false;
        }

        if (_sessionJournal is null)
        {
            if (_documents.Any(d => d.Dirty))
            {
                return ConfirmDiscard(null);
            }
            return true;
        }

        var queued = _sessionWriter.Queue(CaptureSession());
        if (queued is null && !_sessionWriter.IsInFlight)
        {
            _sessionShutdownDone = true;
            return true;
        }

        _closingForSession = true;
        _busy = "Сохранение сессии…";
        InvalidateChrome();

        var journal = _sessionJournal;
        var cap = queued?.Capture ?? CaptureSession();
        ulong rev = queued?.Revision ?? ulong.MaxValue;

        ThreadPool.QueueUserWorkItem(_ =>
        {
            Exception? err = null;
            try
            {
                lock (journal)
                {
                    journal.Commit(rev, cap);
                }
            }
            catch (Exception ex)
            {
                err = ex;
            }

            DispatchUi(() =>
            {
                _busy = null;
                _closingForSession = false;
                _sessionWriter.Complete(rev, cap, err is null);

                if (err is null)
                {
                    _sessionShutdownDone = true;
                    Win32.DestroyWindow(_hwnd);
                    return;
                }

                int ans = Win32.MessageBoxW(
                    _hwnd,
                    $"Не удалось сохранить сессию. {err.Message}",
                    "Сессия не сохранена. Закрыть окно?",
                    Win32.MB_YESNO | Win32.MB_ICONWARNING);
                if (ans == Win32.IDYES)
                {
                    _sessionShutdownDone = true;
                    Win32.DestroyWindow(_hwnd);
                }
                else
                {
                    InvalidateChrome();
                }
            });
        });

        return false;
    }

    private void RestoreSession(RecoveredSession recovered)
    {
        ulong maxId = 0;
        foreach (var t in recovered.State.Tabs)
            if (t.Id > maxId) maxId = t.Id;
        if (recovered.State.Closed is { } closedList)
        {
            for (int i = 0; i < closedList.Count; i++)
            {
                var ct = closedList[i];
                if (ct.Id > maxId) maxId = ct.Id;
                string? ctDraft = i < recovered.ClosedDraftTexts.Count ? recovered.ClosedDraftTexts[i] : null;
                _closed.Add((ct, ctDraft is null ? null : Encoding.UTF8.GetBytes(ctDraft)));
            }
        }
        if (maxId >= _nextId) _nextId = maxId + 1;

        for (int i = 0; i < recovered.State.Tabs.Count; i++)
        {
            RestoreTextTab(recovered.State.Tabs[i], recovered.DraftTexts[i]);
        }

        if (_documents.Count == 0)
        {
            AddDocument(null);
        }
        else
        {
            int idx = Math.Clamp(recovered.State.Active, 0, _documents.Count - 1);
            SelectTab(_documents[idx].Id);
        }
    }

    private ulong RestoreTextTab(SessionTab tab, string? draft)
    {
        if (tab.Log is not null && tab.File is not null)
        {
            var ver = tab.File.Version is { } sv ? new FileVersion(sv.Bytes, sv.ModifiedUtc, sv.CreatedUtc) : (FileVersion?)null;
            bool win1251 = string.Equals(tab.Format.Encoding, nameof(TextEncoding.Windows1251), StringComparison.OrdinalIgnoreCase);
            ShowLog(new FileTarget(tab.File.Path, ver), win1251, tab.Log, restored: true);
            return _documents[^1].Id;
        }

        FileTarget? fileTarget = tab.File is null
            ? null
            : new FileTarget(
                tab.File.Path,
                tab.File.Version is { } v ? new FileVersion(v.Bytes, v.ModifiedUtc, v.CreatedUtc) : null);

        bool win1251Doc = string.Equals(tab.Format.Encoding, nameof(TextEncoding.Windows1251), StringComparison.OrdinalIgnoreCase);
        OpenedFile? diskOpened = null;
        Exception? diskError = null;
        if (fileTarget is not null)
        {
            try
            {
                diskOpened = Files.Open(fileTarget.Path, win1251Doc);
            }
            catch (Exception ex)
            {
                diskError = ex;
            }
        }

        var enc = Enum.TryParse<TextEncoding>(tab.Format.Encoding, out var pe) ? pe : TextEncoding.Utf8;
        var eol = Enum.TryParse<LineEndings>(tab.Format.LineEndings, out var pl) ? pl : LineEndings.Lf;
        var restoredFormat = new TextFormat(enc, eol);

        ulong id = _nextId++;
        var editorHwnd = ScintillaInterop.CreateEditorControl(_hwnd, _hInstance, (int)MathF.Round(_fontSize), _wrap);
        var doc = new Document
        {
            Id = id,
            Name = fileTarget is null ? "Без имени" : FileName(fileTarget.Path),
            File = fileTarget,
            Format = restoredFormat,
            EditorHwnd = editorHwnd,
        };

        if (draft is not null)
        {
            var draftBytes = Encoding.UTF8.GetBytes(draft);
            ScintillaInterop.SetTextUtf8(editorHwnd, draftBytes);
            doc.Dirty = true;
            doc.DraftRevision = tab.DraftRevision;

            if (diskOpened is not null && fileTarget is not null && diskOpened.Target.Version == fileTarget.Version)
            {
                doc.DiskStatus = DiskStatusKind.Current;
            }
            else if (diskOpened is not null)
            {
                doc.DiskStatus = DiskStatusKind.Changed;
            }
            else if (diskError is not null)
            {
                doc.DiskStatus = DiskStatus.FromException(diskError);
            }
        }
        else if (diskOpened is not null)
        {
            ScintillaInterop.SetTextUtf8(editorHwnd, diskOpened.Utf8Bytes);
            doc.File = diskOpened.Target;
            doc.Format = diskOpened.Format;
            doc.Dirty = false;
        }
        else if (diskError is not null)
        {
            doc.DiskStatus = DiskStatus.FromException(diskError);
            doc.Placeholder = true;
        }

        ScintillaInterop.ApplyLanguage(editorHwnd, doc.File?.Path, (int)MathF.Round(_fontSize));
        ScintillaInterop.SetSelection(editorHwnd, tab.Selection.Start, tab.Selection.End, tab.Reversed);
        if (tab.Scroll.Length >= 2)
        {
            ScintillaInterop.SetScroll(editorHwnd, (int)tab.Scroll[0], (int)tab.Scroll[1]);
        }
        _documents.Add(doc);
        return id;
    }

    private void SyncLogInputsAndHits()
    {
        if (_documents.Count == 0) return;
        var doc = ActiveDocument;
        var log = doc.Log;
        if (log is null) return;

        _suppressEditEvents = true;
        try
        {
            SetEditTextIfDifferent(_logJumpEdit, log.JumpPercentInput);
            SetEditTextIfDifferent(_logTrimEdit, log.TrimPercentInput);
            SetEditTextIfDifferent(_logSearchEdit, log.SearchPattern);

            if (_lastSyncedHitLogId != doc.Id || _lastSyncedHitCount != log.SearchHits.Count)
            {
                _lastSyncedHitLogId = doc.Id;
                _lastSyncedHitCount = log.SearchHits.Count;
                Win32.SendMessageW(_logSearchList, Win32.LB_RESETCONTENT, 0, 0);
                foreach (var hit in log.SearchHits)
                {
                    Win32.SendMessageW(_logSearchList, Win32.LB_ADDSTRING, 0, $"Байт {hit.LineStart}: {hit.Preview}");
                }
            }
        }
        finally
        {
            _suppressEditEvents = false;
        }
    }

    private static void SetEditTextIfDifferent(nint editHwnd, string text)
    {
        if (editHwnd == 0) return;
        if (ReadControlText(editHwnd) != text)
        {
            Win32.SetWindowTextW(editHwnd, text);
        }
    }

    private static string ReadControlText(nint hwnd)
    {
        int len = Win32.GetWindowTextLengthW(hwnd);
        if (len <= 0) return "";
        var buf = new char[len + 1];
        fixed (char* ptr = buf)
        {
            int actual = Win32.GetWindowTextW(hwnd, ptr, buf.Length);
            return new string(ptr, 0, actual);
        }
    }

    private void DispatchUi(Action action)
    {
        _uiQueue.Enqueue(action);
        if (_hwnd != 0)
        {
            Win32.PostMessageW(_hwnd, Win32.WM_APP_DISPATCH, 0, 0);
        }
    }

    private void DrainUiQueue()
    {
        while (_uiQueue.TryDequeue(out var action))
        {
            action();
        }
    }

    private int TopChromeLogicalHeight()
    {
        int h = ToolbarLogicalHeight + TabBarLogicalHeight;
        if (_documents.Count > 0 && ActiveDocument.Log is { } log)
        {
            h += LogToolbarLogicalHeight;
            if (log.SearchOpen) h += LogSearchLogicalHeight;
        }
        else if (_searchOpen)
        {
            h += EditorSearchLogicalHeight;
        }
        return h;
    }

    private void LayoutAndSyncControls()
    {
        if (_hwnd == 0 || _documents.Count == 0)
        {
            return;
        }

        Win32.RECT client;
        Win32.GetClientRect(_hwnd, &client);
        int topH = S(TopChromeLogicalHeight());
        int botH = S(StatusLogicalHeight);
        int editorH = Math.Max(0, client.Height - topH - botH);

        foreach (var doc in _documents)
        {
            Win32.MoveWindow(doc.EditorHwnd, 0, topH, client.Width, editorH, true);
        }

        bool isLog = ActiveDocument.Log is not null;
        int yBase = S(ToolbarLogicalHeight + TabBarLogicalHeight);

        if (!isLog && _searchOpen)
        {
            int pad = S(12);
            int editH = S(26);
            int qW = Math.Clamp(client.Width / 3, S(160), S(340));
            Win32.MoveWindow(_searchQueryEdit, pad, yBase + S(8), qW, editH, true);
            Win32.MoveWindow(_searchReplaceEdit, pad, yBase + S(42), qW, editH, true);
            Win32.ShowWindow(_searchQueryEdit, Win32.SW_SHOW);
            Win32.ShowWindow(_searchReplaceEdit, Win32.SW_SHOW);
            Win32.EnableWindow(_searchReplaceEdit, !_searchAllTabs);
        }
        else
        {
            Win32.ShowWindow(_searchQueryEdit, Win32.SW_HIDE);
            Win32.ShowWindow(_searchReplaceEdit, Win32.SW_HIDE);
        }

        if (isLog && ActiveDocument.Log is { } log)
        {
            int row1Y = yBase + S(8);
            int row3Y = yBase + S(76);
            int pad = S(12);
            int btnH = S(26);

            int navW = S(72 + 64 + 74 + 62) + S(16);
            Win32.MoveWindow(_logJumpEdit, pad + navW, row1Y, S(72), btnH, true);
            Win32.ShowWindow(_logJumpEdit, Win32.SW_SHOW);

            Win32.MoveWindow(_logTrimEdit, pad + S(74), row3Y, S(63), btnH, true);
            Win32.ShowWindow(_logTrimEdit, Win32.SW_SHOW);

            if (log.SearchOpen)
            {
                int sTop = yBase + S(LogToolbarLogicalHeight);
                int qW = Math.Clamp(client.Width / 3, S(160), S(320));
                Win32.MoveWindow(_logSearchEdit, pad, sTop + S(8), qW, btnH, true);
                Win32.ShowWindow(_logSearchEdit, Win32.SW_SHOW);

                Win32.MoveWindow(_logSearchList, pad, sTop + S(66), Math.Max(S(200), client.Width - pad * 2), S(78), true);
                Win32.ShowWindow(_logSearchList, Win32.SW_SHOW);
            }
            else
            {
                Win32.ShowWindow(_logSearchEdit, Win32.SW_HIDE);
                Win32.ShowWindow(_logSearchList, Win32.SW_HIDE);
            }
        }
        else
        {
            Win32.ShowWindow(_logJumpEdit, Win32.SW_HIDE);
            Win32.ShowWindow(_logTrimEdit, Win32.SW_HIDE);
            Win32.ShowWindow(_logSearchEdit, Win32.SW_HIDE);
            Win32.ShowWindow(_logSearchList, Win32.SW_HIDE);
        }
    }

    private void InvalidateChrome()
    {
        if (_hwnd == 0) return;
        Win32.RECT client;
        Win32.GetClientRect(_hwnd, &client);
        var topRect = new Win32.RECT { Left = 0, Top = 0, Right = client.Width, Bottom = S(TopChromeLogicalHeight()) };
        var botRect = new Win32.RECT { Left = 0, Top = Math.Max(0, client.Height - S(StatusLogicalHeight)), Right = client.Width, Bottom = client.Height };
        Win32.InvalidateRect(_hwnd, &topRect, false);
        Win32.InvalidateRect(_hwnd, &botRect, false);
    }

    private void UpdateActiveCursor()
    {
        if (_documents.Count == 0 || ActiveDocument.Log is not null) return;
        var doc = ActiveDocument;
        var (line, col) = ScintillaInterop.GetCursorPosition(doc.EditorHwnd);
        if (line != doc.CursorLine || col != doc.CursorColumn)
        {
            doc.CursorLine = line;
            doc.CursorColumn = col;
            InvalidateChrome();
        }
        ScintillaInterop.UpdateLineNumberMarginWidth(doc.EditorHwnd);
    }

    private void Paint(nint hdc, Win32.RECT client)
    {
        if (client.Width <= 0 || client.Height <= 0) return;

        int toolbarH = S(ToolbarLogicalHeight);
        int tabBarH = S(TabBarLogicalHeight);
        int totalTopH = S(TopChromeLogicalHeight());
        int statusH = S(StatusLogicalHeight);
        int statusTop = Math.Max(totalTopH, client.Height - statusH);
        int statusActualH = Math.Max(1, client.Height - statusTop);

        _hits.Clear();

        if (totalTopH > 0)
        {
            nint topDc = _topChromeBuffer.Ensure(hdc, client.Width, totalTopH);
            if (topDc != 0)
            {
                nint oldTopFont = Win32.SelectObject(topDc, _uiFont);
                try
                {
                    Win32.SetBkMode(topDc, Win32.TRANSPARENT);

                    var toolbarRect = new Win32.RECT { Left = 0, Top = 0, Right = client.Width, Bottom = toolbarH };
                    Win32.FillRect(topDc, &toolbarRect, _panelBrush);
                    DrawHLine(topDc, 0, toolbarH - 1, client.Width, ThemePalette.Border);
                    PaintToolbar(topDc, client.Width, toolbarH);

                    var tabRect = new Win32.RECT { Left = 0, Top = toolbarH, Right = client.Width, Bottom = toolbarH + tabBarH };
                    Win32.FillRect(topDc, &tabRect, _tabBarBrush);
                    DrawHLine(topDc, 0, toolbarH + tabBarH - 1, client.Width, ThemePalette.Border);
                    PaintTabs(topDc, client.Width, toolbarH, tabBarH);

                    int panelTop = toolbarH + tabBarH;
                    if (totalTopH > panelTop)
                    {
                        var panelRect = new Win32.RECT { Left = 0, Top = panelTop, Right = client.Width, Bottom = totalTopH };
                        Win32.FillRect(topDc, &panelRect, _panelBrush);
                        DrawHLine(topDc, 0, totalTopH - 1, client.Width, ThemePalette.Border);

                        if (_documents.Count > 0 && ActiveDocument.Log is { } log)
                        {
                            PaintLogToolbarAndSearch(topDc, client.Width, panelTop, log);
                        }
                        else if (_searchOpen)
                        {
                            PaintEditorSearch(topDc, client.Width, panelTop);
                        }
                    }

                    Win32.BitBlt(hdc, 0, 0, client.Width, totalTopH, topDc, 0, 0, Win32.SRCCOPY);
                    Measurements.RecordFirstFramePresented();
                }
                finally
                {
                    Win32.SelectObject(topDc, oldTopFont);
                }
            }
        }

        nint statusDc = _statusBuffer.Ensure(hdc, client.Width, statusActualH);
        if (statusDc != 0)
        {
            nint oldStatusFont = Win32.SelectObject(statusDc, _uiFont);
            try
            {
                Win32.SetBkMode(statusDc, Win32.TRANSPARENT);
                var statusRect = new Win32.RECT { Left = 0, Top = 0, Right = client.Width, Bottom = statusActualH };
                Win32.FillRect(statusDc, &statusRect, _panelBrush);
                DrawHLine(statusDc, 0, 0, client.Width, ThemePalette.Border);
                PaintStatus(statusDc, client.Width, 0, statusActualH, statusTop);
                Win32.BitBlt(hdc, 0, statusTop, client.Width, statusActualH, statusDc, 0, 0, Win32.SRCCOPY);
            }
            finally
            {
                Win32.SelectObject(statusDc, oldStatusFont);
            }
        }
    }

    private void PaintToolbar(nint hdc, int width, int height)
    {
        bool busy = _busy is not null;
        bool isLog = _documents.Count > 0 && ActiveDocument.Log is not null;
        int padX = S(12);
        int gap = S(4);
        int btnH = S(28);
        int y = (height - btnH) / 2;
        int x = padX;

        x = DrawButton(hdc, x, y, btnH, "Создать", HitKind.BtnNew, 0, false, false) + gap;
        x = DrawButton(hdc, x, y, btnH, "Открыть", HitKind.BtnOpen, 0, busy, false) + gap;
        x = DrawButton(hdc, x, y, btnH, "Недавние", HitKind.BtnRecent, 0, busy, false) + gap;
        x = DrawButton(hdc, x, y, btnH, "Лог", HitKind.BtnLogMenu, 0, busy, false) + gap;
        x = DrawButton(hdc, x, y, btnH, "1251", HitKind.BtnOpen1251, 0, busy, false) + gap;
        x = DrawButton(hdc, x, y, btnH, "Сохранить", HitKind.BtnSave, 0, busy || isLog, false) + gap;
        DrawButton(hdc, x, y, btnH, "Сохранить как…", HitKind.BtnSaveAs, 0, busy || isLog, false);

        int rightX = width - padX;
        int zoomInW = MeasureButtonWidth(hdc, "+");
        int zoomOutW = MeasureButtonWidth(hdc, "−");
        int wrapW = MeasureButtonWidth(hdc, "Перенос");
        int jsonW = MeasureButtonWidth(hdc, "JSON");
        int findW = MeasureButtonWidth(hdc, "Поиск");

        DrawButtonFixed(hdc, rightX - zoomInW, y, zoomInW, btnH, "+", HitKind.BtnZoomIn, 0, isLog, false);
        rightX -= zoomInW + gap;
        DrawButtonFixed(hdc, rightX - zoomOutW, y, zoomOutW, btnH, "−", HitKind.BtnZoomOut, 0, isLog, false);
        rightX -= zoomOutW + gap;
        DrawButtonFixed(hdc, rightX - wrapW, y, wrapW, btnH, "Перенос", HitKind.BtnWrap, 0, false, _wrap);
        rightX -= wrapW + gap;
        DrawButtonFixed(hdc, rightX - jsonW, y, jsonW, btnH, "JSON", HitKind.BtnFormatJson, 0, busy || isLog, false);
        rightX -= jsonW + gap;
        DrawButtonFixed(hdc, rightX - findW, y, findW, btnH, "Поиск", HitKind.BtnFindText, 0, false, !isLog && _searchOpen);
    }

    private void PaintEditorSearch(nint hdc, int width, int top)
    {
        int pad = S(12);
        int gap = S(4);
        int btnH = S(26);
        int qW = Math.Clamp(width / 3, S(160), S(340));
        int x = pad + qW + S(8);
        int y1 = top + S(8);
        int y2 = top + S(42);
        bool busy = _busy is not null || _searchInFlight;
        bool replaceDisabled = busy || _searchAllTabs || (_documents.Count > 0 && ActiveDocument.Placeholder);

        x = DrawButton(hdc, x, y1, btnH, "Назад", HitKind.SearchPrev, 0, busy, false) + gap;
        x = DrawButton(hdc, x, y1, btnH, "Далее", HitKind.SearchNext, 0, busy, false) + gap;
        x = DrawButton(hdc, x, y1, btnH, "Регулярка", HitKind.SearchRegex, 0, false, _searchRegex) + gap;
        x = DrawButton(hdc, x, y1, btnH, "С учётом регистра", HitKind.SearchCase, 0, false, _searchCase) + gap;
        x = DrawButton(hdc, x, y1, btnH, "Целое слово", HitKind.SearchWhole, 0, false, _searchWhole) + gap;
        x = DrawButton(hdc, x, y1, btnH, "Все текстовые вкладки", HitKind.SearchAllTabs, 0, false, _searchAllTabs) + gap;
        DrawButton(hdc, x, y1, btnH, "Закрыть", HitKind.SearchClose, 0, false, false);

        int x2 = pad + qW + S(8);
        x2 = DrawButton(hdc, x2, y2, btnH, "Заменить", HitKind.SearchReplaceOne, 0, replaceDisabled, false) + gap;
        x2 = DrawButton(hdc, x2, y2, btnH, "Заменить всё", HitKind.SearchReplaceAll, 0, replaceDisabled, false) + S(10);

        var stRect = new Win32.RECT { Left = x2, Top = y2, Right = Math.Max(x2, width - pad), Bottom = y2 + btnH };
        Win32.SelectObject(hdc, _smallFont);
        Win32.SetTextColor(hdc, ThemePalette.Muted);
        Win32.DrawTextW(hdc, _searchStatus, -1, &stRect, Win32.DT_LEFT | Win32.DT_VCENTER | Win32.DT_SINGLELINE | Win32.DT_END_ELLIPSIS | Win32.DT_NOPREFIX);
    }

    private void PaintLogToolbarAndSearch(nint hdc, int width, int top, LogViewerState log)
    {
        int pad = S(12);
        int gap = S(4);
        int btnH = S(26);
        bool en = log.IsEnabled;
        int row1Y = top + S(8);
        int row2Y = top + S(42);
        int row3Y = top + S(76);

        int x = pad;
        x = DrawButton(hdc, x, row1Y, btnH, "Начало", HitKind.LogStart, 0, !en, false) + gap;
        x = DrawButton(hdc, x, row1Y, btnH, "Назад", HitKind.LogPrev, 0, !en || log.Bookmark.Offset <= log.Bookmark.Hidden, false) + gap;
        x = DrawButton(hdc, x, row1Y, btnH, "Дальше", HitKind.LogNext, 0, !en || (log.Page?.Eof ?? true), false) + gap;
        x = DrawButton(hdc, x, row1Y, btnH, "Конец", HitKind.LogEnd, 0, !en, false) + gap;
        x += S(76) + gap;
        x = DrawButton(hdc, x, row1Y, btnH, "Перейти, %", HitKind.LogJumpPercent, 0, !en, false) + gap;
        DrawButton(hdc, x, row1Y, btnH, "Поиск в логе", HitKind.LogSearchOpen, 0, !en, log.SearchOpen);

        int x2 = pad;
        x2 = DrawButton(hdc, x2, row2Y, btnH, log.Bookmark.AutoRefresh ? "Автообновление: вкл." : "Автообновление: пауза", HitKind.LogAutoRefresh, 0, false, log.Bookmark.AutoRefresh) + gap;
        x2 = DrawButton(hdc, x2, row2Y, btnH, log.Bookmark.FollowEnd ? "Следовать за концом: вкл." : "Следовать за концом: выкл.", HitKind.LogFollowEnd, 0, false, log.Bookmark.FollowEnd) + gap;
        x2 = DrawButton(hdc, x2, row2Y, btnH, "Обновить", HitKind.LogRefreshNow, 0, log.IsWritingFile || log.PreparingTrim, false) + gap;
        if (log.IndexProgress is not null || log.IndexPaused)
        {
            DrawButton(hdc, x2, row2Y, btnH, log.IndexPaused ? "Продолжить индекс" : "Отменить индекс", HitKind.LogIndexToggle, 0, false, false);
        }

        var lblRect = new Win32.RECT { Left = pad, Top = row3Y, Right = pad + S(70), Bottom = row3Y + btnH };
        Win32.SelectObject(hdc, _smallFont);
        Win32.SetTextColor(hdc, ThemePalette.Muted);
        Win32.DrawTextW(hdc, "Начало, %", -1, &lblRect, Win32.DT_LEFT | Win32.DT_VCENTER | Win32.DT_SINGLELINE | Win32.DT_NOPREFIX);

        int x3 = pad + S(74) + S(63) + gap;
        x3 = DrawButton(hdc, x3, row3Y, btnH, "Скрыть начало", HitKind.LogHideStart, 0, !en, false) + gap;
        x3 = DrawButton(hdc, x3, row3Y, btnH, "Удалить начало…", HitKind.LogPhysicalTrim, 0, !en, false) + gap;
        x3 = DrawButton(hdc, x3, row3Y, btnH, "Показать весь файл", HitKind.LogRevealAll, 0, !en || log.Bookmark.Hidden == 0, false) + gap;
        DrawButton(hdc, x3, row3Y, btnH, "Отменить обрезку", HitKind.LogCancelTrim, 0, !log.PreparingTrim && !log.IsWritingFile, false);

        if (log.SearchOpen)
        {
            int sTop = top + S(LogToolbarLogicalHeight);
            DrawHLine(memDc: hdc, 0, sTop, width, ThemePalette.Border);
            int qW = Math.Clamp(width / 3, S(160), S(320));
            int sx = pad + qW + S(8);
            int sy = sTop + S(8);
            bool searching = log.SearchProgress is not null;

            sx = DrawButton(hdc, sx, sy, btnH, "Найти", HitKind.LogFindNew, 0, !en || searching, false) + gap;
            sx = DrawButton(hdc, sx, sy, btnH, "Ещё", HitKind.LogFindMore, 0, !en || searching || log.SearchComplete || log.SearchHits.Count == 0, false) + gap;
            sx = DrawButton(hdc, sx, sy, btnH, "Отменить поиск", HitKind.LogFindCancel, 0, !searching || log.SearchStopping, false) + gap;
            sx = DrawButton(hdc, sx, sy, btnH, "Закрыть поиск", HitKind.LogFindClose, 0, false, false) + gap;

            int sx2 = pad;
            int sy2 = sTop + S(38);
            sx2 = DrawButton(hdc, sx2, sy2, btnH, "Регулярка", HitKind.LogFindRegex, 0, false, log.SearchRegex) + gap;
            sx2 = DrawButton(hdc, sx2, sy2, btnH, "С учётом регистра", HitKind.LogFindCase, 0, false, log.SearchCase) + gap;
            sx2 = DrawButton(hdc, sx2, sy2, btnH, "Целое слово", HitKind.LogFindWhole, 0, false, log.SearchWholeWord) + S(10);

            var stRect = new Win32.RECT { Left = sx2, Top = sy2, Right = Math.Max(sx2, width - pad), Bottom = sy2 + btnH };
            Win32.SelectObject(hdc, _smallFont);
            Win32.SetTextColor(hdc, ThemePalette.Muted);
            Win32.DrawTextW(hdc, log.SearchStatus, -1, &stRect, Win32.DT_LEFT | Win32.DT_VCENTER | Win32.DT_SINGLELINE | Win32.DT_END_ELLIPSIS | Win32.DT_NOPREFIX);
        }
    }

    private string TabLabel(Document doc)
    {
        string prefix = doc.Dirty ? "●  " : "";
        string? suffix = doc.Placeholder ? "откройте заново" : DiskStatus.TabSuffix(doc.DiskStatus);
        return suffix is null ? $"{prefix}{doc.Name}" : $"{prefix}{doc.Name} ({suffix})";
    }

    private void EnsureTabVisible(ulong id)
    {
        if (_hwnd == 0) return;
        Win32.RECT rc;
        Win32.GetClientRect(_hwnd, &rc);
        if (rc.Width <= 0) return;

        nint hdc = Win32.GetDC(_hwnd);
        if (hdc == 0) return;
        nint oldFont = Win32.SelectObject(hdc, _uiFont);
        try
        {
            int padX = S(12);
            int gap = S(4);
            int maxTabW = S(280);
            int closeW = S(24);
            int actionW = S(28);
            int availWidth = Math.Max(1, rc.Width - padX * 2 - actionW * 2 - gap * 2);

            int x = 0;
            foreach (var doc in _documents)
            {
                int labelW = Math.Min(maxTabW, MeasureTextWidth(hdc, TabLabel(doc), _uiFont) + S(20));
                int totalW = labelW + closeW;
                if (doc.Id == id)
                {
                    if (x < _tabScrollX) _tabScrollX = x;
                    else if (x + totalW > _tabScrollX + availWidth) _tabScrollX = Math.Max(0, x + totalW - availWidth);
                    break;
                }
                x += totalW + gap;
            }
        }
        finally
        {
            Win32.SelectObject(hdc, oldFont);
            Win32.ReleaseDC(_hwnd, hdc);
        }
    }

    private void PaintTabs(nint hdc, int width, int top, int height)
    {
        int padX = S(12);
        int gap = S(4);
        int tabH = S(28);
        int y = top + (height - tabH) / 2;
        int x = padX - _tabScrollX;
        int maxTabW = S(280);
        int closeW = S(24);
        int actionW = S(28);
        int availRight = width - padX - actionW * 2 - gap * 2;

        for (int i = 0; i < _documents.Count; i++)
        {
            var doc = _documents[i];
            bool active = doc.Id == _activeId;
            string label = TabLabel(doc);
            int labelW = Math.Min(maxTabW, MeasureTextWidth(hdc, label, _uiFont) + S(20));
            int totalW = labelW + closeW;

            if (x + totalW > padX && x < availRight)
            {
                int drawLeft = Math.Max(padX, x);
                int drawRight = Math.Min(availRight, x + totalW);
                if (drawRight > drawLeft + closeW)
                {
                    var box = new Win32.RECT { Left = drawLeft, Top = y, Right = drawRight, Bottom = y + tabH };
                    FillRoundRect(
                        hdc,
                        box,
                        S(6),
                        active ? ThemePalette.TabActiveBg : ThemePalette.TabInactiveBg,
                        active ? ThemePalette.TabActiveBorder : ThemePalette.TabInactiveBorder);

                    var selectRect = new Win32.RECT { Left = drawLeft + S(8), Top = y, Right = drawRight - closeW, Bottom = y + tabH };
                    Win32.SelectObject(hdc, _uiFont);
                    Win32.SetTextColor(hdc, ThemePalette.Text);
                    Win32.DrawTextW(hdc, label, -1, &selectRect, Win32.DT_LEFT | Win32.DT_VCENTER | Win32.DT_SINGLELINE | Win32.DT_END_ELLIPSIS | Win32.DT_NOPREFIX);
                    _hits.Add(new HitRect(new Win32.RECT { Left = drawLeft, Top = y, Right = drawRight - closeW, Bottom = y + tabH }, HitKind.TabSelect, doc.Id));

                    var closeRect = new Win32.RECT { Left = drawRight - closeW, Top = y + S(2), Right = drawRight - S(2), Bottom = y + tabH - S(2) };
                    if (_hoverKind == HitKind.TabClose && _hoverTabId == doc.Id)
                    {
                        FillRoundRect(hdc, closeRect, S(4), ThemePalette.HoverBg, ThemePalette.HoverBg);
                    }
                    Win32.DrawTextW(hdc, "×", -1, &closeRect, Win32.DT_CENTER | Win32.DT_VCENTER | Win32.DT_SINGLELINE | Win32.DT_NOPREFIX);
                    _hits.Add(new HitRect(closeRect, HitKind.TabClose, doc.Id));
                }
            }

            x += totalW + gap;
        }

        int newTabX = Math.Min(Math.Max(padX, x), availRight + gap);
        DrawButtonFixed(hdc, newTabX, y, actionW, tabH, "+", HitKind.BtnNewTab, 0, _busy is not null, false);

        int reopenX = newTabX + actionW + gap;
        bool reopenDisabled = _busy is not null || _closed.Count == 0;
        DrawButtonFixed(hdc, reopenX, y, actionW, tabH, "↶", HitKind.BtnReopen, 0, reopenDisabled, false);
    }

    private void PaintStatus(nint hdc, int width, int top, int height, int hitYOffset)
    {
        if (_documents.Count == 0) return;
        var doc = ActiveDocument;
        int padX = S(16);
        int gap = S(16);
        int rightX = width - padX;

        if (doc.Log is { } log)
        {
            string enc = TextFormat.EncodingLabelOf(log.Encoding);
            string pos = log.Bookmark.Hidden > 0
                ? $"Байты {log.Bookmark.Offset}–{log.PageEndOffset} из {log.Bookmark.Size} · скрыто {log.Bookmark.Hidden}"
                : $"Байты {log.Bookmark.Offset}–{log.PageEndOffset} из {log.Bookmark.Size}";
            int encW = MeasureTextWidth(hdc, enc, _smallFont);
            int posW = MeasureTextWidth(hdc, pos, _smallFont);

            Win32.SelectObject(hdc, _smallFont);
            Win32.SetTextColor(hdc, ThemePalette.Muted);

            var encRect = new Win32.RECT { Left = rightX - encW, Top = top, Right = rightX, Bottom = top + height };
            Win32.DrawTextW(hdc, enc, -1, &encRect, Win32.DT_RIGHT | Win32.DT_VCENTER | Win32.DT_SINGLELINE | Win32.DT_NOPREFIX);
            rightX -= encW + gap;

            var posRect = new Win32.RECT { Left = rightX - posW, Top = top, Right = rightX, Bottom = top + height };
            Win32.DrawTextW(hdc, pos, -1, &posRect, Win32.DT_LEFT | Win32.DT_VCENTER | Win32.DT_SINGLELINE | Win32.DT_NOPREFIX);
            rightX -= posW + gap;

            var msgRect = new Win32.RECT { Left = padX, Top = top, Right = Math.Max(padX, rightX), Bottom = top + height };
            Win32.DrawTextW(hdc, _error ?? log.LeftStatusText(), -1, &msgRect, Win32.DT_LEFT | Win32.DT_VCENTER | Win32.DT_SINGLELINE | Win32.DT_END_ELLIPSIS | Win32.DT_NOPREFIX);
            return;
        }

        string? diskNotice = DiskStatus.Notice(doc.DiskStatus);
        string message = _error
            ?? _busy
            ?? diskNotice
            ?? (doc.File is not null ? DisplayPath(doc.File.Path) : "Новый документ");

        string eolLabel = doc.Format.LineEndingsLabel;
        string encLabel = doc.Format.EncodingLabel;
        string posLabel = $"Стр. {doc.CursorLine + 1}, стлб. {doc.CursorColumn + 1}";

        int eolW2 = MeasureTextWidth(hdc, eolLabel, _smallFont);
        int encW2 = MeasureTextWidth(hdc, encLabel, _smallFont);
        int posW2 = MeasureTextWidth(hdc, posLabel, _smallFont) + S(12);

        Win32.SelectObject(hdc, _smallFont);
        Win32.SetTextColor(hdc, ThemePalette.Muted);

        var eolRect = new Win32.RECT { Left = rightX - eolW2, Top = top, Right = rightX, Bottom = top + height };
        Win32.DrawTextW(hdc, eolLabel, -1, &eolRect, Win32.DT_RIGHT | Win32.DT_VCENTER | Win32.DT_SINGLELINE | Win32.DT_NOPREFIX);
        rightX -= eolW2 + gap;

        var encRect2 = new Win32.RECT { Left = rightX - encW2, Top = top, Right = rightX, Bottom = top + height };
        Win32.DrawTextW(hdc, encLabel, -1, &encRect2, Win32.DT_LEFT | Win32.DT_VCENTER | Win32.DT_SINGLELINE | Win32.DT_NOPREFIX);
        rightX -= encW2 + gap;

        var goRect = new Win32.RECT { Left = rightX - posW2, Top = top + S(3), Right = rightX, Bottom = top + height - S(3) };
        if (_hoverKind == HitKind.BtnGoToLine && _busy is null)
        {
            FillRoundRect(hdc, goRect, S(4), ThemePalette.HoverBg, ThemePalette.HoverBg);
        }
        Win32.SetTextColor(hdc, _busy is not null ? ThemePalette.Dim : ThemePalette.Muted);
        Win32.DrawTextW(hdc, posLabel, -1, &goRect, Win32.DT_CENTER | Win32.DT_VCENTER | Win32.DT_SINGLELINE | Win32.DT_NOPREFIX);
        _hits.Add(new HitRect(
            new Win32.RECT { Left = goRect.Left, Top = goRect.Top + hitYOffset, Right = goRect.Right, Bottom = goRect.Bottom + hitYOffset },
            HitKind.BtnGoToLine));
        rightX -= posW2 + gap;

        var msgRect2 = new Win32.RECT { Left = padX, Top = top, Right = Math.Max(padX, rightX), Bottom = top + height };
        Win32.SetTextColor(hdc, _error is not null ? ThemePalette.Error : ThemePalette.Muted);
        Win32.DrawTextW(hdc, message, -1, &msgRect2, Win32.DT_LEFT | Win32.DT_VCENTER | Win32.DT_SINGLELINE | Win32.DT_END_ELLIPSIS | Win32.DT_NOPREFIX);
    }

    private int MeasureButtonWidth(nint hdc, string text) => MeasureTextWidth(hdc, text, _uiFont) + S(20);

    private int MeasureTextWidth(nint hdc, string text, nint font)
    {
        Win32.SelectObject(hdc, font);
        Win32.SIZE sz;
        Win32.GetTextExtentPoint32W(hdc, text, text.Length, &sz);
        return sz.cx;
    }

    private int DrawButton(nint hdc, int x, int y, int h, string text, HitKind kind, ulong tabId, bool disabled, bool selected)
    {
        int w = MeasureButtonWidth(hdc, text);
        DrawButtonFixed(hdc, x, y, w, h, text, kind, tabId, disabled, selected);
        return x + w;
    }

    private void DrawButtonFixed(nint hdc, int x, int y, int w, int h, string text, HitKind kind, ulong tabId, bool disabled, bool selected)
    {
        var rect = new Win32.RECT { Left = x, Top = y, Right = x + w, Bottom = y + h };
        bool hover = !disabled && _hoverKind == kind && _hoverTabId == tabId;
        if (selected)
        {
            FillRoundRect(hdc, rect, S(6), ThemePalette.SelectedBg, ThemePalette.TabActiveBorder);
        }
        else if (hover)
        {
            FillRoundRect(hdc, rect, S(6), ThemePalette.HoverBg, ThemePalette.HoverBg);
        }

        Win32.SelectObject(hdc, _uiFont);
        Win32.SetTextColor(hdc, disabled ? ThemePalette.Dim : ThemePalette.Text);
        Win32.DrawTextW(hdc, text, -1, &rect, Win32.DT_CENTER | Win32.DT_VCENTER | Win32.DT_SINGLELINE | Win32.DT_NOPREFIX);
        _hits.Add(new HitRect(rect, kind, tabId));
    }

    private static void FillRoundRect(nint hdc, Win32.RECT rect, int radius, uint fill, uint border)
    {
        nint brush = Win32.CreateSolidBrush(fill);
        nint pen = Win32.CreatePen(Win32.PS_SOLID, 1, border);
        nint oldBrush = Win32.SelectObject(hdc, brush);
        nint oldPen = Win32.SelectObject(hdc, pen);
        Win32.RoundRect(hdc, rect.Left, rect.Top, rect.Right, rect.Bottom, radius, radius);
        Win32.SelectObject(hdc, oldBrush);
        Win32.SelectObject(hdc, oldPen);
        Win32.DeleteObject(brush);
        Win32.DeleteObject(pen);
    }

    private static void DrawHLine(nint memDc, int x1, int y, int x2, uint color)
    {
        nint pen = Win32.CreatePen(Win32.PS_SOLID, 1, color);
        nint oldPen = Win32.SelectObject(memDc, pen);
        Win32.MoveToEx(memDc, x1, y, null);
        Win32.LineTo(memDc, x2, y);
        Win32.SelectObject(memDc, oldPen);
        Win32.DeleteObject(pen);
    }

    private HitRect? HitTest(int x, int y)
    {
        for (int i = _hits.Count - 1; i >= 0; i--)
        {
            var r = _hits[i].Rect;
            if (x >= r.Left && x < r.Right && y >= r.Top && y < r.Bottom)
            {
                return _hits[i];
            }
        }
        return null;
    }

    private void OnMouseMove(int x, int y)
    {
        if (!_trackingMouse)
        {
            var tme = new Win32.TRACKMOUSEEVENT
            {
                cbSize = (uint)sizeof(Win32.TRACKMOUSEEVENT),
                dwFlags = Win32.TME_LEAVE,
                hwndTrack = _hwnd,
            };
            Win32.TrackMouseEvent(&tme);
            _trackingMouse = true;
        }

        if (_dragTabId != 0 && Math.Abs(x - _dragStartX) > S(4))
        {
            _draggingTab = true;
        }

        var hit = HitTest(x, y);
        if (_draggingTab && hit is { Kind: HitKind.TabSelect } targetHit && targetHit.TabId != _dragTabId)
        {
            ReorderTabs(_dragTabId, targetHit.TabId);
            return;
        }

        HitKind nextKind = hit?.Kind ?? HitKind.None;
        ulong nextTabId = hit?.TabId ?? 0;
        if (nextKind != _hoverKind || nextTabId != _hoverTabId)
        {
            _hoverKind = nextKind;
            _hoverTabId = nextTabId;
            InvalidateChrome();
        }
    }

    private void OnLeftButtonDown(int x, int y)
    {
        var hit = HitTest(x, y);
        if (hit is null) return;

        if (hit.Value.Kind == HitKind.TabSelect)
        {
            _dragTabId = hit.Value.TabId;
            _dragStartX = x;
            _draggingTab = false;
            Win32.SetCapture(_hwnd);
            SelectTab(hit.Value.TabId);
            return;
        }

        var log = _documents.Count > 0 ? ActiveDocument.Log : null;

        switch (hit.Value.Kind)
        {
            case HitKind.BtnNew: NewFile(); break;
            case HitKind.BtnOpen: OpenPicker(false, false); break;
            case HitKind.BtnRecent: ShowRecentMenu(hit.Value.Rect); break;
            case HitKind.BtnLogMenu: ShowLogMenu(hit.Value.Rect); break;
            case HitKind.BtnOpen1251: OpenPicker(true, false); break;
            case HitKind.BtnSave: Save(false); break;
            case HitKind.BtnSaveAs: Save(true); break;
            case HitKind.BtnFindText: OpenSearch(false); break;
            case HitKind.BtnFormatJson: FormatJson(); break;
            case HitKind.BtnWrap: ToggleWrap(); break;
            case HitKind.BtnZoomOut: Zoom(-1.0f); break;
            case HitKind.BtnZoomIn: Zoom(1.0f); break;
            case HitKind.TabClose: CloseTab(hit.Value.TabId); break;
            case HitKind.BtnNewTab: NewFile(); break;
            case HitKind.BtnReopen: Reopen(); break;
            case HitKind.BtnGoToLine: GoToLine(); break;

            case HitKind.SearchPrev: RunEditorSearch(SearchCommand.Previous); break;
            case HitKind.SearchNext: RunEditorSearch(SearchCommand.Next); break;
            case HitKind.SearchRegex: _searchRegex = !_searchRegex; ResetEditorSearch(); break;
            case HitKind.SearchCase: _searchCase = !_searchCase; ResetEditorSearch(); break;
            case HitKind.SearchWhole: _searchWhole = !_searchWhole; ResetEditorSearch(); break;
            case HitKind.SearchAllTabs: _searchAllTabs = !_searchAllTabs; LayoutAndSyncControls(); ResetEditorSearch(); break;
            case HitKind.SearchClose: CloseEditorSearch(); break;
            case HitKind.SearchReplaceOne: RunEditorSearch(SearchCommand.Replace); break;
            case HitKind.SearchReplaceAll: RunEditorSearch(SearchCommand.ReplaceAll); break;

            case HitKind.LogStart: log?.Navigate(LogDestination.Start); break;
            case HitKind.LogPrev: log?.Navigate(LogDestination.Previous); break;
            case HitKind.LogNext: log?.Navigate(LogDestination.Next); break;
            case HitKind.LogEnd: log?.Navigate(LogDestination.End); break;
            case HitKind.LogJumpPercent: log?.JumpPercent(); break;
            case HitKind.LogSearchOpen:
                if (log is not null)
                {
                    log.SearchOpen = !log.SearchOpen;
                    LayoutAndSyncControls();
                    if (log.SearchOpen) Win32.SetFocus(_logSearchEdit);
                    InvalidateChrome();
                }
                break;
            case HitKind.LogAutoRefresh: log?.ToggleAutoRefresh(); break;
            case HitKind.LogFollowEnd: log?.ToggleFollowEnd(); break;
            case HitKind.LogRefreshNow: log?.Navigate(LogDestination.AtOffset(log.Bookmark.Offset)); break;
            case HitKind.LogIndexToggle: log?.ToggleIndex(); break;
            case HitKind.LogHideStart: log?.StartHideStart(); break;
            case HitKind.LogPhysicalTrim: log?.StartPhysicalTrim(); break;
            case HitKind.LogRevealAll: log?.RevealAll(); break;
            case HitKind.LogCancelTrim: log?.CancelTrim(); break;
            case HitKind.LogFindNew: log?.StartSearch(false); break;
            case HitKind.LogFindMore: log?.StartSearch(true); break;
            case HitKind.LogFindCancel: log?.CancelSearch(); break;
            case HitKind.LogFindClose:
                if (log is not null)
                {
                    log.SearchOpen = false;
                    LayoutAndSyncControls();
                    FocusActiveEditor();
                    InvalidateChrome();
                }
                break;
            case HitKind.LogFindRegex:
                if (log is not null) { log.SearchRegex = !log.SearchRegex; log.ResetSearch(); InvalidateChrome(); }
                break;
            case HitKind.LogFindCase:
                if (log is not null) { log.SearchCase = !log.SearchCase; log.ResetSearch(); InvalidateChrome(); }
                break;
            case HitKind.LogFindWhole:
                if (log is not null) { log.SearchWholeWord = !log.SearchWholeWord; log.ResetSearch(); InvalidateChrome(); }
                break;
        }
    }

    private void OnLeftButtonUp()
    {
        if (_dragTabId != 0)
        {
            _dragTabId = 0;
            _draggingTab = false;
            Win32.ReleaseCapture();
        }
    }

    private void OnCommand(nuint wParam)
    {
        if (_suppressEditEvents) return;
        int id = (int)((ulong)wParam & 0xFFFF);
        int code = (int)(((ulong)wParam >> 16) & 0xFFFF);

        if (code == Win32.EN_CHANGE)
        {
            switch (id)
            {
                case IdSearchQueryEdit:
                    _searchQuery = ReadControlText(_searchQueryEdit);
                    ResetEditorSearch();
                    break;
                case IdSearchReplaceEdit:
                    _searchReplacement = ReadControlText(_searchReplaceEdit);
                    ResetEditorSearch();
                    break;
                case IdLogJumpEdit:
                    if (_documents.Count > 0 && ActiveDocument.Log is { } jLog)
                        jLog.JumpPercentInput = ReadControlText(_logJumpEdit);
                    break;
                case IdLogTrimEdit:
                    if (_documents.Count > 0 && ActiveDocument.Log is { } tLog)
                        tLog.TrimPercentInput = ReadControlText(_logTrimEdit);
                    break;
                case IdLogSearchEdit:
                    if (_documents.Count > 0 && ActiveDocument.Log is { } sLog)
                    {
                        sLog.SearchPattern = ReadControlText(_logSearchEdit);
                        sLog.ResetSearch();
                        InvalidateChrome();
                    }
                    break;
            }
        }
        else if (id == IdLogSearchList && code == Win32.LBN_SELCHANGE)
        {
            if (_documents.Count > 0 && ActiveDocument.Log is { } log)
            {
                int sel = (int)Win32.SendMessageW(_logSearchList, Win32.LB_GETCURSEL, 0, 0);
                if (sel >= 0 && sel < log.SearchHits.Count)
                {
                    log.Navigate(LogDestination.AtMatch(log.SearchHits[sel].LineStart));
                }
            }
        }
    }

    private void OnDropFiles(nint hDrop)
    {
        try
        {
            uint count = Win32.DragQueryFileW(hDrop, 0xFFFFFFFF, null, 0);
            var paths = new List<string>((int)count);
            var buf = new char[4096];
            for (uint i = 0; i < count; i++)
            {
                fixed (char* p = buf)
                {
                    uint len = Win32.DragQueryFileW(hDrop, i, p, (uint)buf.Length);
                    if (len > 0) paths.Add(new string(p, 0, (int)len));
                }
            }
            if (paths.Count > 0) OpenPaths([.. paths], false, false);
        }
        finally
        {
            Win32.DragFinish(hDrop);
        }
    }

    private void OnNotify(nint lParam)
    {
        var nm = (Win32.NMHDR*)lParam;
        var doc = _documents.Find(d => d.EditorHwnd == nm->hwndFrom);
        if (doc is null || doc.Log is not null) return;

        switch (nm->code)
        {
            case ScintillaInterop.SCN_SAVEPOINTREACHED:
                doc.Revision++;
                if (doc.Dirty)
                {
                    doc.Dirty = false;
                    InvalidateChrome();
                }
                break;

            case ScintillaInterop.SCN_SAVEPOINTLEFT:
                doc.Revision++;
                if (!doc.Dirty)
                {
                    doc.Dirty = true;
                    InvalidateChrome();
                }
                break;

            case ScintillaInterop.SCN_UPDATEUI:
                if (doc.Id == _activeId) UpdateActiveCursor();
                break;

            case ScintillaInterop.SCN_PAINTED:
                if (doc.PendingEditorReady)
                {
                    doc.PendingEditorReady = false;
                    Measurements.RecordEditorReady();
                }
                break;
        }
    }

    private void OnE2ELogPainted(ulong docId)
    {
        if (!Measurements.IsE2EEnabled || _e2eLogDocId != docId) return;
        _e2eFirstPainted = true;
        var doc = _documents.Find(d => d.Id == docId);
        if (doc?.Log is { } log && !log.Reading && log.Page is not null)
        {
            _e2eAwaitPaint = false;
        }
        AdvanceE2EProbe();
    }

    private void AdvanceE2EProbe()
    {
        if (!Measurements.IsE2EEnabled || _e2eLogDocId == 0) return;
        var doc = _documents.Find(d => d.Id == _e2eLogDocId);
        if (doc?.Log is not { } log) return;

        switch (_e2eStage)
        {
            case 1:
                if (!_e2eFirstPainted || log.Reading || log.Page is null || log.Observation is null ||
                    log.Index.ScannedTo < log.Bookmark.Size || log.IndexProgress is not null)
                {
                    return;
                }
                _e2eStage = 2;
                _e2eAwaitPaint = true;
                _e2eStageTicks = Stopwatch.GetTimestamp();
                log.JumpPercentInput = "50";
                log.JumpPercent();
                break;

            case 2:
                if (_e2eAwaitPaint || log.Reading) return;
                Measurements.RecordMetric("jump_50pct_ms", (long)Stopwatch.GetElapsedTime(_e2eStageTicks).TotalMilliseconds);
                _e2eStage = 3;
                _e2eAwaitPaint = true;
                _e2eStageTicks = Stopwatch.GetTimestamp();
                log.Navigate(LogDestination.End);
                break;

            case 3:
                if (_e2eAwaitPaint || log.Reading) return;
                Measurements.RecordMetric("jump_eof_ms", (long)Stopwatch.GetElapsedTime(_e2eStageTicks).TotalMilliseconds);
                _e2eStage = 4;
                _e2eAwaitPaint = true;
                _e2eStageTicks = Stopwatch.GetTimestamp();
                log.Navigate(LogDestination.Previous);
                break;

            case 4:
                if (_e2eAwaitPaint || log.Reading) return;
                Measurements.RecordMetric("scroll_prev_ms", (long)Stopwatch.GetElapsedTime(_e2eStageTicks).TotalMilliseconds);
                if (Measurements.E2ENeedle is { } needle)
                {
                    _e2eStage = 5;
                    _e2eStageTicks = Stopwatch.GetTimestamp();
                    log.SearchOpen = true;
                    log.SearchPattern = needle;
                    log.StartSearch(false);
                }
                else
                {
                    StartE2ECancelSearch(log);
                }
                break;

            case 5:
                if (log.SearchProgress is not null) return;
                Measurements.RecordMetric("search_eof_ms", (long)Stopwatch.GetElapsedTime(_e2eStageTicks).TotalMilliseconds);
                Measurements.RecordMetric("search_eof_hits", log.SearchHits.Count);
                Measurements.RecordMetric("search_eof_complete", log.SearchComplete ? 1 : 0);
                StartE2ECancelSearch(log);
                break;

            case 6:
                if (log.SearchProgress is not null || log.SearchStopping) return;
                _e2eStage = 7;
                Measurements.RecordMemory("after_ops");
                Measurements.RecordMetric("e2e_done", 1);
                Win32.PostMessageW(_hwnd, Win32.WM_CLOSE, 0, 0);
                break;
        }
    }

    private void StartE2ECancelSearch(LogViewerState log)
    {
        _e2eStage = 6;
        log.SearchOpen = true;
        log.SearchPattern = "__E2E_MISS_PATTERN__";
        log.StartSearch(false);
        ThreadPool.QueueUserWorkItem(_ =>
        {
            Thread.Sleep(25);
            DispatchUi(() =>
            {
                if (_e2eStage == 6 && log.SearchProgress is not null)
                {
                    log.CancelSearch();
                }
            });
        });
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint MainWndProc(nint hwnd, uint msg, nuint wParam, nint lParam)
    {
        var app = _instance;
        if (app is null) return Win32.DefWindowProcW(hwnd, msg, wParam, lParam);

        switch (msg)
        {
            case Win32.WM_SIZE:
                app.LayoutAndSyncControls();
                app.InvalidateChrome();
                return 0;

            case Win32.WM_ERASEBKGND:
                return 1;

            case Win32.WM_PAINT:
                Win32.PAINTSTRUCT ps;
                nint hdc = Win32.BeginPaint(hwnd, &ps);
                Win32.RECT client;
                Win32.GetClientRect(hwnd, &client);
                app.Paint(hdc, client);
                Win32.EndPaint(hwnd, &ps);
                return 0;

            case Win32.WM_GETMINMAXINFO:
                var mmi = (Win32.MINMAXINFO*)lParam;
                mmi->ptMinTrackSize.X = app.S(760);
                mmi->ptMinTrackSize.Y = app.S(350);
                return 0;

            case Win32.WM_DPICHANGED:
                app._dpi = (uint)((ulong)wParam & 0xFFFF);
                app.RebuildFonts();
                foreach (var d in app._documents) d.Viewport?.SetDpi((int)app._dpi);
                var suggested = (Win32.RECT*)lParam;
                Win32.MoveWindow(hwnd, suggested->Left, suggested->Top, suggested->Width, suggested->Height, true);
                app.LayoutAndSyncControls();
                app.InvalidateChrome();
                return 0;

            case Win32.WM_SETFOCUS:
                app.FocusActiveEditor();
                return 0;

            case Win32.WM_MOUSEMOVE:
                app.OnMouseMove(Win32.GetXParam(lParam), Win32.GetYParam(lParam));
                return 0;

            case Win32.WM_MOUSELEAVE:
                app._trackingMouse = false;
                if (app._hoverKind != HitKind.None)
                {
                    app._hoverKind = HitKind.None;
                    app._hoverTabId = 0;
                    app.InvalidateChrome();
                }
                return 0;

            case Win32.WM_LBUTTONDOWN:
                app.OnLeftButtonDown(Win32.GetXParam(lParam), Win32.GetYParam(lParam));
                return 0;

            case Win32.WM_LBUTTONUP:
                app.OnLeftButtonUp();
                return 0;

            case Win32.WM_LBUTTONDBLCLK:
            {
                int x = Win32.GetXParam(lParam);
                int y = Win32.GetYParam(lParam);
                int top = app.S(ToolbarLogicalHeight);
                int bottom = top + app.S(TabBarLogicalHeight);
                if (y >= top && y < bottom && app.HitTest(x, y) is null)
                {
                    app.NewFile();
                    return 0;
                }
                break;
            }

            case Win32.WM_MOUSEWHEEL:
            {
                var pt = new Win32.POINT { X = Win32.GetXParam(lParam), Y = Win32.GetYParam(lParam) };
                Win32.ScreenToClient(hwnd, &pt);
                int delta = Win32.GetWheelDelta(wParam);
                int top = app.S(ToolbarLogicalHeight);
                int bottom = top + app.S(TabBarLogicalHeight);
                if (pt.Y >= top && pt.Y <= bottom)
                {
                    app._tabScrollX = Math.Max(0, app._tabScrollX - delta / 3);
                    app.InvalidateChrome();
                    return 0;
                }
                break;
            }

            case Win32.WM_COMMAND:
                app.OnCommand(wParam);
                return 0;

            case Win32.WM_CTLCOLOREDIT:
                Win32.SetTextColor((nint)wParam, ThemePalette.Text);
                Win32.SetBkColor((nint)wParam, ThemePalette.Bg);
                return app._bgBrush;

            case Win32.WM_CTLCOLORLISTBOX:
                Win32.SetTextColor((nint)wParam, ThemePalette.Text);
                Win32.SetBkColor((nint)wParam, ThemePalette.Bg);
                return app._bgBrush;

            case Win32.WM_TIMER:
                if (wParam == FastTimerId) app.OnFastTimer();
                else if (wParam == SlowTimerId) app.OnSlowTimer();
                return 0;

            case Win32.WM_DROPFILES:
                app.OnDropFiles((nint)wParam);
                return 0;

            case Win32.WM_NOTIFY:
                app.OnNotify(lParam);
                return 0;

            case Win32.WM_APP_DISPATCH:
                app.DrainUiQueue();
                return 0;

            case Win32.WM_APP_IPC:
                app.ProcessIncomingIpc();
                return 0;

            case Win32.WM_ACTIVATEAPP:
                if (wParam != 0)
                {
                    app.CheckDiskStatusTick();
                }
                return 0;

            case Win32.WM_CLOSE:
                if (!app.BeginSessionShutdown()) return 0;
                Win32.DestroyWindow(hwnd);
                return 0;

            case Win32.WM_DESTROY:
                Win32.KillTimer(hwnd, FastTimerId);
                Win32.KillTimer(hwnd, SlowTimerId);
                foreach (var d in app._documents) d.Log?.Dispose();
                app._topChromeBuffer.Dispose();
                app._statusBuffer.Dispose();
                app._sessionJournal?.Dispose();
                Win32.PostQuitMessage(0);
                return 0;
        }

        return Win32.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private static string FileName(string path)
    {
        var name = Path.GetFileName(path);
        return string.IsNullOrEmpty(name) ? path : name;
    }

    private static string DisplayPath(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal))
            return @"\\" + path[@"\\?\UNC\".Length..];
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
            return path[@"\\?\".Length..];
        return path;
    }

    private static string RecentWarning(Exception error) =>
        $"Список недавних файлов недоступен. {error.Message}";
}
