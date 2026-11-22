using System.Runtime.InteropServices;
using System.Text;
using NetText.Core;
using NetText.Native;

namespace NetText.UI;

internal sealed unsafe partial class MainWindow
{
    private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;
    private const uint WM_ACTIVATE = 0x0006;
    private const int WA_INACTIVE = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public uint cbSize;
        public Win32.RECT rcMonitor;
        public Win32.RECT rcWork;
        public uint dwFlags;
    }

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromPoint(Win32.POINT pt, uint dwFlags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfoW(nint hMonitor, MONITORINFO* lpmi);

    internal MainWindow(
        string[] startupPaths,
        string? workspaceId,
        string? windowId,
        bool startEmpty,
        bool isDetached,
        Win32.POINT? initialPosition = null,
        RecentFiles? sharedRecent = null,
        float? fontSize = null,
        bool? wrap = null,
        bool shareDefaultSession = false)
    {
        try { _settings = EditorSettings.Load(); }
        catch (Exception error) { _error = $"Не удалось прочитать настройки. {error.Message}"; }

        if (sharedRecent is not null)
        {
            _recent = sharedRecent.Clone();
        }
        else
        {
            var (recent, recentError) = RecentFiles.Load();
            _recent = recent;
            if (recentError is not null) _error = RecentWarning(recentError);
        }

        if (fontSize is { } fs) _fontSize = Math.Clamp(fs, MinFontSize, MaxFontSize);
        if (wrap is { } wp) _wrap = wp;

        _hInstance = Win32.GetModuleHandleW(null);
        var recovered = InitializeWorkspaceState(workspaceId, windowId, startEmpty, isDetached, shareDefaultSession);
        WorkspaceSession.Current.RegisterWindow(this);
        CreateWindowAndShow(startupPaths, recovered);
        UpdateWindowTitle();

        if (initialPosition is { } point)
        {
            PositionWindowAtScreenPoint(point);
        }
    }

    internal static MainWindow OpenAdditionalWindow(
        string? workspaceId,
        string? windowId,
        bool startEmpty,
        bool isDetached,
        Win32.POINT? initialPosition = null)
    {
        var source = WorkspaceSession.Current.LastActiveWindow ?? WorkspaceSession.Current.Windows.FirstOrDefault();
        var window = new MainWindow(
            [],
            workspaceId,
            windowId,
            startEmpty,
            isDetached,
            initialPosition,
            source?._recent,
            source?._fontSize,
            source?._wrap);
        Win32.ShowWindow(window._hwnd, Win32.SW_SHOW);
        Win32.SetForegroundWindow(window._hwnd);
        return window;
    }

    internal void OpenPathsFromIpc(string[] paths)
    {
        if (paths.Length == 0 || _busy is not null) return;
        OpenPaths(paths, false, false);
    }

    partial void OnMoveTabToNewWindow(Document doc) => OpenTabInNewWindow(doc);

    internal void OpenTabInNewWindow(Document doc, Win32.POINT? screenPoint = null)
    {
        if (_closingWindow || doc.Log?.IsWritingFile == true || doc.Log?.IsSavingPage == true || doc.IsConsole || !_documents.Contains(doc))
        {
            return;
        }
        if (!ConfirmLeaveDirtyPageEdit(doc))
        {
            return;
        }
        if (doc.IdleUnloaded || doc.Placeholder)
        {
            RestoreDeferredOrIdleTab(doc);
            if (!_documents.Contains(doc)) return;
        }

        var session = WorkspaceSession.Current;
        string workspaceId = _workspaceId;
        string newWindowId = "window:" + Guid.NewGuid().ToString("N");
        string sourceDir = CurrentJournalDirectory ?? session.Catalog.GetSessionDirectory(workspaceId, _windowId);
        string primaryDir = session.Catalog.GetSessionDirectory(workspaceId, WorkspaceCatalogService.PrimaryWindowId);
        bool independent = !_startEmpty && !string.Equals(
            WorkspaceSession.NormalizeDirectory(sourceDir),
            WorkspaceSession.NormalizeDirectory(primaryDir),
            StringComparison.OrdinalIgnoreCase);

        bool shareDefaultSession = !independent && workspaceId == WorkspaceCatalogService.DefaultWorkspaceId;
        if (independent || workspaceId != WorkspaceCatalogService.DefaultWorkspaceId)
        {
            if (_sessionJournal is { } shared)
            {
                session.ShareSession(workspaceId, newWindowId, shared);
            }
        }

        var destination = new MainWindow(
            [],
            workspaceId,
            newWindowId,
            startEmpty: false,
            isDetached: true,
            initialPosition: screenPoint,
            sharedRecent: _recent,
            fontSize: _fontSize,
            wrap: _wrap,
            shareDefaultSession: shareDefaultSession);

        try
        {
            if (!TransferTabToWindow(doc, destination, targetGroup: 0))
            {
                destination._closingWindow = true;
                Win32.DestroyWindow(destination._hwnd);
                return;
            }
        }
        catch
        {
            destination._closingWindow = true;
            Win32.DestroyWindow(destination._hwnd);
            throw;
        }

        Win32.ShowWindow(destination._hwnd, Win32.SW_SHOW);
        Win32.SetForegroundWindow(destination._hwnd);
        destination.FocusActiveEditor();
    }

    internal bool TransferTabToWindow(Document doc, MainWindow destination, int targetGroup, ulong insertBeforeTabId = 0)
    {
        if (_closingWindow || doc.Log?.IsWritingFile == true || doc.Log?.IsSavingPage == true || !_documents.Contains(doc) || doc.Id == insertBeforeTabId)
        {
            return false;
        }

        if (ReferenceEquals(this, destination))
        {
            MoveTabToGroup(doc.Id, targetGroup);
            if (insertBeforeTabId != 0)
            {
                ReorderTabBefore(doc.Id, insertBeforeTabId);
            }
            return true;
        }

        if (!ConfirmLeaveDirtyPageEdit(doc))
        {
            return false;
        }
        if (doc.IdleUnloaded || doc.Placeholder)
        {
            RestoreDeferredOrIdleTab(doc);
            if (!_documents.Contains(doc)) return false;
        }

        var (snapshotTab, draftBytes) = SnapshotDocument(doc, includeDraft: true);
        Document created;
        if (doc.Log is { } log && doc.File is { } logFile)
        {
            destination.AddLogDocument(logFile, log.Windows1251, log.Bookmark.Copy(), restored: true);
            created = destination._documents[^1];
        }
        else
        {
            ulong newId = destination._nextId++;
            nint editorHwnd = ScintillaInterop.CreateEditorControl(
                destination._hwnd,
                destination._hInstance,
                (int)MathF.Round(destination._fontSize),
                destination._wrap);
            destination.InstallEditorSubclass(editorHwnd);

            bool sharedAttached = false;
            if (doc.File is not null && doc.EditorHwnd != 0)
            {
                nint docPtr = ScintillaInterop.Send(doc.EditorHwnd, ScintillaInterop.SCI_GETDOCPOINTER, 0, 0);
                if (docPtr != 0)
                {
                    ScintillaInterop.Send(doc.EditorHwnd, ScintillaInterop.SCI_ADDREFDOCUMENT, 0, docPtr);
                    ScintillaInterop.Send(editorHwnd, ScintillaInterop.SCI_SETDOCPOINTER, 0, docPtr);
                    sharedAttached = true;
                }
            }

            if (!sharedAttached && doc.EditorHwnd != 0)
            {
                byte[] utf8 = ScintillaInterop.CopyUtf8Bytes(doc.EditorHwnd);
                ScintillaInterop.SetTextUtf8(editorHwnd, utf8);
            }

            ScintillaInterop.ApplyLanguage(editorHwnd, doc.File?.Path, (int)MathF.Round(destination._fontSize));
            if (!doc.Dirty)
            {
                ScintillaInterop.SetSavePoint(editorHwnd);
            }
            ScintillaInterop.SetSelection(editorHwnd, snapshotTab.Selection.Start, snapshotTab.Selection.End, snapshotTab.Reversed);
            if (snapshotTab.Scroll.Length >= 2)
            {
                ScintillaInterop.SetScroll(editorHwnd, (int)snapshotTab.Scroll[0], (int)snapshotTab.Scroll[1]);
            }

            created = new Document
            {
                Id = newId,
                Group = targetGroup,
                Name = doc.Name,
                CustomTitle = doc.CustomTitle,
                File = doc.File,
                Format = doc.Format,
                EditorHwnd = editorHwnd,
                Dirty = doc.Dirty,
                Placeholder = false,
                DiskStatus = doc.DiskStatus,
                Revision = doc.Revision,
                DraftRevision = doc.DraftRevision,
                DraftBytes = draftBytes,
                JsonDisabled = doc.JsonDisabled,
                LastWorked = doc.LastWorked,
                StoredLineCount = doc.StoredLineCount,
                AutoRefresh = doc.AutoRefresh,
                EditPages = doc.EditPages,
            };
            destination._documents.Add(created);
            destination.AttachFileWatch(created);
            destination.UpdateEditorLinksAndLineNumbers(created);
        }

        created.Group = targetGroup;
        created.CustomTitle = doc.CustomTitle;
        if (doc.CustomTitle is not null) created.Name = doc.CustomTitle;
        created.AutoRefresh = doc.AutoRefresh;
        created.LastWorked = doc.LastWorked;

        if (insertBeforeTabId != 0)
        {
            destination.ReorderTabBefore(created.Id, insertBeforeTabId);
        }

        destination.SelectTab(created.Id);

        if (destination._isDetachedWindow && !destination._startEmpty)
        {
            destination.RecordDetachedDraft(snapshotTab, draftBytes);
        }

        RemoveTransferredTab(doc.Id);

        if (_documents.Count == 0 && !_closingWindow && destination._hwnd != 0)
        {
            Win32.PostMessageW(_hwnd, Win32.WM_CLOSE, 0, 0);
        }

        return true;
    }

    private void RemoveTransferredTab(ulong id)
    {
        int index = _documents.FindIndex(d => d.Id == id);
        if (index < 0) return;

        var doc = _documents[index];
        bool wasActive = id == _activeId;
        // Clear _activeId BEFORE removing from _documents and calling DestroyWindow.
        if (wasActive) _activeId = 0;

        _documents.RemoveAt(index);
        RemoveEmptyGroup(doc.Group);
        DisposeDocumentResources(doc);

        if (_documents.Count == 0)
        {
            _showHome = true;
            LayoutAndSyncControls();
            InvalidateChrome();
        }
        else if (wasActive)
        {
            int nextIndex = Math.Max(0, index - 1);
            SelectTab(_documents[nextIndex].Id);
        }
        else
        {
            LayoutAndSyncControls();
            InvalidateChrome();
        }
    }

    private void RecordDetachedDraft(SessionTab tab, byte[]? draftBytes)
    {
        var session = WorkspaceSession.Current;
        string sourceDir = WorkspaceSession.NormalizeDirectory(
            CurrentJournalDirectory ?? session.Catalog.GetSessionDirectory(_workspaceId, _windowId));
        string primaryDir = WorkspaceSession.NormalizeDirectory(
            session.Catalog.GetSessionDirectory(_workspaceId, WorkspaceCatalogService.PrimaryWindowId));
        if (string.Equals(sourceDir, primaryDir, StringComparison.OrdinalIgnoreCase)) return;

        if (!session.DetachedDrafts.TryGetValue(sourceDir, out var list))
        {
            list = [];
            session.DetachedDrafts[sourceDir] = list;
        }

        session.DetachedPrimaryDirectories[sourceDir] = primaryDir;
        list.RemoveAll(item => item.Tab.Id == tab.Id);
        list.Add((tab, draftBytes));
    }

    private void RecordClosingWindowTabsToHistory()
    {
        var other = WorkspaceSession.Current.FindOtherSessionWindow(this);
        if (other is null || _documents.Count == 0) return;

        for (int i = 0; i < _documents.Count; i++)
        {
            var doc = _documents[i];
            var (tab, draftBytes) = SnapshotDocument(doc, includeDraft: true);
            if (SessionJournal.IsKnownEmptyUntitledDraft(tab, draftBytes)) continue;

            if (other._closed.Count >= MaxClosedTabs)
            {
                other._closed.RemoveAt(0);
            }
            other._closed.Add((tab with { ClosedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }, draftBytes));
        }

        other.CommitCurrentSessionSync();
        other.InvalidateChrome();
    }

    private void RecoverDetachedDraftsOnClose()
    {
        if (!_isDetachedWindow || _startEmpty) return;
        var session = WorkspaceSession.Current;
        string sourceDir = WorkspaceSession.NormalizeDirectory(
            CurrentJournalDirectory ?? session.Catalog.GetSessionDirectory(_workspaceId, _windowId));

        var recovered = new List<(SessionTab Tab, byte[]? DraftBytes)>();
        for (int i = 0; i < _documents.Count; i++)
        {
            var doc = _documents[i];
            var (tab, draftBytes) = SnapshotDocument(doc, includeDraft: true);
            if (tab.File is null && !SessionJournal.IsKnownEmptyUntitledDraft(tab, draftBytes))
            {
                recovered.Add((tab with { ClosedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }, draftBytes));
            }
        }

        if (session.DetachedDrafts.TryGetValue(sourceDir, out var tracked))
        {
            foreach (var item in tracked)
            {
                if (item.Tab.File is null &&
                    !SessionJournal.IsKnownEmptyUntitledDraft(item.Tab, item.DraftBytes) &&
                    !recovered.Any(r => r.Tab.Id == item.Tab.Id))
                {
                    recovered.Add(item);
                }
            }
            session.DetachedDrafts.Remove(sourceDir);
        }

        session.DetachedPrimaryDirectories.Remove(sourceDir);
        if (recovered.Count == 0) return;

        var primaryWindow = session.FindWindowForWorkspace(_workspaceId);
        if (primaryWindow is not null && !ReferenceEquals(primaryWindow, this))
        {
            foreach (var item in recovered)
            {
                if (primaryWindow._closed.Count >= MaxClosedTabs)
                {
                    primaryWindow._closed.RemoveAt(0);
                }
                primaryWindow._closed.Add(item);
            }
            primaryWindow.CommitCurrentSessionSync();
            primaryWindow.InvalidateChrome();
        }
        else
        {
            try
            {
                var primaryJournal = session.GetOrOpenJournal(_workspaceId, WorkspaceCatalogService.PrimaryWindowId);
                var existing = primaryJournal.Restore();
                if (existing is not null)
                {
                    var closedTabs = new List<SessionTab>(existing.State.Closed ?? []);
                    var closedPayloads = existing.ClosedDraftTexts
                        .Select(text => text is null ? null : Encoding.UTF8.GetBytes(text))
                        .ToList();
                    foreach (var item in recovered)
                    {
                        if (closedTabs.Count >= MaxClosedTabs)
                        {
                            closedTabs.RemoveAt(0);
                            if (closedPayloads.Count > 0) closedPayloads.RemoveAt(0);
                        }
                        closedTabs.Add(item.Tab);
                        closedPayloads.Add(item.DraftBytes);
                    }
                    var updatedState = existing.State with { Closed = closedTabs };
                    var openPayloads = existing.DraftTexts
                        .Select(text => text is null ? null : Encoding.UTF8.GetBytes(text))
                        .ToList();
                    lock (primaryJournal)
                    {
                        primaryJournal.Commit(ulong.MaxValue, new SessionCapture(updatedState, openPayloads, closedPayloads));
                    }
                }
                session.ReleaseJournalIfUnused(primaryJournal);
            }
            catch
            {
            }
        }
    }

    private void SyncActiveWindowTheme(nuint wParam = 1)
    {
        int activation = (int)((ulong)wParam & 0xFFFF);
        if (activation == WA_INACTIVE) return;

        WorkspaceSession.Current.LastActiveWindow = this;
        string themeName = CurrentWorkspaceEntry()?.Theme ?? _settings.Theme;
        if (ThemePalette.CurrentKind != ThemePalette.ParseKind(themeName))
        {
            ApplyActiveTheme(themeName);
        }
        else if (_hwnd != 0)
        {
            Win32.SetWindowBorderColor(_hwnd, ThemePalette.Chrome);
            InvalidateChrome();
        }
    }

    private void PositionWindowAtScreenPoint(Win32.POINT point)
    {
        if (_hwnd == 0) return;
        Win32.RECT winRect;
        if (!Win32.GetWindowRect(_hwnd, &winRect)) return;

        int width = Math.Max(S(760), winRect.Width);
        int height = Math.Max(S(350), winRect.Height);
        int x = point.X - S(65);
        int y = point.Y - S(18);

        nint monitor = MonitorFromPoint(point, MONITOR_DEFAULTTONEAREST);
        if (monitor != 0)
        {
            var mi = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
            if (GetMonitorInfoW(monitor, &mi))
            {
                var work = mi.rcWork;
                width = Math.Min(width, Math.Max(S(760), work.Width));
                height = Math.Min(height, Math.Max(S(350), work.Height));
                x = Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - width));
                y = Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - height));
            }
        }

        Win32.MoveWindow(_hwnd, x, y, width, height, true);
    }

    private void ReorderTabBefore(ulong draggedId, ulong beforeId)
    {
        int from = _documents.FindIndex(d => d.Id == draggedId);
        if (from < 0) return;

        var tab = _documents[from];
        _documents.RemoveAt(from);

        int to = beforeId == 0 ? _documents.Count : _documents.FindIndex(d => d.Id == beforeId);
        if (to < 0) to = _documents.Count;
        _documents.Insert(to, tab);
        InvalidateChrome();
    }
}
