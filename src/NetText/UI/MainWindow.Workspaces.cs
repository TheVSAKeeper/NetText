using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using NetText.Core;
using NetText.Native;

namespace NetText.UI;

internal sealed unsafe partial class MainWindow
{
    private const uint WPF_RESTORETOMAXIMIZED = 0x0002;
    private const int SW_SHOWNORMAL = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPLACEMENT
    {
        public uint length;
        public uint flags;
        public uint showCmd;
        public Win32.POINT ptMinPosition;
        public Win32.POINT ptMaxPosition;
        public Win32.RECT rcNormalPosition;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowPlacement(nint hWnd, WINDOWPLACEMENT* lpwndpl);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPlacement(nint hWnd, WINDOWPLACEMENT* lpwndpl);

    internal string _workspaceId = WorkspaceCatalogService.DefaultWorkspaceId;
    internal string _windowId = WorkspaceCatalogService.PrimaryWindowId;
    internal bool _startEmpty;
    internal bool _isDetachedWindow;
    internal bool _closingWindow;
    private Win32.RECT _lastNormalWindowRect;
    private bool _hasLastNormalWindowRect;

    internal string? CurrentJournalDirectory => _sessionJournal?.DirectoryPath;

    internal bool UsesJournal(SessionJournal journal) => ReferenceEquals(_sessionJournal, journal);

    internal void RefreshWorkspaceChrome()
    {
        UpdateWindowTitle();
        InvalidateChrome();
    }

    private RecoveredSession? InitializeWorkspaceState(
        string? workspaceId,
        string? windowId,
        bool startEmpty,
        bool isDetached,
        bool shareDefaultSession = false)
    {
        _workspaceId = string.IsNullOrWhiteSpace(workspaceId) ? WorkspaceCatalogService.DefaultWorkspaceId : workspaceId;
        _windowId = string.IsNullOrWhiteSpace(windowId) ? WorkspaceCatalogService.PrimaryWindowId : windowId;
        _startEmpty = startEmpty;
        _isDetachedWindow = isDetached;

        var session = WorkspaceSession.Current;
        WorkspaceEntry? entry = null;
        try
        {
            var catalog = session.EnsureCatalog();
            if (!_startEmpty)
            {
                entry = catalog.Workspaces.FirstOrDefault(item => item.Id == _workspaceId);
                if (entry is null)
                {
                    _workspaceId = WorkspaceCatalogService.DefaultWorkspaceId;
                    entry = catalog.Workspaces.FirstOrDefault(item => item.Id == _workspaceId);
                }
            }
        }
        catch (Exception ex)
        {
            _error ??= $"Каталог рабочих пространств недоступен. {ex.Message}";
        }

        string initialTheme = entry?.Theme ?? _settings.Theme;
        ThemePalette.Apply(initialTheme);

        string journalWorkspace = _startEmpty ? WorkspaceCatalogService.DefaultWorkspaceId : _workspaceId;
        string journalWindow = _startEmpty ? WorkspaceCatalogService.EmptyStartupWindowId : _windowId;

        RecoveredSession? recovered = null;
        try
        {
            _sessionJournal = session.GetOrOpenJournal(journalWorkspace, journalWindow, shareDefaultSession);
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

        return recovered;
    }

    private void UpdateWindowTitle()
    {
        if (_hwnd == 0) return;
        string title;
        if (_startEmpty)
        {
            title = "NetText – Пустое окно";
        }
        else
        {
            var entry = CurrentWorkspaceEntry();
            title = entry is null || entry.Id == WorkspaceCatalogService.DefaultWorkspaceId
                ? "NetText"
                : $"NetText – {entry.Name}";
        }
        Win32.SetWindowTextW(_hwnd, title);
    }

    private void TrackNormalWindowBounds()
    {
        if (_hwnd == 0) return;
        var wp = new WINDOWPLACEMENT { length = (uint)sizeof(WINDOWPLACEMENT) };
        if (GetWindowPlacement(_hwnd, &wp) && wp.rcNormalPosition.Width > 0 && wp.rcNormalPosition.Height > 0)
        {
            _lastNormalWindowRect = wp.rcNormalPosition;
            _hasLastNormalWindowRect = true;
        }
    }

    private void ApplyWorkspaceWindowBounds(SessionWindow window)
    {
        if (_hwnd == 0) return;
        TrackNormalWindowBounds();

        int w = S((int)MathF.Round(Math.Max(760, window.Width)));
        int h = S((int)MathF.Round(Math.Max(350, window.Height)));
        int left = _hasLastNormalWindowRect ? _lastNormalWindowRect.Left : 0;
        int top = _hasLastNormalWindowRect ? _lastNormalWindowRect.Top : 0;
        if (!_hasLastNormalWindowRect)
        {
            Win32.RECT cur;
            Win32.GetWindowRect(_hwnd, &cur);
            left = cur.Left;
            top = cur.Top;
        }

        var wp = new WINDOWPLACEMENT
        {
            length = (uint)sizeof(WINDOWPLACEMENT),
            flags = window.Maximized ? WPF_RESTORETOMAXIMIZED : 0,
            showCmd = window.Maximized ? (uint)Win32.SW_MAXIMIZE : SW_SHOWNORMAL,
            rcNormalPosition = new Win32.RECT
            {
                Left = left,
                Top = top,
                Right = left + w,
                Bottom = top + h,
            },
        };
        _lastNormalWindowRect = wp.rcNormalPosition;
        _hasLastNormalWindowRect = true;
        SetWindowPlacement(_hwnd, &wp);
    }

    private void ShowCreateWorkspaceDialog()
    {
        if (_busy is not null) return;
        var session = WorkspaceSession.Current;
        int selectedColorIndex = 0;
        string selectedHex = WorkspaceColors[0].Hex;
        uint selectedBgr = WorkspaceColors[0].Bgr;

        ToolDialog.Show(_hwnd, "Новая рабочая область", 460, 356, dialog =>
        {
            dialog.Label("Имя", 16, 12, 428, 20);
            nint nameEdit = dialog.Edit("", 16, 34, 428, 26);

            dialog.Label("Символ", 16, 68, 428, 20);
            nint symbolEdit = dialog.Edit(WorkspaceSymbols[0], 16, 90, 52, 26);
            int symX = 76;
            foreach (string choice in WorkspaceSymbols)
            {
                string picked = choice;
                dialog.Button(picked, symX, 89, 34, 28, () => ToolDialog.SetText(symbolEdit, picked));
                symX += 38;
            }

            dialog.Label("Цвет рамки", 16, 126, 428, 20);
            nint colorStatus = dialog.Label($"{WorkspaceColors[0].Name} ({selectedHex})", 16, 222, 428, 20);
            var swatchButtons = new nint[WorkspaceColors.Length];

            void RefreshColorSelection()
            {
                for (int i = 0; i < swatchButtons.Length; i++)
                {
                    ToolDialog.SetText(swatchButtons[i], i == selectedColorIndex ? "✓" : "");
                }

                string label = selectedColorIndex >= 0
                    ? $"{WorkspaceColors[selectedColorIndex].Name} ({selectedHex})"
                    : $"Свой цвет ({selectedHex})";
                ToolDialog.SetText(colorStatus, label);
            }

            for (int i = 0; i < WorkspaceColors.Length; i++)
            {
                int idx = i;
                int col = i % 8;
                int row = i / 8;
                swatchButtons[i] = dialog.Button(
                    i == 0 ? "✓" : "",
                    16 + col * 36,
                    150 + row * 32,
                    32,
                    28,
                    () =>
                    {
                        selectedColorIndex = idx;
                        selectedHex = WorkspaceColors[idx].Hex;
                        selectedBgr = WorkspaceColors[idx].Bgr;
                        RefreshColorSelection();
                    });
            }

            dialog.Button("+ Свой цвет", 312, 150, 132, 28, () =>
            {
                if (TryPickCustomColor(dialog.Handle, selectedBgr, out string pickedHex) &&
                    TryParseHexColorToBgr(pickedHex, out uint pickedBgr))
                {
                    selectedColorIndex = -1;
                    selectedHex = pickedHex;
                    selectedBgr = pickedBgr;
                    RefreshColorSelection();
                }
            });

            nint errorLabel = dialog.Label("", 16, 250, 428, 28);

            dialog.Button("Отмена", 224, 286, 106, dialog.Close);
            dialog.Button("Создать", 338, 286, 106, () =>
            {
                string rawName = ToolDialog.Text(nameEdit);
                string rawSymbol = ToolDialog.Text(symbolEdit).Trim();
                string? validatedSymbol = string.IsNullOrEmpty(rawSymbol)
                    ? null
                    : WorkspaceCatalogService.ValidateSymbol(rawSymbol);

                try
                {
                    var (created, updatedCatalog) = session.Catalog.CreateWithCatalogAsync(
                        rawName,
                        ThemePalette.CurrentName,
                        CancellationToken.None,
                        symbol: validatedSymbol,
                        color: selectedHex).GetAwaiter().GetResult();
                    session.PublishCatalog(updatedCatalog);
                    dialog.Close();
                    SwitchToWorkspace(created.Id);
                }
                catch (Exception ex)
                {
                    ToolDialog.SetText(errorLabel, ex.Message);
                }
            }, primary: true);
        });
        FocusActiveEditor();
    }

    private void ShowManageWorkspacesDialog()
    {
        if (_busy is not null) return;
        var session = WorkspaceSession.Current;
        var catalog = session.Catalog.LoadAsync().GetAwaiter().GetResult();
        session.CachedCatalog = catalog;

        ToolDialog.Show(_hwnd, "Рабочие пространства", 680, 490, dialog =>
        {
            dialog.Label("Активные рабочие пространства", 16, 12, 648, 20);
            nint activeList = 0;

            nint switchBtn = 0;
            nint newWinBtn = 0;
            nint dupBtn = 0;
            nint renameBtn = 0;
            nint deleteBtn = 0;

            dialog.Label("Удалённые пространства", 16, 276, 648, 20);
            nint deletedList = 0;
            nint restoreBtn = 0;

            WorkspaceEntry? SelectedActive()
            {
                int idx = ToolDialog.Selection(activeList);
                return (uint)idx < (uint)catalog.Workspaces.Count ? catalog.Workspaces[idx] : null;
            }

            DeletedWorkspaceEntry? SelectedDeleted()
            {
                var deleted = catalog.DeletedWorkspaces ?? [];
                int idx = ToolDialog.Selection(deletedList);
                return (uint)idx < (uint)deleted.Count ? deleted[idx] : null;
            }

            void SyncButtons()
            {
                var sel = SelectedActive();
                bool hasSel = sel is not null;
                bool isCurrent = sel is not null && !_startEmpty && sel.Id == _workspaceId;
                bool isDefault = sel?.Id == WorkspaceCatalogService.DefaultWorkspaceId;
                bool isOpen = sel is not null && session.IsWorkspaceOpen(sel.Id);

                ToolDialog.SetEnabled(switchBtn, hasSel && !isCurrent);
                ToolDialog.SetEnabled(newWinBtn, hasSel);
                ToolDialog.SetEnabled(dupBtn, hasSel);
                ToolDialog.SetEnabled(renameBtn, hasSel && !isDefault);
                ToolDialog.SetEnabled(deleteBtn, hasSel && !isDefault && !isOpen);
                ToolDialog.SetEnabled(restoreBtn, SelectedDeleted() is not null);
            }

            void ReloadLists()
            {
                catalog = session.Catalog.LoadAsync().GetAwaiter().GetResult();
                session.PublishCatalog(catalog);

                ToolDialog.SetItems(activeList, catalog.Workspaces.Select(w =>
                {
                    string glyph = w.Symbol ?? DefaultWorkspaceSymbol;
                    bool current = !_startEmpty && w.Id == _workspaceId;
                    bool openElsewhere = !current && session.IsWorkspaceOpen(w.Id);
                    string suffix = current ? " · открыто здесь" : openElsewhere ? " · открыто" : "";
                    return $"{glyph}  {w.Name}{suffix}";
                }));

                int currentIndex = catalog.Workspaces.FindIndex(w => !_startEmpty && w.Id == _workspaceId);
                if (currentIndex >= 0) ToolDialog.SetSelection(activeList, currentIndex);

                var deleted = catalog.DeletedWorkspaces ?? [];
                ToolDialog.SetItems(deletedList, deleted.Select(d =>
                {
                    string glyph = d.Workspace.Symbol ?? DefaultWorkspaceSymbol;
                    var when = DateTimeOffset.FromUnixTimeMilliseconds(d.DeletedAtMs).ToLocalTime();
                    return $"{glyph}  {d.Workspace.Name} · {when:g}";
                }));

                SyncButtons();
            }

            activeList = dialog.ListBox(16, 36, 460, 190, onSelect: SyncButtons, onDoubleClick: () =>
            {
                if (SelectedActive() is { } sel && (_startEmpty || sel.Id != _workspaceId))
                {
                    dialog.Close();
                    SwitchToWorkspace(sel.Id);
                }
            });
            deletedList = dialog.ListBox(16, 300, 460, 120, onSelect: SyncButtons);

            switchBtn = dialog.Button("Переключить", 488, 36, 176, 29, () =>
            {
                if (SelectedActive() is not { } sel) return;
                dialog.Close();
                SwitchToWorkspace(sel.Id);
            }, primary: true);

            newWinBtn = dialog.Button("Новое окно", 488, 72, 176, 29, () =>
            {
                if (SelectedActive() is not { } sel) return;
                dialog.Close();
                OpenWorkspaceInNewWindow(sel.Id);
            });

            dupBtn = dialog.Button("Дублировать…", 488, 108, 176, 29, () =>
            {
                if (SelectedActive() is not { } sel) return;
                string? copyName = PromptWorkspaceText(dialog.Handle, "Дублировать рабочее пространство", "Имя копии", sel.Name + " (копия)");
                if (copyName is null) return;
                var created = DuplicateWorkspaceSync(sel, copyName);
                dialog.Close();
                SwitchToWorkspace(created.Id);
            });

            renameBtn = dialog.Button("Переименовать…", 488, 144, 176, 29, () =>
            {
                if (SelectedActive() is not { } sel || sel.Id == WorkspaceCatalogService.DefaultWorkspaceId) return;
                string? nextName = PromptWorkspaceText(dialog.Handle, "Переименовать пространство", "Новое имя", sel.Name);
                if (nextName is null) return;
                session.Catalog.RenameAsync(sel.Id, nextName).GetAwaiter().GetResult();
                ReloadLists();
            });

            deleteBtn = dialog.Button("Удалить", 488, 180, 176, 29, () =>
            {
                if (SelectedActive() is not { } sel || sel.Id == WorkspaceCatalogService.DefaultWorkspaceId) return;
                if (session.IsWorkspaceOpen(sel.Id))
                {
                    throw new InvalidOperationException("Закройте окна с этим пространством перед удалением.");
                }

                int answer = Win32.MessageBoxW(
                    dialog.Handle,
                    $"Удалить «{sel.Name}»? Сохранённые вкладки и черновики можно будет восстановить из истории удалённых пространств.",
                    "Удалить рабочее пространство?",
                    Win32.MB_YESNO | Win32.MB_ICONWARNING);
                if (answer != Win32.IDYES) return;

                session.Catalog.DeleteAsync(sel.Id, _settings.WorkspaceHistoryLimit).GetAwaiter().GetResult();
                ReloadLists();
            });

            dialog.Button("Сохранить текущее как…", 16, 234, 210, 29, () =>
            {
                string? newName = PromptWorkspaceText(dialog.Handle, "Новое рабочее пространство", "Имя пространства", "");
                if (newName is null) return;
                var created = CreateWorkspaceFromCurrentSync(newName);
                dialog.Close();
                SwitchToWorkspace(created.Id);
            });

            restoreBtn = dialog.Button("Восстановить", 488, 300, 176, 29, () =>
            {
                if (SelectedDeleted() is not { } sel) return;
                session.Catalog.RestoreDeletedAsync(sel.Workspace.Id).GetAwaiter().GetResult();
                ReloadLists();
            });

            dialog.Button("Закрыть", 542, 428, 122, 29, dialog.Close);

            ReloadLists();
        });
        FocusActiveEditor();
    }

    private static string? PromptWorkspaceText(nint owner, string title, string label, string initialValue)
    {
        string? result = null;
        ToolDialog.Show(owner, title, 420, 168, dialog =>
        {
            dialog.Label(label, 16, 12, 388, 22);
            nint edit = dialog.Edit(initialValue, 16, 38, 388, 26);
            dialog.Button("Отмена", 180, 82, 108, dialog.Close);
            dialog.Button("ОК", 296, 82, 108, () =>
            {
                result = ToolDialog.Text(edit).Trim();
                dialog.Close();
            }, primary: true);
        });
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }

    private WorkspaceEntry DuplicateWorkspaceSync(WorkspaceEntry source, string copyName)
    {
        var session = WorkspaceSession.Current;
        if (source.Id == _workspaceId && !_startEmpty)
        {
            CommitCurrentSessionSync();
        }

        string sourceDir = session.Catalog.GetSessionDirectory(source.Id, _windowId);
        if (_windowId != WorkspaceCatalogService.PrimaryWindowId && !HasCheckpointDirectory(sourceDir))
        {
            sourceDir = session.Catalog.GetSessionDirectory(source.Id, WorkspaceCatalogService.PrimaryWindowId);
        }

        var (copy, catalog) = session.Catalog.CreateWithCatalogAsync(
            copyName,
            source.Theme ?? ThemePalette.CurrentName,
            CancellationToken.None,
            symbol: source.Symbol,
            color: source.Color).GetAwaiter().GetResult();

        try
        {
            if (HasCheckpointDirectory(sourceDir))
            {
                CopyLatestCheckpointDirectory(sourceDir, session.Catalog.GetSessionDirectory(copy.Id, _windowId));
                if (_windowId != WorkspaceCatalogService.PrimaryWindowId)
                {
                    CopyLatestCheckpointDirectory(sourceDir, session.Catalog.GetSessionDirectory(copy.Id, WorkspaceCatalogService.PrimaryWindowId));
                }
            }
        }
        catch
        {
            session.PublishCatalog(session.Catalog.DiscardIncompleteCopyAsync(copy.Id).GetAwaiter().GetResult());
            throw;
        }

        session.PublishCatalog(catalog);
        return copy;
    }

    private WorkspaceEntry CreateWorkspaceFromCurrentSync(string name)
    {
        var session = WorkspaceSession.Current;
        CommitCurrentSessionSync();
        var current = CurrentWorkspaceEntry();
        var (created, catalog) = session.Catalog.CreateWithCatalogAsync(
            name,
            ThemePalette.CurrentName,
            CancellationToken.None,
            symbol: current?.Symbol,
            color: current?.Color).GetAwaiter().GetResult();

        try
        {
            string sourceDir = CurrentJournalDirectory ?? session.Catalog.GetSessionDirectory(_workspaceId, _windowId);
            if (HasCheckpointDirectory(sourceDir))
            {
                CopyLatestCheckpointDirectory(sourceDir, session.Catalog.GetSessionDirectory(created.Id, _windowId));
                if (_windowId != WorkspaceCatalogService.PrimaryWindowId)
                {
                    CopyLatestCheckpointDirectory(sourceDir, session.Catalog.GetSessionDirectory(created.Id, WorkspaceCatalogService.PrimaryWindowId));
                }
            }
        }
        catch
        {
            session.PublishCatalog(session.Catalog.DiscardIncompleteCopyAsync(created.Id).GetAwaiter().GetResult());
            throw;
        }

        session.PublishCatalog(catalog);
        return created;
    }

    private void OpenWorkspaceInNewWindow(string workspaceId)
    {
        var session = WorkspaceSession.Current;
        var catalog = session.EnsureCatalog();
        if (!catalog.Workspaces.Any(w => w.Id == workspaceId))
        {
            throw new KeyNotFoundException("Рабочее пространство не найдено.");
        }

        if (!_startEmpty && _workspaceId == workspaceId)
        {
            CommitCurrentSessionSync();
        }

        string sourceDir = (!_startEmpty && _workspaceId == workspaceId && CurrentJournalDirectory is not null)
            ? CurrentJournalDirectory
            : session.Catalog.GetSessionDirectory(workspaceId, WorkspaceCatalogService.PrimaryWindowId);

        string newWindowId = "window:" + Guid.NewGuid().ToString("N");
        string targetDir = session.Catalog.GetSessionDirectory(workspaceId, newWindowId);
        if (HasCheckpointDirectory(sourceDir))
        {
            CopyLatestCheckpointDirectory(sourceDir, targetDir);
        }

        session.Catalog.SetWindowWorkspaceAsync(newWindowId, workspaceId).GetAwaiter().GetResult();
        session.PublishCatalog(session.Catalog.LoadAsync().GetAwaiter().GetResult());
        OpenAdditionalWindow(workspaceId, newWindowId, startEmpty: false, isDetached: false);
    }

    private void SwitchToEmptyWindow() =>
        SwitchToWorkspaceCore(WorkspaceCatalogService.DefaultWorkspaceId, enterEmpty: true);

    private void SwitchToWorkspace(string workspaceId) =>
        SwitchToWorkspaceCore(workspaceId, enterEmpty: false);

    private void SwitchToWorkspaceCore(string workspaceId, bool enterEmpty)
    {
        if (_busy is not null || _closingWindow || _documents.Any(doc => doc.Log?.IsWritingFile == true))
        {
            return;
        }

        if (enterEmpty)
        {
            if (_startEmpty || _windowId != WorkspaceCatalogService.PrimaryWindowId) return;
        }
        else if (!_startEmpty && workspaceId == _workspaceId)
        {
            return;
        }

        var dirtyFiles = _documents.Where(doc => doc.Dirty && doc.File is not null).ToArray();
        if (dirtyFiles.Length > 0)
        {
            string? choice = PromptDirtySwitchAction(dirtyFiles.Length);
            if (choice is null) return;
            if (choice == "save" && !SaveDirtyFilesBeforeSwitch(dirtyFiles))
            {
                return;
            }
        }

        var session = WorkspaceSession.Current;
        var catalog = session.EnsureCatalog();
        WorkspaceEntry? targetEntry = null;
        if (!enterEmpty)
        {
            targetEntry = catalog.Workspaces.FirstOrDefault(w => w.Id == workspaceId)
                ?? throw new KeyNotFoundException("Рабочее пространство не найдено.");
        }

        TrackNormalWindowBounds();
        CommitCurrentSessionSync();

        if (!enterEmpty)
        {
            session.Catalog.SetWindowWorkspaceAsync(_windowId, workspaceId).GetAwaiter().GetResult();
            session.PublishCatalog(session.Catalog.LoadAsync().GetAwaiter().GetResult());
            targetEntry = session.CachedCatalog?.Workspaces.FirstOrDefault(w => w.Id == workspaceId) ?? targetEntry;
        }

        var previousJournal = _sessionJournal;
        _sessionJournal = null;

        CloseAllDocumentsForWorkspaceSwitch();
        session.ReleaseJournalIfUnused(previousJournal);

        _workspaceId = enterEmpty ? WorkspaceCatalogService.DefaultWorkspaceId : workspaceId;
        _startEmpty = enterEmpty;
        _isDetachedWindow = false;

        string targetJournalWorkspace = enterEmpty ? WorkspaceCatalogService.DefaultWorkspaceId : _workspaceId;
        string targetJournalWindow = enterEmpty ? WorkspaceCatalogService.EmptyStartupWindowId : _windowId;

        RecoveredSession? recovered = null;
        try
        {
            _sessionJournal = session.GetOrOpenJournal(targetJournalWorkspace, targetJournalWindow);
            recovered = _sessionJournal.Restore();
            _error = recovered?.Warning;
        }
        catch (Exception ex)
        {
            _error = $"Сохранение сессии недоступно. {ex.Message}";
        }

        if (recovered is not null && !enterEmpty)
        {
            _wrap = recovered.State.Wrap;
            _fontSize = Math.Clamp(recovered.State.FontSize, MinFontSize, MaxFontSize);
            RestoreSession(recovered);
            ApplyWorkspaceWindowBounds(recovered.State.Window);
        }
        else
        {
            _showHome = true;
            UpdateHistory();
        }

        string targetTheme = targetEntry?.Theme ?? _settings.Theme;
        ApplyActiveTheme(targetTheme);
        UpdateWindowTitle();
        LayoutAndSyncControls();
        FocusActiveEditor();
        InvalidateChrome();
    }

    private string? PromptDirtySwitchAction(int dirtyCount)
    {
        string? answer = null;
        ToolDialog.Show(_hwnd, "Переключить рабочее пространство?", 480, 175, dialog =>
        {
            dialog.Label(
                $"Открытые файлы содержат несохранённые изменения ({dirtyCount}). Сохранить перед переключением или оставить черновиками?",
                16, 14, 448, 44);
            dialog.Button("Сохранить", 16, 86, 130, () => { answer = "save"; dialog.Close(); }, primary: true);
            dialog.Button("Оставить черновики", 156, 86, 176, () => { answer = "drafts"; dialog.Close(); });
            dialog.Button("Отмена", 342, 86, 122, () => { answer = null; dialog.Close(); });
        });
        return answer;
    }

    private bool SaveDirtyFilesBeforeSwitch(Document[] dirtyFiles)
    {
        foreach (var doc in dirtyFiles)
        {
            if (doc.File is null || doc.Log is not null || doc.EditorHwnd == 0) continue;
            try
            {
                byte[] snapshot = ScintillaInterop.CopyUtf8Bytes(doc.EditorHwnd);
                var icon = DesktopIcon.Capture(doc.File);
                var savedTarget = Files.Save(doc.File, doc.Format, snapshot);
                if (icon is { } pos) DesktopIcon.Restore(savedTarget, pos);
                try { new FileVersions().Capture(savedTarget, doc.Format.Encoding, _settings); } catch { }
                doc.File = savedTarget;
                doc.Placeholder = false;
                doc.DiskStatus = DiskStatusKind.Current;
                doc.Dirty = false;
                ScintillaInterop.SetSavePoint(doc.EditorHwnd);
                PropagateSharedDocumentSaved(doc);
            }
            catch (Exception ex)
            {
                _error = $"Не удалось сохранить «{doc.Name}». {ex.Message}";
                InvalidateChrome();
                return false;
            }
        }

        return true;
    }

    private void CommitCurrentSessionSync()
    {
        if (_sessionJournal is null) return;
        var capture = CaptureSessionForWorkspacePersistence();
        var queued = _sessionWriter.Queue(capture);
        ulong rev = queued?.Revision ?? ulong.MaxValue;
        var cap = queued?.Capture ?? capture;
        try
        {
            lock (_sessionJournal)
            {
                _sessionJournal.Commit(rev, cap);
            }
            _sessionWriter.Complete(rev, cap, success: true);
        }
        catch
        {
            _sessionWriter.Complete(rev, cap, success: false);
        }
    }

    private SessionCapture CaptureSessionForWorkspacePersistence()
    {
        var capture = CaptureSession();
        if (_hasLastNormalWindowRect)
        {
            var normalW = Math.Max(760f, _lastNormalWindowRect.Width / Scale);
            var normalH = Math.Max(350f, _lastNormalWindowRect.Height / Scale);
            bool maximized = Win32.IsZoomed(_hwnd);
            if (maximized)
            {
                capture = capture with
                {
                    State = capture.State with
                    {
                        Window = new SessionWindow(normalW, normalH, true),
                    },
                };
            }
        }
        return capture;
    }

    private void CloseAllDocumentsForWorkspaceSwitch()
    {
        // Always clear _activeId BEFORE removing from _documents and destroying child Scintilla HWNDs.
        _activeId = 0;
        _searchOpen = false;
        _searchGen++;
        _searchInFlight = false;
        _lastSearchHit = null;
        _scrollDrag = null;
        _tabScrollX = 0;
        _tabDragOffsets.Clear();
        _editorIndicators.Clear();
        _mapSamples.Clear();
        _scrollBounds.Clear();

        foreach (var doc in _documents)
        {
            doc.Log?.Dispose();
            doc.FileWatch?.Dispose();
            doc.ConsoleSession?.Dispose();
            if (doc.FilterPreviewHwnd != 0)
            {
                Win32.DestroyWindow(doc.FilterPreviewHwnd);
                doc.FilterPreviewHwnd = 0;
            }
            if (doc.EditorHwnd != 0)
            {
                Win32.DestroyWindow(doc.EditorHwnd);
                doc.EditorHwnd = 0;
            }
        }

        _documents.Clear();
        _closed.Clear();
        _panes = new SessionPane(0);
        _groupTabs.Clear();
        _groupBounds.Clear();
        _groupHeaderBounds.Clear();
        _groupTabScrollX.Clear();
        _groupMaxScrollX.Clear();
        _dividers.Clear();
        _activeGroup = 0;
        _nextGroup = 1;
    }

    private static bool HasCheckpointDirectory(string sessionDir)
    {
        if (!Directory.Exists(sessionDir)) return false;
        return Directory.EnumerateDirectories(sessionDir, "checkpoint-*").Any();
    }

    private static void CopyLatestCheckpointDirectory(string sourceSessionDir, string targetSessionDir)
    {
        string? latest = Directory.EnumerateDirectories(sourceSessionDir, "checkpoint-*")
            .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
            .FirstOrDefault();
        if (latest is null) return;

        Directory.CreateDirectory(targetSessionDir);
        string pending = Path.Combine(targetSessionDir, $".pending-{Guid.NewGuid():N}");
        string targetCheckpoint = Path.Combine(targetSessionDir, "checkpoint-00000000000000000001");
        Directory.CreateDirectory(pending);
        try
        {
            foreach (string file in Directory.EnumerateFiles(latest))
            {
                string name = Path.GetFileName(file);
                if (string.Equals(name, "session.lock", StringComparison.OrdinalIgnoreCase)) continue;
                File.Copy(file, Path.Combine(pending, name), overwrite: true);
            }

            if (Directory.Exists(targetCheckpoint))
            {
                Directory.Delete(targetCheckpoint, recursive: true);
            }
            Directory.Move(pending, targetCheckpoint);
        }
        finally
        {
            if (Directory.Exists(pending))
            {
                try { Directory.Delete(pending, recursive: true); } catch { }
            }
        }
    }
}
