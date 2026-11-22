using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using NetText.Core;
using NetText.Native;

namespace NetText.UI;

internal sealed unsafe partial class MainWindow
{
    private const int ToolbarLogicalHeight = 36;
    private const int TabBarLogicalHeight = 30;
    private const int StatusLogicalHeight = 26;
    private const int EditorSearchLogicalHeight = 160;
    private const int LogToolbarLogicalHeight = 38;
    private const int LogSearchLogicalHeight = 150;
    private const int LogNavigationLogicalHeight = 36;
    private const float MinFontSize = 8.0f;
    private const float MaxFontSize = 48.0f;
    private const int MaxClosedTabs = SessionJournal.MaxClosedTabs;

    private const nuint FastTimerId = 1;
    private const nuint SlowTimerId = 2;
    private const uint WM_MOVE = 0x0003;

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
        FileMenu = 48,
        ToolsMenu = 49,
        Home = 50,
        JsonError = 51,
        SplitVertical = 52,
        SplitHorizontal = 53,
        GroupDivider = 54,
        GroupTab = 55,
        HomePrevious = 56,
        HomeNext = 57,
        SearchList = 58,
        SearchHighlight = 59,
        LogTime = 60,
        SearchToggleReplace = 61,
        ScrollMap = 62,
        ApplyUpdate = 63,
        DismissUpdate = 64,
        EmptyGroup = 65,
        NewInGroup = 66,
        CloseEmptyGroup = 67,
        HorizontalScroll = 68,
        VerticalScroll = 69,
        SearchFindAll = 70,
        SearchRun = 71,
        ThemeMenu = 72,
        WorkspaceBadge = 73,
        LogLevelsButton = 74,
        LogActionsMenu = 75,
        LogEditPage = 76,
        LogSavePage = 77,
        LogCancelPage = 78,
        LogJump50 = 79,
        LogFilterToggle = 80,
        LogFilterAll = 81,
        LogFilterClose = 82,
        HomeSectionToggle = 83,
        HomeRowOpen = 84,
        HomeRowDelete = 85,
        HomeSectionPrev = 86,
        HomeSectionNext = 87,
        OpenUpdateDialog = 88,
        DeferredLoadTab = 89,
    }

    private readonly record struct HitRect(Win32.RECT Rect, HitKind Kind, ulong TabId = 0);

    internal sealed class Document
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
        public byte[]? DraftBytes;
        public bool JsonDisabled;
        public bool UntitledNamePending;
        public long JsonDue;
        public ulong JsonRevision = ulong.MaxValue;
        public ulong JsonResultRevision = ulong.MaxValue;
        public string? JsonResult;
        public int JsonErrorPosition = -1;
        public bool JsonMarked;
        public string? JsonError => !JsonDisabled && JsonResultRevision == Revision ? JsonResult : null;
        public int Group;
        public DateTimeOffset? LastWorked = DateTimeOffset.UtcNow;
        public ulong? StoredLineCount;
        public LogViewerState? Log;
        public LogViewport? Viewport;
        public bool NativeVerticalScroll = true;
        public bool HorizontalScroll;
        public (int First, int Count)? MeasuredLines;
        public int MeasuredWidth;
        public ScrollRange LastVerticalRange;
        public ScrollRange LastHorizontalRange;
        public LogDestination? ScrollDestination;
        public string? CustomTitle;
        public bool AutoRefresh = true;
        public bool EditPages;
        public bool IsConsole;
        public ConsoleCaptureSession? ConsoleSession;
        public bool IdleUnloaded;
        public bool CheckingSession;
        public SessionTab? DeferredState;
        public EditorFileWatch? FileWatch;
        public nint FilterPreviewHwnd;
        public LogLevel ActiveLogFilter = LogLevel.All;
        public int[]? FilterSourceLines;
        public HashSet<string>? VisitedLinks;
        public int LastSelectedChars;
        public int LastCaretCount = 1;
        public bool IsExternalDraft;
        public string? RecoveryStatus;
    }

    private static MainWindow? _creatingWindow;

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
    private float _fontSize = 12.0f;
    private int _tabScrollX;
    private int _fastTicks;
    private int _slowTicks;
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
        try
        {
            var loaded = EditorSettings.Load();
            var migrated = loaded.MigrateLegacyUpdateChoice();
            if (!Equals(migrated, loaded))
            {
                loaded = migrated;
                try { loaded.Save(); } catch { }
            }
            _settings = loaded;
        }
        catch (Exception error) { _error = $"Не удалось прочитать настройки. {error.Message}"; }

        ThemePalette.Apply(_settings.Theme);
        _fontSize = Math.Clamp(_settings.FontSize, MinFontSize, MaxFontSize);
        _wrap = _settings.WordWrap;

        var (recent, recentError) = RecentFiles.Load();
        _recent = recent;
        if (recentError is not null)
        {
            _error = RecentWarning(recentError);
        }

        string? startupWorkspaceId = WorkspaceCatalogService.DefaultWorkspaceId;
        bool startEmpty = false;
        if (startupPaths.Length == 0)
        {
            (startupWorkspaceId, startEmpty) = ResolveStartupWorkspace();
        }

        _hInstance = Win32.GetModuleHandleW(null);
        var recovered = InitializeWorkspaceState(
            startupWorkspaceId,
            WorkspaceCatalogService.PrimaryWindowId,
            startEmpty,
            isDetached: false);
        WorkspaceSession.Current.RegisterWindow(this);
        CreateWindowAndShow(startupPaths, recovered);
        UpdateWindowTitle();
        CheckForUpdates();
    }

    private (string WorkspaceId, bool StartEmpty) ResolveStartupWorkspace()
    {
        if (_settings.WorkspaceStartupMode == WorkspaceStartupMode.Empty)
        {
            return (WorkspaceCatalogService.DefaultWorkspaceId, true);
        }

        try
        {
            var catalog = WorkspaceSession.Current.EnsureCatalog();
            if (_settings.WorkspaceStartupMode == WorkspaceStartupMode.Specific &&
                !string.IsNullOrWhiteSpace(_settings.StartupWorkspaceId) &&
                catalog.Workspaces.Any(w => w.Id == _settings.StartupWorkspaceId))
            {
                return (_settings.StartupWorkspaceId!, false);
            }

            if (catalog.WindowAssignments.TryGetValue(WorkspaceCatalogService.PrimaryWindowId, out var assigned) &&
                catalog.Workspaces.Any(w => w.Id == assigned))
            {
                return (assigned, false);
            }

            var latest = catalog.Workspaces
                .OrderByDescending(w => w.LastUsedMs)
                .FirstOrDefault(w => w.LastUsedMs > 0);
            if (latest is not null)
            {
                return (latest.Id, false);
            }
        }
        catch
        {
        }

        return (WorkspaceCatalogService.DefaultWorkspaceId, false);
    }

    public nint Hwnd => _hwnd;

    public bool TryHandleKeyMessage(nint targetHwnd, int vk)
    {
        if (_showHome && vk == Win32.VK_RETURN && (targetHwnd == _historyQuery || targetHwnd == _historyList))
        {
            OpenHistorySelection();
            return true;
        }
        if (targetHwnd == _searchQueryEdit)
        {
            if (vk == Win32.VK_RETURN)
            {
                RunEditorSearch(Win32.IsKeyDown(Win32.VK_SHIFT) ? SearchCommand.Previous : SearchCommand.List);
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
        else if (targetHwnd == _textSearchList && vk == Win32.VK_RETURN)
        {
            ChooseTextSearchResult();
            return true;
        }
        else if (HasDocument && targetHwnd == _logJumpEdit && vk == Win32.VK_RETURN)
        {
            ActiveDocument.Log?.JumpPercent();
            return true;
        }
        else if (HasDocument && targetHwnd == _logSearchEdit)
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
        bool alt = Win32.IsKeyDown(VK_MENU);

        if (vk == Win32.VK_ESCAPE && !ctrl && !shift && !alt)
        {
            if (CancelActiveTabDrag()) return true;
            if (HandleMultiCaretKey(Win32.VK_ESCAPE, ctrl, shift, alt)) return true;
            if (_searchOpen && !_showHome && HasDocument && ActiveDocument.Log is null)
            {
                CloseEditorSearch();
                return true;
            }
        }

        if (HasDocument && ActiveDocument.Log is { } activeLog)
        {
            if (ctrl && !shift && !alt && vk == 'X' && activeLog.IsEditingPage && HandlePageEditCut(ActiveDocument))
            {
                return true;
            }
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
            if (vk == Win32.VK_F3 && HasDocument && !_showHome)
            {
                RunEditorSearch(shift ? SearchCommand.Previous : SearchCommand.Next);
                return true;
            }
            return false;
        }

        if (HandleMultiCaretKey(vk, ctrl, shift, alt))
        {
            return true;
        }

        if (alt) return false;

        switch (vk)
        {
            case 'N' when !shift:
                NewFile();
                return true;
            case 'O' when !shift:
                OpenPicker(false, false);
                return true;
            case 'S' when !shift:
                if (HasDocument && !_showHome && ActiveDocument.Log is { IsEditingPage: true })
                {
                    SaveLogPageEdit(ActiveDocument);
                }
                else
                {
                    Save(false);
                }
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
                FormatActiveDocumentJson();
                return true;
            case 'F' when !shift:
                OpenSearch(false);
                return true;
            case 'H' when !shift:
                OpenSearch(true);
                return true;
            case '0' when !shift:
            case 0x60 when !shift:
                Zoom(12.0f - _fontSize);
                _settings = _settings with { FontSize = _fontSize };
                try { _settings.Save(); } catch { }
                InvalidateChrome();
                return true;
            case Win32.VK_OEM_PLUS:
            case Win32.VK_ADD:
                Zoom(1.0f);
                InvalidateChrome();
                return true;
            case Win32.VK_OEM_MINUS:
            case Win32.VK_SUBTRACT:
                Zoom(-1.0f);
                InvalidateChrome();
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

        _creatingWindow = this;
        try
        {
            _hwnd = Win32.CreateWindowExW(
                0,
                "NetTextMainWindow",
                "NetText",
                Win32.WS_OVERLAPPEDWINDOW | Win32.WS_CLIPCHILDREN,
                initX, initY, initW, initH,
                0, 0, _hInstance, null);
        }
        finally
        {
            _creatingWindow = null;
        }

        if (_hwnd == 0)
        {
            throw new InvalidOperationException("Не удалось открыть окно NetText");
        }

        _dpi = Win32.GetDpiForWindow(_hwnd);
        if (_dpi == 0) _dpi = 96;
        UpdateWindowIcons();

        int scaledW = S(initW);
        int scaledH = S(initH);
        Win32.MoveWindow(_hwnd, Math.Max(0, (screenW - scaledW) / 2), Math.Max(0, (screenH - scaledH) / 2), scaledW, scaledH, false);

        Win32.EnableDarkTitleBar(_hwnd);
        Win32.SetWindowBorderColor(_hwnd, ThemePalette.Chrome);
        Win32.DragAcceptFiles(_hwnd, true);
        RebuildFonts();
        CreateChildInputControls();
        CreateSearchList();
        CreateHomeControls();
        UpdateHistory();

        if (recovered is not null && !_startEmpty)
        {
            RestoreSession(recovered);
        }
        else if (startupPaths.Length == 0) _showHome = true;
        if (_showHome) UpdateHistory();

        if (startupPaths.Length > 0)
        {
            Measurements.DumpMemoryProfile("before-open-paths");
            OpenPaths(startupPaths, false, false);
        }

        Win32.ShowWindow(_hwnd, recovered?.State.Window.Maximized == true ? (int)Win32.SW_MAXIMIZE : Win32.SW_SHOW);
        Measurements.RecordWindowVisible();
        Win32.UpdateWindow(_hwnd);
        TrackNormalWindowBounds();
        LayoutAndSyncControls();
        FocusActiveEditor();

        (WorkspaceSession.Current.SingleInstance ?? _singleInstance)?.SetWakeupCallback(() =>
        {
            var target = WorkspaceSession.Current.LastActiveWindow ?? WorkspaceSession.Current.Windows.FirstOrDefault() ?? this;
            if (target._hwnd != 0)
            {
                Win32.PostMessageW(target._hwnd, Win32.WM_APP_IPC, 0, 0);
            }
        });
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
        if (_historyQuery != 0) Win32.SendMessageW(_historyQuery, Win32.WM_SETFONT, (nuint)_uiFont, 1);
        if (_historyList != 0) Win32.SendMessageW(_historyList, Win32.WM_SETFONT, (nuint)_uiFont, 1);
        if (_textSearchList != 0) Win32.SendMessageW(_textSearchList, Win32.WM_SETFONT, (nuint)_smallFont, 1);
    }

    private bool HasDocument => _activeId != 0;

    private Document ActiveDocument =>
        _documents.Find(d => d.Id == _activeId) ?? throw new InvalidOperationException("active tab must exist");

    private void AddDocument(NativeOpenedFile? opened)
    {
        ulong id = _nextId++;
        var target = opened?.Target;
        var format = opened?.Format ?? TextFormat.Default;
        var name = target is null ? "Без имени" : FileName(target.Path);

        var editorHwnd = ScintillaInterop.CreateEditorControl(_hwnd, _hInstance, (int)MathF.Round(_fontSize), _wrap);
        InstallEditorSubclass(editorHwnd);

        var doc = new Document
        {
            Id = id,
            Group = _activeGroup,
            Name = name,
            File = target,
            Format = format,
            EditorHwnd = editorHwnd,
            PendingEditorReady = opened is not null,
            Dirty = false,
        };

        bool attachedShared = TryAttachSharedScintillaDocument(doc);
        if (!attachedShared)
        {
            if (opened is not null)
            {
                ScintillaInterop.SetTextFromOpened(editorHwnd, opened);
            }
            ScintillaInterop.ApplyLanguage(editorHwnd, target?.Path, (int)MathF.Round(_fontSize));
            if (opened is not null)
            {
                ScintillaInterop.SetSavePoint(editorHwnd);
            }
        }

        _documents.Add(doc);
        AttachFileWatch(doc);
        UpdateEditorLinksAndLineNumbers(doc);
        SelectTab(id);
        if (doc.PendingEditorReady)
        {
            Win32.InvalidateRect(editorHwnd, null, false);
        }
    }

    private void AddLogDocument(FileTarget target, bool windows1251, LogBookmark? restoredBookmark, bool restored, bool editPages = false)
    {
        ulong id = _nextId++;
        var bookmark = restoredBookmark ?? new LogBookmark();
        if (editPages)
        {
            bookmark.EditPages = true;
            bookmark.AutoRefresh = false;
            bookmark.FollowEnd = false;
        }
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
        viewport.Focused = () =>
        {
            if (!_showHome && _activeId != id) SelectTab(id);
        };
        viewport.Scrolled = () =>
        {
            var document = _documents.Find(d => d.Id == id);
            if (document is not null) SyncDocumentScroll(document);
            if (_activeId == id)
            {
                Win32.RECT status;
                Win32.GetClientRect(_hwnd, &status);
                status.Top = Math.Max(0, status.Bottom - S(StatusLogicalHeight));
                Win32.InvalidateRect(_hwnd, &status, false);
            }
        };

        state = new LogViewerState(
            target.Path,
            windows1251,
            bookmark,
            DispatchUi,
            () =>
            {
                if (state is not null)
                {
                    viewport.SetSearchHighlight(state.SearchOpen ? state.HighlightRegex : null);
                    viewport.SetFirstLine(state.PageLine, state.SearchOpen && state.SelectedSearchOffset == state.Bookmark.Offset);
                    UpdateLogMinimap(id, state);
                    if (state.EditsPagesByDefault && !state.IsEditingPage && !state.Reading && !state.IsFiltered &&
                        state.Page is not null && !_pageEditors.ContainsKey(id))
                    {
                        var pageDoc = _documents.Find(d => d.Id == id);
                        if (pageDoc is not null)
                        {
                            EnterLogPageEdit(pageDoc);
                        }
                    }
                }
                if (_activeId == id)
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
            (page, line, scrollToBottom) =>
            {
                viewport.SetPage(page, state!.Encoding, line, scrollToBottom, state.SelectedSearchOffset == state.Bookmark.Offset);
            },
            () =>
            {
                Win32.RECT rc;
                Win32.GetClientRect(editorHwnd, &rc);
                return rc.Height;
            },
            plan =>
            {
                string detail = $"Из «{FileName(target.Path)}» будет удалено строк: {plan.RemovedLines:N0}, байт: {plan.RemovableBytes:N0} из {plan.OriginalSize:N0}. Перед записью NetText сохранит полную резервную копию рядом с логом.";
                return Win32.MessageBoxW(_hwnd, detail, "Удалить начало исходного файла?", Win32.MB_YESNO | Win32.MB_ICONWARNING) == Win32.IDYES;
            });

        var doc = new Document
        {
            Id = id,
            Group = _activeGroup,
            Name = FileName(target.Path),
            File = target,
            Format = new TextFormat(windows1251 ? TextEncoding.Windows1251 : TextEncoding.Utf8, LineEndings.Lf),
            EditorHwnd = editorHwnd,
            Log = state,
            Viewport = viewport,
            AutoRefresh = bookmark.AutoRefresh,
            EditPages = bookmark.EditPages,
        };
        _documents.Add(doc);
        AttachFileWatch(doc);
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

        int previousGroup = _activeGroup;
        if (_activeId != id && !_restoringSession) target.LastWorked = DateTimeOffset.UtcNow;
        _activeId = id;
        _activeGroup = target.Group;
        _groupTabs[target.Group] = id;
        _showHome = false;
        if (previousGroup != _activeGroup) _tabScrollX = 0;
        if (target.Log is not null)
        {
            _searchOpen = false;
        }

        if (!_restoringSession && (target.IdleUnloaded || target.Placeholder))
        {
            RestoreDeferredOrIdleTab(target);
        }

        EnsureTabVisible(id);
        SyncLogInputsAndHits();
        LayoutAndSyncControls();
        FocusActiveEditor();
        InvalidateChrome();
    }

    private void FocusActiveEditor()
    {
        if (HasDocument && !_showHome)
        {
            var doc = ActiveDocument;
            nint target = _pageEditors.TryGetValue(doc.Id, out nint pageHwnd) && pageHwnd != 0
                ? pageHwnd
                : doc.FilterPreviewHwnd != 0
                    ? doc.FilterPreviewHwnd
                    : doc.EditorHwnd;
            if (target != 0 && Win32.GetFocus() != target)
            {
                Win32.SetFocus(target);
            }
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
        if (!ConfirmLeaveDirtyPageEdit(doc))
        {
            return;
        }

        doc.LastWorked = DateTimeOffset.UtcNow;
        var (tab, draftBytes) = SnapshotDocument(doc, includeDraft: doc.File is null);
        tab = tab with
        {
            ClosedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            CustomTitle = doc.CustomTitle,
            AutoRefresh = doc.AutoRefresh,
            EditPages = doc.EditPages,
        };
        if (doc.File is { } file)
        {
            var entry = RecentFile.From(file.Path, doc.Format.Encoding, doc.Log is not null, doc.EditPages) with
            {
                Position = tab,
                Lines = tab.LineCount,
            };
            try { _recent.Record([entry]); }
            catch (Exception error) { _error = RecentWarning(error); }
        }

        bool wasActive = id == _activeId;
        if (wasActive) _activeId = 0;
        _documents.RemoveAt(index);
        RemoveEmptyGroup(doc.Group);
        DisposeDocumentResources(doc);

        if (!SessionJournal.IsKnownEmptyUntitledDraft(tab, draftBytes))
        {
            if (_closed.Count >= MaxClosedTabs)
            {
                _closed.RemoveAt(0);
            }
            _closed.Add((tab, draftBytes));
        }

        if (_documents.Count == 0)
        {
            ToggleHome();
        }
        else if (wasActive)
        {
            ulong nextId = _documents[Math.Max(0, index - 1)].Id;
            SelectTab(nextId);
        }
        else
        {
            LayoutAndSyncControls();
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

    private string ClosedTabLabel(SessionTab tab, byte[]? draftBytes)
    {
        if (tab.File is { } file)
        {
            return DisplayPath(file.Path);
        }

        if (draftBytes is null || draftBytes.Length == 0)
        {
            return "Без имени";
        }

        int firstLine = draftBytes.AsSpan().IndexOfAny((byte)'\r', (byte)'\n');
        return _settings.UntitledName(firstLine < 0 ? draftBytes : draftBytes.AsSpan(0, firstLine));
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
            SyncDocumentScroll(doc);
        }
        LayoutAndSyncControls();
        InvalidateScrollbars();
        InvalidateChrome();
    }

    private void Zoom(float delta)
    {
        if (!HasDocument || _showHome) return;
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
                doc.MeasuredLines = null;
                doc.MeasuredWidth = 0;
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
        if (!HasDocument || _showHome) return;
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

    private void OpenSearch(bool replace)
    {
        if (!HasDocument || _showHome) return;
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
        if (replace) _replaceOpen = true;
        LayoutAndSyncControls();
        var focusTarget = replace ? _searchReplaceEdit : _searchQueryEdit;
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
        ClearSearchResults();
        LayoutAndSyncControls();
        FocusActiveEditor();
        InvalidateChrome();
    }

    private void ResetEditorSearch()
    {
        _searchGen++;
        _searchInFlight = false;
        _lastSearchHit = null;
        ClearSearchResults();
        _searchStatus = _searchAllTabs
            ? "Поиск по всем вкладкам. Замена доступна в текущей вкладке."
            : "Enter – найти совпадения";
        InvalidateChrome();
    }

    private void RunEditorSearch(SearchCommand command)
    {
        if (!HasDocument || _busy is not null || _searchInFlight) return;
        var active = ActiveDocument;
        if (active.Log is not null)
        {
            return;
        }
        if (ScintillaInterop.GetByteLength(active.EditorHwnd) > EditorSearch.MaxDocumentBytes)
        {
            _searchStatus = "Поиск и замена доступны в документах до 64 МиБ.";
            InvalidateChrome();
            return;
        }

        if ((command is SearchCommand.Replace or SearchCommand.ReplaceAll) &&
            (_busy is not null || _searchInFlight || active.Placeholder))
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
            if (d.Log is null && (d.Id == active.Id || (query.AllTabs && command is not (SearchCommand.Replace or SearchCommand.ReplaceAll))))
            {
                if (ScintillaInterop.GetByteLength(d.EditorHwnd) > EditorSearch.MaxDocumentBytes) continue;
                var (buf, len) = ScintillaInterop.CopyUtf8BytesPooled(d.EditorHwnd);
                snapshots.Add((d.Id, d.Revision, new SearchUtf8Source(d.Id, buf, len)));
            }
        }

        var revisions = snapshots.Select(item => (item.Id, item.Rev)).ToArray();
        SearchNavigationCursor? resume = _pausedNavigation is { } paused && paused.Command == command &&
            paused.Query == query && paused.Origin == origin && paused.Sources.SequenceEqual(revisions) ? paused.Cursor : null;
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

                ok = command is SearchCommand.Next or SearchCommand.Previous
                    ? EditorSearch.TryNavigateUtf8(query, command == SearchCommand.Previous, srcList, origin, skipCurrent,
                        resume, out outcome, out error)
                    : EditorSearch.TryRunUtf8(
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

                bool stale = _busy is not null || _activeId != active.Id || _showHome;
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
                    case SearchOutcome.Navigated navigation:
                        ApplyNavigation(command, query, origin, navigation, revisions);
                        break;
                    case SearchOutcome.Found found:
                        if (found.Hit is SearchHit hit && command != SearchCommand.List)
                        {
                            SelectTab(hit.Tab);
                            var targetDoc = _documents.Find(d => d.Id == hit.Tab);
                            if (targetDoc is not null)
                            {
                                ScintillaInterop.SetSelection(targetDoc.EditorHwnd, hit.Range.Start, hit.Range.End, false);
                                UpdateActiveCursor();
                            }
                            _lastSearchHit = (query, hit);
                        }
                        else
                        {
                            _lastSearchHit = null;
                        }
                        ShowSearchResults(query, found, snapshots);
                        break;

                    case SearchOutcome.Replaced rep:
                        ClearSearchResults();
                        ScintillaInterop.ReplaceByteRange(active.EditorHwnd, rep.Range.Start, rep.Range.End, rep.Text);
                        active.Dirty = true;
                        active.Revision++;
                        _lastSearchHit = null;
                        _searchStatus = $"Заменено: {rep.Count}";
                        FocusActiveEditor();
                        break;

                    case SearchOutcome.ReplacedUtf8 repUtf8:
                        ClearSearchResults();
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

    private void OpenPaths(string[] paths, bool windows1251, bool viewAsLog, SessionTab? closedTab = null, bool autoDetectLog = true)
    {
        if (paths.Length == 0 || _busy is not null)
        {
            return;
        }

        var recentCopy = _recent.Clone();
        var history = recentCopy.History.GroupBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var largeChoices = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        if (!viewAsLog && closedTab is null)
        {
            foreach (var rawPath in paths)
            {
                try
                {
                    var fullPath = Path.GetFullPath(rawPath);
                    var version = FileVersion.At(fullPath);
                    if (version is { } v && v.Bytes > LogLevels.LargeFilePromptBytes)
                    {
                        bool? choice = _settings.OpenLargeFilesAsLog ?? ChooseLargeFileModeDialog(fullPath, v.Bytes);
                        if (choice is null) continue;
                        largeChoices[fullPath] = choice.Value;
                    }
                }
                catch
                {
                }
            }
        }

        _busy = "Открытие…";
        _error = null;
        InvalidateChrome();

        ThreadPool.QueueUserWorkItem(_ =>
        {
            var loadedItems = new List<(NativeOpenedFile? Editor, FileTarget? LogTarget, bool EditPages)>();
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

                    bool openAsLog;
                    bool editPages;
                    if (closedTab is not null)
                    {
                        openAsLog = closedTab.Log is not null || version.Bytes > Files.MaxEditorBytes;
                        editPages = closedTab.EditPages ?? closedTab.Log?.EditPages ?? false;
                    }
                    else if (viewAsLog)
                    {
                        openAsLog = true;
                        editPages = false;
                    }
                    else if (version.Bytes > LogLevels.LargeFilePromptBytes)
                    {
                        if (!largeChoices.TryGetValue(fullPath, out bool asLog))
                        {
                            continue;
                        }
                        openAsLog = true;
                        editPages = !asLog;
                    }
                    else
                    {
                        var recentItem = history.GetValueOrDefault(fullPath);
                        bool detectedLog = autoDetectLog &&
                            (LogLevels.IsLogPath(fullPath) || string.Equals(Path.GetExtension(fullPath), ".txt", StringComparison.OrdinalIgnoreCase)) &&
                            ((recentItem is { ViewAsLog: true } && recentItem.EditPages != true) ||
                             LogLevels.HasLogRecords(fullPath, windows1251 ? TextEncoding.Windows1251 : null));

                        if (detectedLog)
                        {
                            openAsLog = true;
                            editPages = false;
                        }
                        else if (version.Bytes > Files.MaxEditorBytes)
                        {
                            openAsLog = true;
                            editPages = true;
                        }
                        else
                        {
                            openAsLog = false;
                            editPages = false;
                        }
                    }

                    if (openAsLog)
                    {
                        var target = new FileTarget(fullPath, version);
                        loadedItems.Add((null, target, editPages));
                        recentBatch.Add(RecentFile.From(
                            fullPath,
                            windows1251 ? TextEncoding.Windows1251 : TextEncoding.Utf8,
                            viewAsLog: true,
                            editPages: editPages));
                    }
                    else
                    {
                        var opened = Files.OpenForEditor(fullPath, windows1251);
                        loadedItems.Add((opened, null, false));
                        recentBatch.Add(RecentFile.From(opened.Target.Path, opened.Format.Encoding, viewAsLog: false, editPages: false) with
                        {
                            Lines = opened.LineCount ?? (ulong)ScintillaInterop.CountTextLines(opened.Span),
                        });
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
                    var path = item.LogTarget?.Path ?? item.Editor?.Target.Path;
                    var restore = closedTab ?? (path is null ? null : history.GetValueOrDefault(path)?.Position);
                    if (item.LogTarget is { } logTarget)
                    {
                        ShowLog(logTarget, windows1251, restore?.Log?.Copy(), restore?.Log is not null, item.EditPages);
                    }
                    else if (item.Editor is { } opened)
                    {
                        using (opened)
                        {
                            ShowOpened(opened, restore, closedTab is not null);
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

                if (_documents.Count == 0) ToggleHome();

                InvalidateChrome();
            });
        });
    }

    private void ShowOpened(NativeOpenedFile opened, SessionTab? restore, bool reopeningClosed)
    {
        var existing = _documents.Find(d =>
            d.Log is null &&
            d.File is not null &&
            string.Equals(d.File.Path, opened.Target.Path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            bool replaced = Files.ShouldReplaceOpenedDocument(
                existing.Dirty,
                existing.Placeholder,
                existing.File,
                opened.Target,
                existing.Format.Encoding,
                opened.Format.Encoding);
            if (replaced)
            {
                ScintillaInterop.SetTextFromOpened(existing.EditorHwnd, opened);
                ScintillaInterop.ApplyLanguage(existing.EditorHwnd, opened.Target.Path, (int)MathF.Round(_fontSize));
                ScintillaInterop.SetSavePoint(existing.EditorHwnd);
                existing.File = opened.Target;
                existing.Format = opened.Format;
                existing.Placeholder = false;
                existing.PendingEditorReady = true;
                existing.DiskStatus = DiskStatusKind.Current;
                existing.Dirty = false;
                AttachFileWatch(existing);
                UpdateEditorLinksAndLineNumbers(existing);
                Win32.InvalidateRect(existing.EditorHwnd, null, false);
            }
            SelectTab(existing.Id);
            if (restore is not null && !existing.Dirty && (replaced || reopeningClosed))
                ApplyStoredTextPosition(existing, restore);
            return;
        }

        AddDocument(opened);
        if (restore is not null) ApplyStoredTextPosition(_documents[^1], restore);
    }

    private static void ApplyStoredTextPosition(Document doc, SessionTab restore)
    {
        doc.JsonDisabled = restore.JsonDisabled;
        ScintillaInterop.SetSelection(doc.EditorHwnd, restore.Selection.Start, restore.Selection.End, restore.Reversed);
        if (restore.Scroll.Length >= 2)
        {
            ScintillaInterop.SetScroll(doc.EditorHwnd, (int)restore.Scroll[0], (int)restore.Scroll[1]);
        }
    }

    private void ShowLog(FileTarget target, bool windows1251, LogBookmark? restoredBookmark, bool restored, bool editPages = false)
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

        AddLogDocument(target, windows1251, restoredBookmark, restored, editPages);
    }

    private void Save(bool saveAs)
    {
        if (_busy is not null || !HasDocument || _showHome) return;
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
        string? pickedPath = PickSavePath(_hwnd, suggested, initialDir, "Сохранить как");
        if (pickedPath is null) return;
        try { WriteFile(doc.Id, FileTarget.FromPicker(pickedPath)); }
        catch (Exception ex)
        {
            _error = $"Не удалось выбрать файл. {ex.Message}";
            InvalidateChrome();
        }
    }

    private static string? PickSavePath(nint owner, string suggested, string initialDir, string title)
    {
        var buffer = new char[4096];
        suggested.CopyTo(buffer);
        var filter = "Все файлы\0*.*\0Текстовые файлы (*.txt)\0*.txt\0\0";

        fixed (char* pBuf = buffer)
        fixed (char* pFilter = filter)
        fixed (char* pDir = string.IsNullOrEmpty(initialDir) ? null : initialDir)
        fixed (char* pTitle = title)
        {
            var ofn = new Win32.OPENFILENAMEW
            {
                lStructSize = (uint)sizeof(Win32.OPENFILENAMEW),
                hwndOwner = owner,
                lpstrFilter = pFilter,
                nFilterIndex = 1,
                lpstrFile = pBuf,
                nMaxFile = (uint)buffer.Length,
                lpstrInitialDir = pDir,
                lpstrTitle = pTitle,
                Flags = Win32.OFN_EXPLORER | Win32.OFN_PATHMUSTEXIST | Win32.OFN_OVERWRITEPROMPT | Win32.OFN_NOCHANGEDIR,
            };

            if (!Win32.GetSaveFileNameW(&ofn)) return null;
            int end = Array.IndexOf(buffer, '\0');
            return end >= 0 ? new string(buffer, 0, end) : new string(buffer);
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
        ulong revision = doc.Revision;
        ulong lineCount = (ulong)ScintillaInterop.GetLineCount(doc.EditorHwnd);
        var settings = _settings;
        var format = doc.Format;
        var recentCopy = _recent.Clone();
        _busy = "Сохранение…";
        InvalidateChrome();

        ThreadPool.QueueUserWorkItem(_ =>
        {
            FileTarget? savedTarget = null;
            Exception? saveError = null;
            Exception? historyError = null;
            Exception? versionError = null;

            try
            {
                var icon = DesktopIcon.Capture(target);
                savedTarget = Files.Save(target, format, snapshot);
                if (icon is { } position) DesktopIcon.Restore(savedTarget, position);
                try { new FileVersions().Capture(savedTarget, format.Encoding, settings); }
                catch (Exception error) { versionError = error; }
                try
                {
                    recentCopy.Record([RecentFile.From(savedTarget.Path, format.Encoding, false) with { Lines = lineCount }]);
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
                    else if (versionError is not null) _error = $"Файл сохранён. Не удалось записать версию. {versionError.Message}";

                    var targetDoc = _documents.Find(d => d.Id == id);
                    if (targetDoc is not null)
                    {
                        bool pathChanged = targetDoc.File is null ||
                            !string.Equals(targetDoc.File.Path, savedTarget.Path, StringComparison.OrdinalIgnoreCase);
                        targetDoc.Name = FileName(savedTarget.Path);
                        targetDoc.File = savedTarget;
                        targetDoc.Placeholder = false;
                        targetDoc.DiskStatus = DiskStatusKind.Current;
                        if (targetDoc.Revision == revision)
                        {
                            ScintillaInterop.SetSavePoint(targetDoc.EditorHwnd);
                            targetDoc.Dirty = false;
                        }
                        else targetDoc.Dirty = true;
                        if (pathChanged)
                        {
                            ScintillaInterop.ApplyLanguage(targetDoc.EditorHwnd, savedTarget.Path, (int)MathF.Round(_fontSize));
                            AttachFileWatch(targetDoc);
                        }
                        PropagateSharedDocumentSaved(targetDoc);
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
                    var file = tab.File is { } target ? _recent.History.FirstOrDefault(item =>
                        string.Equals(item.Path, target.Path, StringComparison.OrdinalIgnoreCase)) : null;
                    string label = ClosedTabLabel(tab, draft) + HistoryMetadata(tab.LineCount ?? file?.Lines,
                        tab.LastWorked ?? (file is null ? null : LastWorked(file)));
                    Win32.AppendMenuW(closedMenu, Win32.MF_STRING, (nuint)(ClosedCmdBase + i), label);
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
                    Win32.AppendMenuW(menu, Win32.MF_STRING, (nuint)(i + 1),
                        $"{name} – {DisplayPath(dir)}" + HistoryMetadata(entry.Lines, LastWorked(entry)));
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
        if (_busy is not null) return;
        WorkspaceSession.Current.RouteIncomingIpc(WorkspaceSession.Current.SingleInstance ?? _singleInstance);
    }

    private void OnFastTimer()
    {
        _fastTicks++;

        ProcessIncomingIpc();
        TickJsonValidation();
        foreach (var doc in _documents) FlushLogScroll(doc);
        if (_showHome && _fastTicks % 80 == 0) UpdateHistory();

        if (HasDocument && ActiveDocument.Log is { } activeLog)
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
        _slowTicks++;
        CheckDiskStatusTick();
        QueueSessionTick();
        if (_slowTicks % 60 == 0)
        {
            RunIdleUnloadPass();
            RunDraftArchivePass();
            CheckUpdatesIfHourlyDue();
        }
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

    private (SessionTab Tab, byte[]? DraftBytes) SnapshotDocument(Document doc, bool includeDraft)
    {
        if (doc.IdleUnloaded && doc.DeferredState is { } deferred)
        {
            byte[]? deferredDraft = includeDraft && deferred.DraftRevision is not null
                ? TryReadSessionDraftBytes(doc, deferred)
                : null;
            return (
                deferred with
                {
                    Group = doc.Group,
                    CustomTitle = doc.CustomTitle,
                    AutoRefresh = doc.AutoRefresh,
                    EditPages = doc.EditPages,
                    LastWorked = doc.LastWorked ?? deferred.LastWorked,
                },
                deferredDraft);
        }

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
            var bookmark = log.Bookmark.Copy();
            if (doc.Viewport?.VisibleLogOffset is { } visible) bookmark.Offset = visible;
            return (
                new SessionTab(
                    doc.Id,
                    sFile,
                    sFmt,
                    new SessionSelection(0, 0),
                    false,
                    [0f, 0f],
                    null,
                    bookmark, doc.Group, false, KnownLineCount(doc), doc.LastWorked)
                {
                    CustomTitle = doc.CustomTitle,
                    AutoRefresh = doc.AutoRefresh,
                    EditPages = doc.EditPages,
                },
                null);
        }

        var (selStart, selEnd, reversed) = ScintillaInterop.GetSelection(doc.EditorHwnd);
        var (sx, sy) = ScintillaInterop.GetScroll(doc.EditorHwnd);
        if (includeDraft && doc.Dirty)
        {
            if (doc.DraftBytes is null)
            {
                doc.DraftRevision = (doc.DraftRevision ?? 0) + 1;
                doc.DraftBytes = ScintillaInterop.CopyUtf8Bytes(doc.EditorHwnd);
            }
            return (
                new SessionTab(
                    doc.Id,
                    sFile,
                    sFmt,
                    new SessionSelection(selStart, selEnd),
                    reversed,
                    [sx, sy],
                    doc.DraftRevision,
                    null, doc.Group, doc.JsonDisabled, KnownLineCount(doc), doc.LastWorked)
                {
                    CustomTitle = doc.CustomTitle,
                    AutoRefresh = doc.AutoRefresh,
                    EditPages = doc.EditPages,
                },
                doc.DraftBytes);
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
                null, doc.Group, doc.JsonDisabled, KnownLineCount(doc), doc.LastWorked)
            {
                CustomTitle = doc.CustomTitle,
                AutoRefresh = doc.AutoRefresh,
                EditPages = doc.EditPages,
            },
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

        var state = new SessionState(tabs, activeIdx, new SessionWindow(w, h, maximized), _wrap, _fontSize, closedTabs,
            _panes, CaptureGroupTabs(), SplitView ? _activeGroup : null);
        return new SessionCapture(state, payloads, closedPayloads);
    }

    private void QueueSessionTick()
    {
        if (_sessionJournal is null || _closingForSession || _sessionWriter.IsInFlight) return;
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
                    FinishClosing();
                    return;
                }

                int ans = Win32.MessageBoxW(
                    _hwnd,
                    $"Не удалось сохранить сессию. {err.Message}",
                    "Сессия не сохранена. Закрыть окно?",
                    Win32.MB_YESNO | Win32.MB_ICONWARNING);
                if (ans == Win32.IDYES)
                {
                    _restartForUpdate = false;
                    _sessionShutdownDone = true;
                    FinishClosing();
                }
                else
                {
                    _restartForUpdate = false;
                    InvalidateChrome();
                }
            });
        });

        return false;
    }

    private void RestoreSession(RecoveredSession recovered)
    {
        _restoringSession = true;
        ulong maxId = 0;
        foreach (var t in recovered.State.Tabs)
            if (t.Id > maxId) maxId = t.Id;
        if (recovered.State.Closed is { } closedList)
        {
            var (filteredTabs, filteredTexts) = SessionJournal.FilterClosedHistory(closedList, recovered.ClosedDraftTexts);
            for (int i = 0; i < filteredTabs.Count; i++)
            {
                var ct = filteredTabs[i];
                if (ct.Id > maxId) maxId = ct.Id;
                string? ctDraft = i < filteredTexts.Count ? filteredTexts[i] : null;
                _closed.Add((ct, ctDraft is null ? null : Encoding.UTF8.GetBytes(ctDraft)));
            }
        }
        if (maxId >= _nextId) _nextId = maxId + 1;

        for (int i = 0; i < recovered.State.Tabs.Count; i++)
        {
            var tab = recovered.State.Tabs[i];
            ulong id = RestoreTextTab(tab, recovered.DraftTexts[i]);
            var restored = _documents.Find(doc => doc.Id == id);
            if (restored is not null && recovered.State.Panes is not null) restored.Group = tab.Group;
        }
        RestoreGroups(recovered.State);

        if (_documents.Count == 0)
        {
            _showHome = true;
        }
        else
        {
            int idx = Math.Clamp(recovered.State.Active, 0, _documents.Count - 1);
            SelectTab(_documents[idx].Id);
            if (recovered.State.FocusedGroup is { } focused && focused != _activeGroup && _groupBounds.ContainsKey(focused) &&
                !_documents.Exists(doc => doc.Group == focused)) FocusEmptyGroup(focused);
        }
        _restoringSession = false;
    }

    private ulong RestoreTextTab(SessionTab tab, string? draft)
    {
        if (tab.Log is not null && tab.File is not null)
        {
            var ver = tab.File.Version is { } sv ? new FileVersion(sv.Bytes, sv.ModifiedUtc, sv.CreatedUtc) : (FileVersion?)null;
            bool win1251 = string.Equals(tab.Format.Encoding, nameof(TextEncoding.Windows1251), StringComparison.OrdinalIgnoreCase);
            var bookmark = tab.Log.Copy();
            if (tab.AutoRefresh is { } ar) bookmark.AutoRefresh = ar;
            if (tab.EditPages is { } ep) bookmark.EditPages = ep;
            AddLogDocument(new FileTarget(tab.File.Path, ver), win1251, bookmark, restored: true, editPages: bookmark.EditPages);
            var logDoc = _documents[^1];
            logDoc.LastWorked = tab.LastWorked;
            logDoc.CustomTitle = tab.CustomTitle;
            if (!string.IsNullOrWhiteSpace(tab.CustomTitle)) logDoc.Name = tab.CustomTitle;
            return logDoc.Id;
        }

        FileTarget? fileTarget = tab.File is null
            ? null
            : new FileTarget(
                tab.File.Path,
                tab.File.Version is { } v ? new FileVersion(v.Bytes, v.ModifiedUtc, v.CreatedUtc) : null);

        bool win1251Doc = string.Equals(tab.Format.Encoding, nameof(TextEncoding.Windows1251), StringComparison.OrdinalIgnoreCase);

        var enc = Enum.TryParse<TextEncoding>(tab.Format.Encoding, out var pe) ? pe : TextEncoding.Utf8;
        var eol = Enum.TryParse<LineEndings>(tab.Format.LineEndings, out var pl) ? pl : LineEndings.Lf;
        var restoredFormat = new TextFormat(enc, eol);

        ulong id = _nextId++;
        var editorHwnd = ScintillaInterop.CreateEditorControl(_hwnd, _hInstance, (int)MathF.Round(_fontSize), _wrap);
        InstallEditorSubclass(editorHwnd);
        var doc = new Document
        {
            Id = id,
            Group = _activeGroup,
            JsonDisabled = tab.JsonDisabled,
            LastWorked = tab.LastWorked,
            StoredLineCount = tab.LineCount,
            CustomTitle = tab.CustomTitle,
            AutoRefresh = tab.AutoRefresh ?? true,
            EditPages = tab.EditPages ?? false,
            Name = !string.IsNullOrWhiteSpace(tab.CustomTitle) ? tab.CustomTitle : fileTarget is null ? "Без имени" : FileName(fileTarget.Path),
            File = fileTarget,
            Format = restoredFormat,
            EditorHwnd = editorHwnd,
        };

        bool attachedShared = draft is null && TryAttachSharedScintillaDocument(doc);
        if (!attachedShared)
        {
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

            if (draft is not null)
            {
                var draftBytes = Encoding.UTF8.GetBytes(draft);
                ScintillaInterop.SetTextUtf8(editorHwnd, draftBytes);
                doc.Dirty = true;
                doc.DraftRevision = tab.DraftRevision;
                doc.DraftBytes = draftBytes;

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
            if (!doc.Dirty && diskOpened is not null)
            {
                ScintillaInterop.SetSavePoint(editorHwnd);
            }
        }

        ScintillaInterop.SetSelection(editorHwnd, tab.Selection.Start, tab.Selection.End, tab.Reversed);
        if (tab.Scroll.Length >= 2)
        {
            ScintillaInterop.SetScroll(editorHwnd, (int)tab.Scroll[0], (int)tab.Scroll[1]);
        }
        _documents.Add(doc);
        UpdateUntitledName(doc);
        AttachFileWatch(doc);
        UpdateEditorLinksAndLineNumbers(doc);
        return id;
    }

    private void SyncLogInputsAndHits()
    {
        if (!HasDocument) return;
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
                    string position = hit.Line is { } number ? $"Строка {number:N0}" : $"Байт {hit.LineStart:N0}";
                    Win32.SendMessageW(_logSearchList, Win32.LB_ADDSTRING, 0, $"{position} · {hit.Preview}");
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
        if (!_showHome && HasDocument && ActiveDocument.Log is { } log)
        {
            h += LogToolbarLogicalHeight;
        }
        return h;
    }

    private void LayoutAndSyncControls()
    {
        if (_hwnd == 0)
        {
            return;
        }

        Win32.RECT client;
        Win32.GetClientRect(_hwnd, &client);
        int topH = S(TopChromeLogicalHeight());
        int botH = S(StatusLogicalHeight);
        int statusTop = Math.Max(topH, client.Height - botH);
        int bottom = BottomPanelsTop(statusTop);
        LayoutGroups(new Win32.RECT { Left = 0, Right = client.Width, Top = topH, Bottom = bottom });
        InvalidateScrollbars();
        LayoutHome(client, topH, bottom);

        bool hasDocument = !_showHome && HasDocument;
        bool isLog = hasDocument && ActiveDocument.Log is not null;


        if (hasDocument && !isLog && _searchOpen)
        {
            int pad = S(12);
            int editH = S(26);
            int qW = Math.Clamp(client.Width / 4, S(145), S(320));
            int searchTop = bottom;
            int searchBottom = statusTop - GetLogFilterPanelHeight();
            Win32.MoveWindow(_searchQueryEdit, pad, searchTop + S(5), qW, editH, true);
            Win32.MoveWindow(_searchReplaceEdit, pad, searchTop + S(36), qW, editH, true);
            Win32.MoveWindow(_textSearchList, pad, searchTop + S(69), Math.Max(0, client.Width - pad * 2),
                Math.Max(0, searchBottom - searchTop - S(76)), true);
            Win32.ShowWindow(_searchQueryEdit, Win32.SW_SHOW);
            Win32.ShowWindow(_searchReplaceEdit, _replaceOpen ? Win32.SW_SHOW : Win32.SW_HIDE);
            Win32.ShowWindow(_textSearchList, _searchListExpanded ? Win32.SW_SHOW : Win32.SW_HIDE);
        }
        else
        {
            Win32.ShowWindow(_searchQueryEdit, Win32.SW_HIDE);
            Win32.ShowWindow(_searchReplaceEdit, Win32.SW_HIDE);
            Win32.ShowWindow(_textSearchList, Win32.SW_HIDE);
        }

        if (isLog && ActiveDocument.Log is { } log)
        {
            int row1Y = statusTop - S(LogNavigationLogicalHeight) + S(5);
            int pad = S(12);
            int btnH = S(26);

            Win32.MoveWindow(_logJumpEdit, S(335), row1Y, S(70), btnH, true);
            Win32.ShowWindow(_logJumpEdit, Win32.SW_SHOW);

            Win32.ShowWindow(_logTrimEdit, Win32.SW_HIDE);

            if (log.SearchOpen)
            {
                int sTop = bottom;
                int qW = Math.Clamp(client.Width / 3, S(160), S(320));
                Win32.MoveWindow(_logSearchEdit, pad, sTop + S(8), qW, btnH, true);
                Win32.ShowWindow(_logSearchEdit, Win32.SW_SHOW);

                Win32.MoveWindow(_logSearchList, pad, sTop + S(65), Math.Max(S(200), client.Width - pad * 2), Math.Max(0, statusTop - S(LogNavigationLogicalHeight) - GetLogFilterPanelHeight() - sTop - S(69)), true);
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
        var botRect = new Win32.RECT { Left = 0, Top = BottomPanelsTop(Math.Max(0, client.Height - S(StatusLogicalHeight))), Right = client.Width, Bottom = client.Height };
        Win32.InvalidateRect(_hwnd, &topRect, false);
        Win32.InvalidateRect(_hwnd, &botRect, false);
        if (_showHome || SplitView)
        {
            var body = new Win32.RECT { Left = 0, Right = client.Width, Top = topRect.Bottom, Bottom = botRect.Top };
            Win32.InvalidateRect(_hwnd, &body, false);
        }
    }

    private void UpdateActiveCursor()
    {
        if (!HasDocument || ActiveDocument.Log is not null) return;
        var doc = ActiveDocument;
        var (line, col) = ScintillaInterop.GetCursorPosition(doc.EditorHwnd);
        int selected = ScintillaInterop.CountSelectedChars(doc.EditorHwnd, out int carets);
        if (line != doc.CursorLine || col != doc.CursorColumn || selected != doc.LastSelectedChars || carets != doc.LastCaretCount)
        {
            doc.CursorLine = line;
            doc.CursorColumn = col;
            doc.LastSelectedChars = selected;
            doc.LastCaretCount = carets;
            InvalidateChrome();
        }
        UpdateEditorLinksAndLineNumbers(doc);
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
                    PaintTitleBarBackground(topDc, toolbarRect);
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

                        if (HasDocument && ActiveDocument.Log is { } log)
                        {
                            PaintLogToolbar(topDc, client.Width, panelTop, log);
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
        Win32.SetBkMode(hdc, Win32.TRANSPARENT);
        nint oldBodyFont = Win32.SelectObject(hdc, _uiFont);
        int panelBottom = BottomPanelsTop(statusTop);
        if (_showHome)
        {
            PaintHome(hdc, new Win32.RECT { Left = 0, Right = client.Width, Top = totalTopH, Bottom = panelBottom });
        }
        else
        {
            PaintGroupHeaders(hdc);
            foreach (var (group, bounds) in _groupBounds)
            {
                ulong tabId = _groupTabs.GetValueOrDefault(group);
                var groupDoc = tabId == 0 ? null : _documents.Find(d => d.Id == tabId);
                if (groupDoc is not null && (groupDoc.CheckingSession || groupDoc.IdleUnloaded))
                {
                    var area = bounds;
                    if (SplitView) area.Top += S(29);
                    PaintDeferredSurface(hdc, groupDoc, area);
                }
            }
        }
        PaintDocumentScrollbars(hdc);
        if (panelBottom < statusTop)
        {
            var panel = new Win32.RECT { Left = 0, Right = client.Width, Top = panelBottom, Bottom = statusTop };
            Win32.FillRect(hdc, &panel, _panelBrush);
            if (HasDocument && ActiveDocument.Log is { } log)
            {
                PaintLogBottom(hdc, client.Width, panelBottom, statusTop, log);
            }
            else if (HasDocument)
            {
                if (_searchOpen)
                {
                    PaintUpgradedEditorSearch(hdc, client.Width, panelBottom);
                }
                if (_logFilterOpen)
                {
                    PaintLogLevelFilterPanel(hdc, client.Width, statusTop - GetLogFilterPanelHeight());
                }
            }
        }
        Win32.SelectObject(hdc, oldBodyFont);
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
        int padX = S(12);
        int gap = S(4);
        int btnH = S(28);
        int y = (height - btnH) / 2;
        int x = padX;

        int badgeSize = S(27);
        int badgeY = (height - badgeSize) / 2;
        PaintWorkspaceBadge(hdc, new Win32.RECT { Left = x, Top = badgeY, Right = x + badgeSize, Bottom = badgeY + badgeSize });
        x += badgeSize + gap;

        x = DrawButton(hdc, x, y, btnH, "NetText", HitKind.Home, 0, false, _showHome, _smallWindowIcon) + gap;
        x = DrawButton(hdc, x, y, btnH, "Файл ▾", HitKind.FileMenu, 0, false, false) + gap;
        x = DrawButton(hdc, x, y, btnH, "Инструменты ▾", HitKind.ToolsMenu, 0, false, false) + gap;
        x = DrawButton(hdc, x, y, btnH, "Тема ▾", HitKind.ThemeMenu, 0, false, false) + gap;
        bool disabled = _showHome || !HasDocument;
        DrawButton(hdc, x, y, btnH, "Поиск", HitKind.BtnFindText, 0, disabled, !disabled && (_searchOpen || ActiveDocument.Log?.SearchOpen == true));
        PaintUpdateNotice(hdc, CaptionMenuRight(width), y, btnH);
        PaintCaptionButtons(hdc, width, height);
    }

    private string TabLabel(Document doc)
    {
        string prefix = GetTabStatePrefix(doc);
        string name = !string.IsNullOrWhiteSpace(doc.CustomTitle) ? doc.CustomTitle : doc.Name;
        if (doc.CustomTitle is null && doc.File is { } file)
        {
            var others = _documents.Where(other => other != doc && other.File is not null &&
                string.Equals(other.Name, doc.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (others.Length > 0)
            {
                string path = DisplayPath(file.Path);
                string directory = Path.GetDirectoryName(path) ?? path;
                string tail = Path.GetFileName(directory);
                int index = directory.Length - tail.Length;
                var otherDirectories = others.Select(other => Path.GetDirectoryName(DisplayPath(other.File!.Path)) ?? "").ToArray();
                if (tail.Length == 0)
                {
                    tail = directory;
                    index = 0;
                }
                while (index > 0 && otherDirectories.Any(other => other.Equals(tail, StringComparison.OrdinalIgnoreCase) ||
                    (other.Length > tail.Length && other.EndsWith(tail, StringComparison.OrdinalIgnoreCase) &&
                     other[other.Length - tail.Length - 1] == Path.DirectorySeparatorChar)))
                {
                    index = index > 1 ? directory.LastIndexOf(Path.DirectorySeparatorChar, index - 2) + 1 : 0;
                    tail = directory[index..];
                }
                name += " · " + tail;
            }
        }
        return prefix + name;
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
            int availWidth = Math.Max(1, rc.Width - padX * 2 - actionW * 4 - gap * 4);

            int x = 0;
            foreach (var doc in _documents)
            {
                if (doc.Group != _activeGroup) continue;
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
        int tabH = S(26);
        int y = top + (height - tabH) / 2;
        int maxTabW = S(280);
        int closeW = S(24);
        int actionW = S(28);
        int availRight = Math.Max(padX + 1, width - padX - actionW * 4 - gap * 4);
        int availWidth = availRight - padX;

        var groupDocs = new List<Document>();
        var widths = new List<int>();
        int totalWidth = 0;
        for (int i = 0; i < _documents.Count; i++)
        {
            var doc = _documents[i];
            if (doc.Group != _activeGroup) continue;
            int labelW = Math.Min(maxTabW, MeasureTextWidth(hdc, TabLabel(doc), _uiFont) + S(20));
            int totalW = labelW + closeW;
            groupDocs.Add(doc);
            widths.Add(totalW);
            totalWidth += totalW + (groupDocs.Count > 1 ? gap : 0);
        }

        int maxScroll = Math.Max(0, totalWidth - availWidth);
        _tabScrollX = Math.Clamp(_tabScrollX, 0, maxScroll);
        int x = padX - _tabScrollX;

        for (int i = 0; i < groupDocs.Count; i++)
        {
            var doc = groupDocs[i];
            bool active = doc.Id == _activeId;
            string label = TabLabel(doc);
            int totalW = widths[i];
            int shiftedX = x + _tabDragOffsets.GetValueOrDefault(doc.Id);

            if (shiftedX + totalW > padX && shiftedX < availRight)
            {
                int drawLeft = Math.Max(padX, shiftedX);
                int drawRight = Math.Min(availRight, shiftedX + totalW);
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

                    var closeRect = new Win32.RECT { Left = drawRight - closeW, Top = y, Right = drawRight, Bottom = y + tabH };
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

        if (maxScroll > 0)
        {
            int barH = Math.Max(2, S(2));
            var track = new Win32.RECT
            {
                Left = padX,
                Top = top + height - barH,
                Right = availRight,
                Bottom = top + height,
            };
            nint trackBrush = Win32.CreateSolidBrush(ThemePalette.Dim);
            Win32.FillRect(hdc, &track, trackBrush);
            Win32.DeleteObject(trackBrush);

            int thumbW = Math.Max(S(24), (int)MathF.Round(availWidth * ((float)availWidth / totalWidth)));
            thumbW = Math.Min(availWidth, thumbW);
            int thumbTravel = Math.Max(0, availWidth - thumbW);
            int thumbLeft = padX + (int)MathF.Round(thumbTravel * ((float)_tabScrollX / maxScroll));
            var thumb = new Win32.RECT
            {
                Left = thumbLeft,
                Top = track.Top,
                Right = Math.Min(availRight, thumbLeft + thumbW),
                Bottom = track.Bottom,
            };
            nint thumbBrush = Win32.CreateSolidBrush(ThemePalette.Muted);
            Win32.FillRect(hdc, &thumb, thumbBrush);
            Win32.DeleteObject(thumbBrush);
        }

        int newTabX = Math.Min(Math.Max(padX, x), availRight + gap);
        DrawButtonFixed(hdc, newTabX, y, actionW, tabH, "+", HitKind.BtnNewTab, 0, _busy is not null, false);

        int reopenX = newTabX + actionW + gap;
        bool reopenDisabled = _busy is not null || _closed.Count == 0;
        DrawButtonFixed(hdc, reopenX, y, actionW, tabH, "↶", HitKind.BtnReopen, 0, reopenDisabled, false);
        bool splitDisabled = _busy is not null || _showHome || _documents.Count == 0;
        DrawButtonFixed(hdc, reopenX + actionW + gap, y, actionW, tabH, "◫", HitKind.SplitVertical, 0, splitDisabled, false);
        DrawButtonFixed(hdc, reopenX + (actionW + gap) * 2, y, actionW, tabH, "⊟", HitKind.SplitHorizontal, 0, splitDisabled, false);
    }

    private void PaintStatus(nint hdc, int width, int top, int height, int hitYOffset)
    {
        if (!HasDocument || _showHome)
        {
            var rect = new Win32.RECT { Left = S(16), Right = width - S(16), Top = top, Bottom = top + height };
            Win32.SetTextColor(hdc, _error is null ? ThemePalette.Muted : ThemePalette.Error);
            Win32.DrawTextW(hdc, _error ?? _busy ?? "", -1, &rect, Win32.DT_VCENTER | Win32.DT_SINGLELINE | Win32.DT_END_ELLIPSIS);
            return;
        }
        var doc = ActiveDocument;
        int padX = S(12);
        int gap = S(12);
        int rightX = width - padX;

        if (doc.Log is { } log)
        {
            string enc = TextFormat.EncodingLabelOf(log.Encoding);
            var visible = doc.Viewport?.VisibleRange;
            ulong first = visible?.Start ?? log.Bookmark.Offset;
            ulong last = visible?.End ?? first;
            string pos = log.PageLine is { } line && visible is { } range
                ? $"Строки {line + (ulong)range.FirstRow:N0}–{line + (ulong)range.LastRow:N0}" +
                  (log.Index.ScannedTo >= log.Bookmark.Size ? $" из {log.Index.LineCount:N0}" : "")
                : $"Байты {first:N0}–{last:N0} из {log.Bookmark.Size:N0}";
            if (log.Bookmark.Hidden > 0 && log.PageLine is null) pos += $" · скрыто {log.Bookmark.Hidden:N0}";
            int encW = Math.Max(S(82), MeasureTextWidth(hdc, enc, _smallFont));
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
            Win32.DrawTextW(hdc, _error ?? doc.RecoveryStatus ?? log.LeftStatusText(), -1, &msgRect, Win32.DT_LEFT | Win32.DT_VCENTER | Win32.DT_SINGLELINE | Win32.DT_END_ELLIPSIS | Win32.DT_NOPREFIX);
            return;
        }

        string? diskNotice = DiskStatus.Notice(doc.DiskStatus);
        string? notice = _error ?? _busy ?? doc.RecoveryStatus ?? doc.JsonError ?? diskNotice;
        string message = notice ?? (doc.File is not null ? DisplayPath(doc.File.Path) : "Новый документ");

        string eolLabel = doc.Format.LineEndingsLabel;
        string encLabel = doc.Format.EncodingLabel;
        int lineNum = doc.CursorLine + 1;
        int colNum = doc.CursorColumn + 1;
        string posLabel = $"Строка {LineNumbers.Format(lineNum)} · Выделено {LineNumbers.Format(doc.LastSelectedChars)} симв. · Стлб. {LineNumbers.Format(colNum)}" +
            (doc.LastCaretCount > 1 ? $" · Курсоры: {LineNumbers.Format(doc.LastCaretCount)}" : "");

        int eolW2 = Math.Max(S(36), MeasureTextWidth(hdc, eolLabel, _smallFont));
        int encW2 = Math.Max(S(82), MeasureTextWidth(hdc, encLabel, _smallFont));
        int posW2 = Math.Max(S(144), MeasureTextWidth(hdc, posLabel, _smallFont) + S(12));

        Win32.SelectObject(hdc, _smallFont);
        Win32.SetTextColor(hdc, ThemePalette.Muted);

        var eolRect = new Win32.RECT { Left = rightX - eolW2, Top = top, Right = rightX, Bottom = top + height };
        Win32.DrawTextW(hdc, eolLabel, -1, &eolRect, Win32.DT_RIGHT | Win32.DT_VCENTER | Win32.DT_SINGLELINE | Win32.DT_NOPREFIX);
        rightX -= eolW2 + gap;

        var encRect2 = new Win32.RECT { Left = rightX - encW2, Top = top, Right = rightX, Bottom = top + height };
        Win32.DrawTextW(hdc, encLabel, -1, &encRect2, Win32.DT_RIGHT | Win32.DT_VCENTER | Win32.DT_SINGLELINE | Win32.DT_NOPREFIX);
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
        string saveState = doc.Dirty ? "Изменён" : doc.File is null ? "Без файла" : "Сохранён";
        int saveWidth = Math.Max(S(76), MeasureTextWidth(hdc, saveState, _smallFont));
        var saveRect = new Win32.RECT { Left = rightX - saveWidth, Right = rightX, Top = top, Bottom = top + height };
        Win32.DrawTextW(hdc, saveState, -1, &saveRect, Win32.DT_RIGHT | Win32.DT_VCENTER | Win32.DT_SINGLELINE);
        rightX -= saveWidth + gap;

        var msgRect2 = new Win32.RECT { Left = padX, Top = top, Right = Math.Max(padX, rightX), Bottom = top + height };
        Win32.SetTextColor(hdc, _error is not null || doc.JsonError is not null ? ThemePalette.Error : ThemePalette.Muted);
        uint ellipsis = notice is null && doc.File is not null ? Win32.DT_PATH_ELLIPSIS : Win32.DT_END_ELLIPSIS;
        Win32.DrawTextW(hdc, message, -1, &msgRect2, Win32.DT_LEFT | Win32.DT_VCENTER | Win32.DT_SINGLELINE | ellipsis | Win32.DT_NOPREFIX);
        if (doc.JsonError is not null && _error is null && _busy is null)
        {
            msgRect2.Top += hitYOffset;
            msgRect2.Bottom += hitYOffset;
            _hits.Add(new HitRect(msgRect2, HitKind.JsonError));
        }
    }

    private int MeasureButtonWidth(nint hdc, string text) => MeasureTextWidth(hdc, text, _uiFont) + S(20);

    private int MeasureTextWidth(nint hdc, string text, nint font)
    {
        Win32.SelectObject(hdc, font);
        Win32.SIZE sz;
        Win32.GetTextExtentPoint32W(hdc, text, text.Length, &sz);
        return sz.cx;
    }

    private int DrawButton(nint hdc, int x, int y, int h, string text, HitKind kind, ulong tabId, bool disabled, bool selected, nint icon = 0)
    {
        int w = MeasureButtonWidth(hdc, text) + (icon == 0 ? 0 : S(22));
        DrawButtonFixed(hdc, x, y, w, h, text, kind, tabId, disabled, selected, icon);
        return x + w;
    }

    private void DrawButtonFixed(nint hdc, int x, int y, int w, int h, string text, HitKind kind, ulong tabId, bool disabled, bool selected, nint icon = 0)
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
        var label = rect;
        if (icon != 0)
        {
            int size = S(SmallIconLogicalSize);
            Win32.DrawIconEx(hdc, x + S(10), y + (h - size) / 2, icon, size, size, 0, 0, Win32.DI_NORMAL);
            label.Left += S(22);
        }
        Win32.DrawTextW(hdc, text, -1, &label, Win32.DT_CENTER | Win32.DT_VCENTER | Win32.DT_SINGLELINE | Win32.DT_NOPREFIX);
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
        if (DragDivider(x, y)) return;
        if (_scrollDrag is not null)
        {
            DragDocumentScroll(x, y);
            return;
        }
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

        if (UpdateTabDrag(x, y))
        {
            return;
        }

        var hit = HitTest(x, y);
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

        if (hit.Value.Kind is HitKind.ScrollMap or HitKind.HorizontalScroll or HitKind.VerticalScroll)
        {
            BeginScroll(hit.Value, x, y);
            return;
        }

        if (hit.Value.Kind == HitKind.GroupDivider)
        {
            var divider = _dividers[(int)hit.Value.TabId];
            _dragDivider = (divider.Pane, divider.Bounds);
            Win32.SetCapture(_hwnd);
            return;
        }

        if (hit.Value.Kind is HitKind.TabSelect or HitKind.GroupTab)
        {
            BeginTabDrag(hit.Value.TabId, x, y);
            SelectTab(hit.Value.TabId);
            return;
        }

        var log = HasDocument ? ActiveDocument.Log : null;

        switch (hit.Value.Kind)
        {
            case HitKind.WorkspaceBadge: ShowWorkspaceMenu(hit.Value.Rect); break;
            case HitKind.ThemeMenu: ShowThemeMenu(hit.Value.Rect); break;
            case HitKind.OpenUpdateDialog: ShowUpdateDialog(); break;
            case HitKind.DeferredLoadTab:
                if (_documents.Find(d => d.Id == hit.Value.TabId) is { } deferredDoc)
                {
                    RestoreDeferredOrIdleTab(deferredDoc);
                }
                break;
            case HitKind.HomeSectionToggle:
            case HitKind.HomeRowOpen:
            case HitKind.HomeRowDelete:
            case HitKind.HomeSectionPrev:
            case HitKind.HomeSectionNext:
                HandleHomeHit(hit.Value);
                break;
            case HitKind.EmptyGroup: FocusEmptyGroup((int)hit.Value.TabId); break;
            case HitKind.NewInGroup:
                FocusEmptyGroup((int)hit.Value.TabId);
                NewFile();
                break;
            case HitKind.CloseEmptyGroup: CloseEmptyGroup((int)hit.Value.TabId); break;
            case HitKind.ApplyUpdate: RestartForUpdate(); break;
            case HitKind.DismissUpdate: DismissUpdate(); break;
            case HitKind.Home: ToggleHome(); break;
            case HitKind.HomeNext: _historyPage++; UpdateHistory(); break;
            case HitKind.HomePrevious: _historyPage--; UpdateHistory(); break;
            case HitKind.FileMenu: ShowFileMenu(hit.Value.Rect); break;
            case HitKind.ToolsMenu: ShowToolsMenu(hit.Value.Rect); break;
            case HitKind.JsonError: OpenJsonError(); break;
            case HitKind.SplitVertical: SplitGroup(true); break;
            case HitKind.SplitHorizontal: SplitGroup(false); break;
            case HitKind.BtnNew: NewFile(); break;
            case HitKind.BtnOpen: OpenPicker(false, false); break;
            case HitKind.BtnRecent: ShowRecentMenu(hit.Value.Rect); break;
            case HitKind.BtnLogMenu: ShowLogMenu(hit.Value.Rect); break;
            case HitKind.BtnOpen1251: OpenPicker(true, false); break;
            case HitKind.BtnSave: Save(false); break;
            case HitKind.BtnSaveAs: Save(true); break;
            case HitKind.BtnFindText: OpenSearch(false); break;
            case HitKind.BtnFormatJson: FormatActiveDocumentJson(); break;
            case HitKind.BtnWrap: ToggleWrap(); break;
            case HitKind.BtnZoomOut: Zoom(-1.0f); InvalidateChrome(); break;
            case HitKind.BtnZoomIn: Zoom(1.0f); InvalidateChrome(); break;
            case HitKind.TabClose: CloseTab(hit.Value.TabId); break;
            case HitKind.BtnNewTab: NewFile(); break;
            case HitKind.BtnReopen: Reopen(); break;
            case HitKind.BtnGoToLine: GoToLine(); break;

            case HitKind.SearchPrev: RunEditorSearch(SearchCommand.Previous); break;
            case HitKind.SearchNext: RunEditorSearch(SearchCommand.Next); break;
            case HitKind.SearchRun: RunExplicitSearch(); break;
            case HitKind.SearchFindAll: FindAllToMultiCarets(); break;
            case HitKind.SearchList: ToggleSearchList(); break;
            case HitKind.SearchToggleReplace:
                _replaceOpen = !_replaceOpen;
                LayoutAndSyncControls();
                if (_replaceOpen) Win32.SetFocus(_searchReplaceEdit);
                InvalidateChrome();
                break;
            case HitKind.SearchHighlight: ToggleHighlightCurrentOnly(); break;
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
            case HitKind.LogJump50:
                if (log is not null)
                {
                    log.JumpPercentInput = "50";
                    log.JumpPercent();
                }
                break;
            case HitKind.LogLevelsButton: ToggleLogLevelsPanel(); break;
            case HitKind.LogActionsMenu:
                if (HasDocument) ShowLogActionsMenu(ActiveDocument, hit.Value.Rect);
                break;
            case HitKind.LogEditPage:
                if (HasDocument) EnterLogPageEdit(ActiveDocument);
                break;
            case HitKind.LogSavePage:
                if (HasDocument) SaveLogPageEdit(ActiveDocument);
                break;
            case HitKind.LogCancelPage:
                if (HasDocument) CancelLogPageEdit(ActiveDocument);
                break;
            case HitKind.LogFilterToggle:
            case HitKind.LogFilterAll:
            case HitKind.LogFilterClose:
                HandleLogFilterHit(hit.Value);
                break;
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
            case HitKind.LogHideStart: if (log is not null) ShowLogTrim(log, false); break;
            case HitKind.LogPhysicalTrim: if (log is not null) ShowLogTrim(log, true); break;
            case HitKind.LogRevealAll: log?.RevealAll(); break;
            case HitKind.LogCancelTrim: log?.CancelTrim(); break;
            case HitKind.LogFindNew: log?.StartSearch(false); break;
            case HitKind.LogTime: log?.SeekTime(); break;
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

    private void OnLeftButtonUp(int x, int y)
    {
        if (_dragDivider is not null)
        {
            _dragDivider = null;
            Win32.ReleaseCapture();
        }
        FinishTabDrag(x, y);
    }

    private void OnCommand(nuint wParam)
    {
        if (_suppressEditEvents) return;
        int id = (int)((ulong)wParam & 0xFFFF);
        int code = (int)(((ulong)wParam >> 16) & 0xFFFF);

        if (id == IdHistoryQuery && code == Win32.EN_CHANGE)
        {
            HomeSearchChanged();
            return;
        }
        if (id == IdHistoryList && code == 2)
        {
            OpenHistorySelection();
            return;
        }
        if (id == IdTextSearchList && code == Win32.LBN_SELCHANGE)
        {
            ChooseTextSearchResult();
            return;
        }

        if (code == Win32.EN_CHANGE)
        {
            switch (id)
            {
                case IdSearchQueryEdit:
                    _searchQuery = ReadControlText(_searchQueryEdit);
                    ResetEditorSearch();
                    ScheduleAutoSearch();
                    break;
                case IdSearchReplaceEdit:
                    _searchReplacement = ReadControlText(_searchReplaceEdit);
                    ResetEditorSearch();
                    break;
                case IdLogJumpEdit:
                    if (HasDocument && ActiveDocument.Log is { } jLog)
                        jLog.JumpPercentInput = ReadControlText(_logJumpEdit);
                    break;
                case IdLogTrimEdit:
                    if (HasDocument && ActiveDocument.Log is { } tLog)
                        tLog.TrimPercentInput = ReadControlText(_logTrimEdit);
                    break;
                case IdLogSearchEdit:
                    if (HasDocument && ActiveDocument.Log is { } sLog)
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
            if (HasDocument && ActiveDocument.Log is { } log)
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
        if (_pageEditors.FirstOrDefault(kv => kv.Value == nm->hwndFrom) is { Key: > 0 } pageEntry)
        {
            var pageDoc = _documents.Find(d => d.Id == pageEntry.Key);
            if (pageDoc is not null)
            {
                if (nm->code == ScintillaInterop.SCN_MODIFIED)
                {
                    pageDoc.Log?.UpdatePendingPageEdit(ScintillaInterop.GetTextString(pageEntry.Value));
                    InvalidateChrome();
                }
                else if (nm->code == ScintillaInterop.SCN_FOCUSIN && !_showHome && pageDoc.Id != _activeId)
                {
                    SelectTab(pageDoc.Id);
                }
            }
            return;
        }

        var doc = _documents.Find(d => d.EditorHwnd == nm->hwndFrom);
        if (doc is null || doc.Log is not null) return;

        switch (nm->code)
        {
            case ScintillaInterop.SCN_MODIFIED:
                doc.Revision++;
                RefreshJsonIndicator(doc);
                doc.LastWorked = DateTimeOffset.UtcNow;
                doc.DraftBytes = null;
                doc.UntitledNamePending = doc.File is null;
                doc.JsonDue = Environment.TickCount64 + 300;
                doc.MeasuredLines = null;
                doc.MeasuredWidth = 0;
                InvalidateMinimap(doc.Id);
                UpdateEditorLinksAndLineNumbers(doc);
                if (doc.ActiveLogFilter != LogLevel.All) SyncEditorLogFilter(doc);
                PropagateSharedDocumentChange(doc);
                break;

            case ScintillaInterop.SCN_FOCUSIN:
                if (!_showHome && doc.Id != _activeId) SelectTab(doc.Id);
                break;

            case ScintillaInterop.SCN_SAVEPOINTREACHED:
                if (doc.Dirty)
                {
                    doc.Dirty = false;
                    PropagateSharedDocumentChange(doc);
                    InvalidateChrome();
                }
                break;

            case ScintillaInterop.SCN_SAVEPOINTLEFT:
                if (!doc.Dirty)
                {
                    doc.Dirty = true;
                    PropagateSharedDocumentChange(doc);
                    InvalidateChrome();
                }
                break;

            case ScintillaInterop.SCN_UPDATEUI:
                SyncDocumentScroll(doc);
                UpdateEditorLinksAndLineNumbers(doc);
                if (doc.Id == _activeId)
                {
                    UpdateActiveCursor();
                    UpdateEditorIndicators(doc);
                }
                break;

            case ScintillaInterop.SCN_PAINTED:
                MeasureEditorWidth(doc);
                SyncDocumentScroll(doc);
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
                if (_e2eFirstPainted && !log.Reading && log.Page is not null && log.NeedsIndexOptIn && log.IndexProgress is null)
                {
                    log.StartFullIndexOptIn();
                    return;
                }
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
        var app = WorkspaceSession.Current.FindWindow(hwnd) ?? _creatingWindow;
        if (app is null) return Win32.DefWindowProcW(hwnd, msg, wParam, lParam);
        nint dwmResult;
        switch (msg)
        {
            case Win32.WM_NCCALCSIZE:
                return CalculateCaptionFrame(hwnd, msg, wParam, lParam);

            case Win32.WM_NCHITTEST:
                return app.CaptionHitTest(hwnd, msg, wParam, lParam);

            case Win32.WM_NCMOUSEMOVE:
                app.TrackCaptionButton((int)wParam);
                if (Win32.DwmDefWindowProc(hwnd, msg, wParam, lParam, &dwmResult)) return dwmResult;
                break;

            case Win32.WM_NCMOUSELEAVE:
                app.TrackCaptionButton(0);
                if (Win32.DwmDefWindowProc(hwnd, msg, wParam, lParam, &dwmResult)) return dwmResult;
                break;

            case Win32.WM_NCLBUTTONDOWN:
            case Win32.WM_NCLBUTTONDBLCLK:
                if (app.BeginCaptionButton((int)wParam)) return 0;
                break;

            case WM_ACTIVATE:
                app.SyncActiveWindowTheme(wParam);
                break;

            case WM_MOVE:
                app.TrackNormalWindowBounds();
                return 0;

            case Win32.WM_SIZE:
                app.TrackNormalWindowBounds();
                app.LayoutAndSyncControls();
                app.InvalidateChrome();
                app.InvalidateScrollbars();
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
                app.UpdateWindowIcons();
                foreach (var d in app._documents) d.Viewport?.SetDpi((int)app._dpi);
                var suggested = (Win32.RECT*)lParam;
                Win32.MoveWindow(hwnd, suggested->Left, suggested->Top, suggested->Width, suggested->Height, true);
                foreach (var doc in app._documents)
                    if (doc.Log is null)
                    {
                        doc.MeasuredLines = null;
                        doc.MeasuredWidth = 0;
                        ScintillaInterop.ApplyLanguage(doc.EditorHwnd, doc.File?.Path, (int)MathF.Round(app._fontSize));
                    }
                app.LayoutAndSyncControls();
                app.InvalidateChrome();
                return 0;

            case Win32.WM_SETFOCUS:
                app.FocusActiveEditor();
                return 0;

            case Win32.WM_MOUSEMOVE:
            {
                int x = Win32.GetXParam(lParam);
                int y = Win32.GetYParam(lParam);
                if (app._captionPressed == 0) app.OnMouseMove(x, y);
                else app.TrackCaptionButton(app.CaptionButtonAt(x, y), trackLeave: false);
                return 0;
            }

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
                if (app.ReleaseCaptionButton(Win32.GetXParam(lParam), Win32.GetYParam(lParam))) return 0;
                if (app._scrollDrag is not null)
                {
                    app.DragDocumentScroll(Win32.GetXParam(lParam), Win32.GetYParam(lParam));
                    app._scrollDrag = null;
                    Win32.ReleaseCapture();
                    return 0;
                }
                app.OnLeftButtonUp(Win32.GetXParam(lParam), Win32.GetYParam(lParam));
                return 0;

            case Win32.WM_CAPTURECHANGED:
                if (app._captionPressed != 0)
                {
                    app._captionPressed = 0;
                    app.TrackCaptionButton(0);
                }
                app._scrollDrag = null;
                app._dragDivider = null;
                app.CancelTabDrag();
                return 0;

            case Win32.WM_RBUTTONUP:
                app.ShowTabMenu(Win32.GetXParam(lParam), Win32.GetYParam(lParam));
                return 0;

            case Win32.WM_LBUTTONDBLCLK:
            {
                int x = Win32.GetXParam(lParam);
                int y = Win32.GetYParam(lParam);
                if (app.HandleGroupHeaderDoubleClick(x, y))
                {
                    return 0;
                }
                if (app.HitTest(x, y) is { Kind: HitKind.TabSelect } tab)
                {
                    app.CancelTabDrag();
                    if (app._justDraggedTabId != tab.TabId)
                    {
                        app.CloseTab(tab.TabId);
                    }
                    app._justDraggedTabId = 0;
                    return 0;
                }
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
                if (app.HandleGroupHeaderWheel(pt.X, pt.Y, delta, horizontal: false))
                {
                    return 0;
                }
                if (!app._forwardingWheel && app.HitTest(pt.X, pt.Y) is { Kind: HitKind.ScrollMap or HitKind.HorizontalScroll or HitKind.VerticalScroll } hit
                    && app._documents.Find(doc => doc.Id == hit.TabId) is { } doc)
                {
                    app._forwardingWheel = true;
                    Win32.SendMessageW(doc.EditorHwnd, msg, wParam, lParam);
                    app._forwardingWheel = false;
                    return 0;
                }
                break;
            }

            case Win32.WM_MOUSEHWHEEL:
            {
                var pt = new Win32.POINT { X = Win32.GetXParam(lParam), Y = Win32.GetYParam(lParam) };
                Win32.ScreenToClient(hwnd, &pt);
                int delta = Win32.GetWheelDelta(wParam);
                int top = app.S(ToolbarLogicalHeight);
                int bottom = top + app.S(TabBarLogicalHeight);
                if (pt.Y >= top && pt.Y <= bottom)
                {
                    app._tabScrollX = Math.Max(0, app._tabScrollX + delta / 3);
                    app.InvalidateChrome();
                    return 0;
                }
                if (app.HandleGroupHeaderWheel(pt.X, pt.Y, delta, horizontal: true))
                {
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
                else if (wParam == SearchDebounceTimerId) app.OnSearchDebounceTimer();
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

            case Win32.WM_APP_SCROLL_LAYOUT:
                app._scrollLayoutQueued = false;
                app.LayoutAndSyncControls();
                app.InvalidateScrollbars();
                return 0;

            case Win32.WM_APP_IPC:
                WorkspaceSession.Current.RouteIncomingIpc(WorkspaceSession.Current.SingleInstance ?? app._singleInstance);
                return 0;

            case Win32.WM_ACTIVATEAPP:
                if (wParam != 0)
                {
                    app.CheckDiskStatusTick();
                }
                return 0;

            case Win32.WM_CLOSE:
                foreach (var doc in app._documents.ToArray())
                {
                    if (!app.ConfirmLeaveDirtyPageEdit(doc)) return 0;
                }
                if (!app.BeginSessionShutdown()) return 0;
                app.FinishClosing();
                return 0;

            case Win32.WM_NCDESTROY:
            {
                nint result = Win32.DefWindowProcW(hwnd, msg, wParam, lParam);
                app.ReleaseWindowIcons();
                return result;
            }

            case Win32.WM_DESTROY:
                app._closingWindow = true;
                app._activeId = 0;
                app.CancelTabDrag();
                app.DisposeUpdate();
                Win32.KillTimer(hwnd, FastTimerId);
                Win32.KillTimer(hwnd, SlowTimerId);
                app.CancelAutoSearch();
                foreach (var d in app._documents.ToArray())
                {
                    app.DisposeDocumentResources(d);
                }
                app._documents.Clear();
                app._topChromeBuffer.Dispose();
                app._statusBuffer.Dispose();
                var journal = app._sessionJournal;
                app._sessionJournal = null;
                WorkspaceSession.Current.ReleaseJournalIfUnused(journal);
                if (WorkspaceSession.Current.UnregisterWindow(app))
                {
                    Win32.PostQuitMessage(0);
                }
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
