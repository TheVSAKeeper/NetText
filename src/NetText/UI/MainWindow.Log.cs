using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using NetText.Core;
using NetText.Native;

namespace NetText.UI;

internal sealed unsafe partial class MainWindow
{
    private const uint MB_YESNOCANCEL = 0x00000003;
    private const int IDCANCEL = 2;
    private const uint BS_AUTOCHECKBOX = 0x0003;
    private const uint BM_GETCHECK = 0x00F0;
    private const nint BST_CHECKED = 1;
    private const uint WM_GETFONT = 0x0031;

    private readonly Dictionary<ulong, nint> _pageEditors = [];
    private Win32.RECT _logActionsAnchor;

    private int BottomPanelsTop(int statusTop)
    {
        if (_showHome || !HasDocument) return statusTop;
        int top = S(TopChromeLogicalHeight());
        int filterH = GetLogFilterPanelHeight();
        if (ActiveDocument.Log is { } log)
        {
            int height = S(LogNavigationLogicalHeight) + filterH;
            if (log.SearchOpen) height += Math.Min(S(LogSearchLogicalHeight), Math.Max(S(91), statusTop - top - height - S(65)));
            return Math.Max(top, statusTop - height);
        }
        int searchH = _searchOpen ? Math.Min(S(EditorSearchLogicalHeight), Math.Max(S(92), statusTop - top - filterH - S(70))) : 0;
        return Math.Max(top, statusTop - searchH - filterH);
    }

    private void PaintLogToolbar(nint hdc, int width, int top, LogViewerState log)
    {
        var doc = _documents.Find(d => ReferenceEquals(d.Log, log));
        int y = top + S(5);
        int btnH = S(26);
        int x = S(12);
        int gap = S(6);

        string title = doc?.CustomTitle ?? FileName(log.FilePath);
        int maxTitleW = Math.Max(S(100), Math.Min(S(260), width / 4));
        int titleW = Math.Min(maxTitleW, MeasureTextWidth(hdc, title, _uiFont));
        var titleRect = new Win32.RECT { Left = x, Right = x + titleW, Top = y, Bottom = y + btnH };
        Win32.SelectObject(hdc, _uiFont);
        Win32.SetTextColor(hdc, ThemePalette.Text);
        Win32.DrawTextW(hdc, title, -1, &titleRect,
            Win32.DT_SINGLELINE | Win32.DT_VCENTER | Win32.DT_END_ELLIPSIS | Win32.DT_NOPREFIX);
        x += titleW + S(8);

        string modeLabel = log.IsDraft && (log.IsEditingPage || log.EditsPagesByDefault)
            ? "РЕДАКТИРОВАНИЕ ЧЕРНОВИКА"
            : log.EditsPagesByDefault || log.IsEditingPage
                ? "РЕДАКТИРОВАНИЕ"
                : "ТОЛЬКО ЧТЕНИЕ";
        int pillW = MeasureTextWidth(hdc, modeLabel, _smallFont) + S(14);
        var pillRect = new Win32.RECT { Left = x, Top = y + S(2), Right = x + pillW, Bottom = y + btnH - S(2) };
        FillRoundRect(hdc, pillRect, S(4), ThemePalette.Field, ThemePalette.Border);
        Win32.SelectObject(hdc, _smallFont);
        Win32.SetTextColor(hdc, log.IsEditingPage || log.EditsPagesByDefault ? ThemePalette.Accent : ThemePalette.Muted);
        Win32.DrawTextW(hdc, modeLabel, -1, &pillRect, Win32.DT_CENTER | Win32.DT_VCENTER | Win32.DT_SINGLELINE | Win32.DT_NOPREFIX);
        x += pillW + S(8);

        if (!log.IsConsole && !log.EditsPagesByDefault)
        {
            string editText = log.IsEditingPage ? "Только чтение" : "Редактировать";
            bool editDisabled = log.IsSavingPage || log.PreparingTrim || log.IsWritingFile || log.IsFiltered || log.Page is null;
            x = DrawButton(hdc, x, y, btnH, editText, HitKind.LogEditPage, 0, editDisabled, log.IsEditingPage) + gap;
        }

        if (log.IsEditingPage)
        {
            bool saveDisabled = !log.HasPendingEdit || log.IsSavingPage;
            x = DrawButton(hdc, x, y, btnH, "Сохранить страницу", HitKind.LogSavePage, 0, saveDisabled, false) + gap;
            bool cancelDisabled = !log.IsSavingPage && log.EditsPagesByDefault && !log.HasPendingEdit;
            string cancelLabel = log.IsSavingPage ? "Остановить" : "Отменить";
            _ = DrawButton(hdc, x, y, btnH, cancelLabel, HitKind.LogCancelPage, 0, cancelDisabled, false);
        }

        string statusText = log.ConsoleDisconnected
            ? "● Источник отключён"
            : log.Bookmark.AutoRefresh
                ? "● Обновляется"
                : "● На паузе";
        uint statusColor = log.ConsoleDisconnected
            ? ThemePalette.Danger
            : log.Bookmark.AutoRefresh
                ? ThemePalette.Success
                : ThemePalette.Warning;

        string pauseText = log.Bookmark.AutoRefresh ? "Пауза" : "Продолжить";
        int pauseW = MeasureButtonWidth(hdc, pauseText);
        int statusW = MeasureTextWidth(hdc, statusText, _smallFont) + S(6);
        int rightPad = S(12);
        int pauseX = Math.Max(x + gap, width - rightPad - pauseW);
        int statusX = Math.Max(x + gap, pauseX - gap - statusW);

        var stRect = new Win32.RECT { Left = statusX, Top = y, Right = statusX + statusW, Bottom = y + btnH };
        Win32.SelectObject(hdc, _smallFont);
        Win32.SetTextColor(hdc, statusColor);
        Win32.DrawTextW(hdc, statusText, -1, &stRect, Win32.DT_RIGHT | Win32.DT_VCENTER | Win32.DT_SINGLELINE | Win32.DT_NOPREFIX);

        bool pauseDisabled = log.EditsPagesByDefault || log.IsEditingPage || log.ConsoleDisconnected;
        DrawButtonFixed(hdc, pauseX, y, pauseW, btnH, pauseText, HitKind.LogAutoRefresh, 0, pauseDisabled, !log.Bookmark.AutoRefresh);
    }

    private void PaintLogBottom(nint hdc, int width, int top, int statusTop, LogViewerState log)
    {
        int filterH = GetLogFilterPanelHeight();
        int height = S(26);
        int pad = S(12);
        int gap = S(4);
        int navTop = statusTop - S(LogNavigationLogicalHeight);

        if (_logFilterOpen)
        {
            PaintLogLevelFilterPanel(hdc, width, navTop - filterH);
        }

        DrawHLine(hdc, 0, navTop, width, ThemePalette.Border);
        int y = navTop + S(5);
        bool navBlocked = !log.IsEnabled;
        bool filtered = log.IsFiltered;
        bool prevDisabled = navBlocked || (filtered ? !log.CanFilterPrevious : log.Bookmark.Offset <= log.Bookmark.Hidden);
        bool nextDisabled = navBlocked || (filtered ? !log.CanFilterNext : log.Page?.Eof != false);

        DrawButtonFixed(hdc, pad, y, S(70), height, "Начало", HitKind.LogStart, 0, navBlocked || filtered, false);
        DrawButtonFixed(hdc, S(87), y, S(72), height, "Назад", HitKind.LogPrev, 0, prevDisabled, false);
        DrawButtonFixed(hdc, S(164), y, S(78), height, "Дальше", HitKind.LogNext, 0, nextDisabled, false);
        DrawButtonFixed(hdc, S(247), y, S(80), height, "Конец", HitKind.LogFollowEnd, 0,
            navBlocked || filtered || log.EditsPagesByDefault, log.Bookmark.FollowEnd);

        // Space S(335)..S(405) is occupied by _logJumpEdit positioned in MainWindow.cs
        int x = DrawButton(hdc, S(415), y, height, "Перейти, %", HitKind.LogJumpPercent, 0, navBlocked || filtered, false) + gap;
        x = DrawButton(hdc, x, y, height, "50%", HitKind.LogJump50, 0, navBlocked || filtered, false) + gap;
        x = DrawButton(hdc, x, y, height, "Ко времени", HitKind.LogTime, 0, navBlocked || filtered || log.SearchProgress is not null, false) + gap;
        x = DrawButton(hdc, x, y, height, "Поиск в логе", HitKind.LogSearchOpen, 0, navBlocked || filtered, log.SearchOpen) + gap;
        string levelsLabel = _logFilterOpen ? "Закрыть уровни" : "Уровни…";
        x = DrawButton(hdc, x, y, height, levelsLabel, HitKind.LogLevelsButton, 0, log.IsEditingPage && !log.EditsPagesByDefault, _logFilterOpen || log.IsFiltered) + gap;

        int actStart = x;
        x = DrawButton(hdc, x, y, height, "Действия ▾", HitKind.LogActionsMenu, 0, false, false) + gap;
        _logActionsAnchor = new Win32.RECT { Left = actStart, Top = y, Right = x - gap, Bottom = y + height };

        if (log.PreparingTrim || log.IsWritingFile)
        {
            DrawButton(hdc, x, y, height, "Отменить обрезку", HitKind.LogCancelTrim, 0, false, false);
        }

        if (!log.SearchOpen) return;
        int queryWidth = Math.Clamp(width / 3, S(160), S(320));
        int sx = pad + queryWidth + S(8);
        int sy = top + S(8);
        bool searching = log.SearchProgress is not null;
        sx = DrawButton(hdc, sx, sy, height, "Найти", HitKind.LogFindNew, 0, navBlocked || filtered || searching, false) + gap;
        sx = DrawButton(hdc, sx, sy, height, "Ко времени", HitKind.LogTime, 0, navBlocked || filtered || searching, false) + gap;
        sx = DrawButton(hdc, sx, sy, height, "Ещё", HitKind.LogFindMore, 0, navBlocked || filtered || searching || !log.CanSearchMore, false) + gap;
        DrawButton(hdc, sx, sy, height, "Отмена", HitKind.LogFindCancel, 0, !searching || log.SearchStopping, false);
        DrawButtonFixed(hdc, width - pad - S(28), sy, S(28), height, "×", HitKind.LogFindClose, 0, false, false);

        sx = pad;
        sy = top + S(37);
        sx = DrawButton(hdc, sx, sy, height, ".*", HitKind.LogFindRegex, 0, false, log.SearchRegex) + gap;
        sx = DrawButton(hdc, sx, sy, height, "Aa", HitKind.LogFindCase, 0, false, log.SearchCase) + gap;
        sx = DrawButton(hdc, sx, sy, height, "Целое слово", HitKind.LogFindWhole, 0, false, log.SearchWholeWord) + S(10);
        var label = new Win32.RECT { Left = sx, Right = width - pad, Top = sy, Bottom = sy + height };
        Win32.SetTextColor(hdc, ThemePalette.Muted);
        Win32.SelectObject(hdc, _smallFont);
        Win32.DrawTextW(hdc, log.SearchStatus, -1, &label, Win32.DT_SINGLELINE | Win32.DT_VCENTER | Win32.DT_END_ELLIPSIS | Win32.DT_NOPREFIX);
    }

    private void ShowLogActionsMenu(Document doc) => ShowLogActionsMenu(doc, _logActionsAnchor);

    private void ShowLogActionsMenu(Document doc, Win32.RECT anchor)
    {
        if (doc.Log is not { } log) return;
        var items = new List<(string Label, Action Run, bool Enabled)>();

        if (log.NeedsIndexOptIn)
        {
            items.Add(("Индексировать весь файл", log.StartFullIndexOptIn, true));
        }
        else if (log.IndexProgress is not null)
        {
            items.Add(("Индексирование (пауза)", log.ToggleIndex, true));
        }
        else if (log.Index.ScannedTo < log.Bookmark.Size)
        {
            items.Add(("Продолжить индекс", log.ToggleIndex, true));
        }
        else
        {
            items.Add(("Индексирование завершено", static () => { }, false));
        }

        items.Add((_wrap ? "Перенос: вкл." : "Перенос: выкл.", ToggleWrap, true));

        bool canNav = log.IsEnabled && !log.IsFiltered;
        if (log.Bookmark.Hidden > 0)
        {
            items.Add(("Показать весь файл", log.RevealAll, canNav));
        }
        else
        {
            items.Add(("Скрыть начало", () => ShowLogTrim(log, false), canNav));
        }

        if (!log.IsConsole && !log.IsDraft && !log.EditsPagesByDefault)
        {
            items.Add(("Удалить начало…", () => ShowLogTrim(log, true), canNav));
        }

        ShowMenu(anchor, [.. items]);
    }

    private void ShowLogTrim(LogViewerState log, bool physical)
    {
        string? percent = null;
        ToolDialog.Show(_hwnd, physical ? "Удалить начало лога" : "Скрыть начало лога", 390, 180, dialog =>
        {
            dialog.Label("Доля начальной части файла в процентах", 16, 14, 348);
            var input = dialog.Edit(log.TrimPercentInput, 16, 40, 345);
            dialog.Button("Продолжить", 221, 80, 140, () =>
            {
                var text = ToolDialog.Text(input);
                if (!byte.TryParse(text, out byte value) || value is < 1 or > 99)
                    throw new FormatException("Введите целое число от 1 до 99.");
                percent = text;
                dialog.Close();
            }, true);
        });
        if (percent is null) return;
        log.TrimPercentInput = percent;
        if (physical) log.StartPhysicalTrim();
        else log.StartHideStart();
    }

    private void EnterLogPageEdit(Document doc)
    {
        if (doc.Log is not { } log || log.IsConsole || log.IsFiltered) return;
        if (log.IsEditingPage)
        {
            if (log.HasPendingEdit)
            {
                log.Notice = "Сохраните или отмените правки страницы.";
                InvalidateChrome();
            }
            else
            {
                CancelLogPageEdit(doc);
            }
            return;
        }

        log.BeginPageEdit(snapshot =>
        {
            if (!_documents.Contains(doc)) return;
            ShowPageEditorHost(doc, snapshot.OriginalText);
        });
    }

    private void ShowPageEditorHost(Document doc, string text)
    {
        if (doc.Viewport is null) return;
        if (!_pageEditors.TryGetValue(doc.Id, out nint pageHwnd) || pageHwnd == 0)
        {
            pageHwnd = ScintillaInterop.CreateEditorControl(_hwnd, _hInstance, (int)MathF.Round(_fontSize), _wrap);
            _pageEditors[doc.Id] = pageHwnd;
        }

        ScintillaInterop.SetTextUtf8(pageHwnd, Encoding.UTF8.GetBytes(text));
        ScintillaInterop.ApplyLanguage(pageHwnd, doc.File?.Path, (int)MathF.Round(_fontSize));
        ScintillaInterop.SetWrap(pageHwnd, _wrap);
        ScintillaInterop.SetSavePoint(pageHwnd);

        SyncPageEditorBounds(doc, pageHwnd);
        Win32.ShowWindow(doc.Viewport.Hwnd, Win32.SW_HIDE);
        Win32.ShowWindow(pageHwnd, Win32.SW_SHOW);
        Win32.SetFocus(pageHwnd);
        InvalidateChrome();
    }

    private void SyncPageEditorBounds(Document doc, nint pageHwnd)
    {
        if (doc.Viewport is null || pageHwnd == 0) return;
        Win32.RECT rc;
        Win32.GetWindowRect(doc.Viewport.Hwnd, &rc);
        var tl = new Win32.POINT { X = rc.Left, Y = rc.Top };
        var br = new Win32.POINT { X = rc.Right, Y = rc.Bottom };
        Win32.ScreenToClient(_hwnd, &tl);
        Win32.ScreenToClient(_hwnd, &br);
        Win32.MoveWindow(pageHwnd, tl.X, tl.Y, Math.Max(0, br.X - tl.X), Math.Max(0, br.Y - tl.Y), true);
    }

    private void SaveLogPageEdit(Document doc)
    {
        if (doc.Log is not { IsEditingPage: true } log) return;
        if (!_pageEditors.TryGetValue(doc.Id, out nint pageHwnd) || pageHwnd == 0) return;
        string editedText = ScintillaInterop.GetTextString(pageHwnd);
        log.SavePageEdit(editedText, onSaved: _ =>
        {
            if (!_documents.Contains(doc)) return;
            if (doc.File is { } file)
            {
                doc.File = file with { Version = FileVersion.At(file.Path) };
            }
            if (!log.EditsPagesByDefault)
            {
                DestroyPageEditorHost(doc);
            }
            else
            {
                EnterLogPageEdit(doc);
            }
            UpdateLogMinimap(doc.Id, log);
        });
    }

    private void CancelLogPageEdit(Document doc)
    {
        if (doc.Log is not { } log) return;
        if (log.IsSavingPage)
        {
            log.CancelPageEdit();
            return;
        }
        if (log.EditsPagesByDefault && log.PageEditSnapshot is { } snap &&
            _pageEditors.TryGetValue(doc.Id, out nint pageHwnd) && pageHwnd != 0)
        {
            ScintillaInterop.SetTextUtf8(pageHwnd, Encoding.UTF8.GetBytes(snap.OriginalText));
            ScintillaInterop.SetSavePoint(pageHwnd);
            log.HasPendingEdit = false;
            InvalidateChrome();
            return;
        }
        DestroyPageEditorHost(doc);
        log.CancelPageEdit();
    }

    private void DestroyPageEditorHost(Document doc)
    {
        if (!_pageEditors.TryGetValue(doc.Id, out nint pageHwnd)) return;
        _pageEditors.Remove(doc.Id);
        if (pageHwnd != 0)
        {
            if (Win32.GetFocus() == pageHwnd)
            {
                if (doc.Viewport is not null) Win32.SetFocus(doc.Viewport.Hwnd);
                else Win32.SetFocus(_hwnd);
            }
            Win32.DestroyWindow(pageHwnd);
        }
        if (doc.Viewport is not null)
        {
            Win32.ShowWindow(doc.Viewport.Hwnd, Win32.SW_SHOW);
            if (_activeId == doc.Id && !_showHome)
            {
                Win32.SetFocus(doc.Viewport.Hwnd);
            }
        }
    }

    private bool ConfirmLeaveDirtyPageEdit(Document doc)
    {
        if (doc.Log is not { IsEditingPage: true, HasPendingEdit: true }) return true;
        int answer = Win32.MessageBoxW(
            _hwnd,
            $"На текущей странице «{doc.Name}» есть несохранённые правки.",
            "Сохранить страницу?",
            MB_YESNOCANCEL | Win32.MB_ICONWARNING);
        if (answer == Win32.IDYES)
        {
            SaveLogPageEdit(doc);
            return false;
        }
        if (answer == Win32.IDNO)
        {
            CancelLogPageEdit(doc);
            return true;
        }
        return false;
    }

    private bool HandlePageEditCut(Document doc)
    {
        if (!_pageEditors.TryGetValue(doc.Id, out nint pageHwnd) || pageHwnd == 0) return false;
        if (Win32.GetFocus() != pageHwnd) return false;
        var (start, end, _) = ScintillaInterop.GetSelection(pageHwnd);
        if (start != end) return false;
        ScintillaInterop.Send(pageHwnd, ScintillaInterop.SCI_LINECUT, 0, 0);
        if (doc.Log is { } log)
        {
            log.UpdatePendingPageEdit(ScintillaInterop.GetTextString(pageHwnd));
        }
        return true;
    }

    private void ShowAttachConsoleDialog()
    {
        if (!OperatingSystem.IsWindows())
        {
            _error = "Подключение к консоли доступно в Windows.";
            InvalidateChrome();
            return;
        }

        string? query = null;
        ToolDialog.Show(_hwnd, "Подключиться к консоли", 460, 185, dialog =>
        {
            dialog.Label("Имя процесса или PID в классической консоли Windows", 16, 14, 420);
            var input = dialog.Edit("", 16, 42, 420);
            dialog.Button("Подключиться", 286, 86, 150, () =>
            {
                string text = ToolDialog.Text(input).Trim();
                if (string.IsNullOrEmpty(text))
                    throw new FormatException("Укажите имя процесса или PID.");
                query = text;
                dialog.Close();
            }, true);
            dialog.Button("Отмена", 140, 86, 134, dialog.Close);
        });

        if (string.IsNullOrWhiteSpace(query)) return;

        IReadOnlyList<TargetProcessMatch> matches;
        try
        {
            matches = ConsoleCapture.FindTargetProcesses(query);
        }
        catch (Exception ex)
        {
            _error = $"Не удалось найти процесс. {ex.Message}";
            InvalidateChrome();
            return;
        }

        if (matches.Count == 0)
        {
            _error = "Процесс с этим именем или PID не найден.";
            InvalidateChrome();
            return;
        }

        TargetProcessMatch? selected = matches.Count == 1 ? matches[0] : null;
        if (selected is null)
        {
            ToolDialog.Show(_hwnd, "Выбрать процесс", 460, 290, dialog =>
            {
                dialog.Label($"Найдено несколько процессов ({matches.Count}). Выберите PID:", 16, 14, 420);
                nint list = 0;
                list = dialog.List(16, 40, 420, 150, () =>
                {
                    int idx = ToolDialog.Selection(list);
                    if ((uint)idx < (uint)matches.Count)
                    {
                        selected = matches[idx];
                        dialog.Close();
                    }
                });
                ToolDialog.SetItems(list, matches.Select(m => $"{m.ProcessName} (PID {m.ProcessId})"));
                dialog.Button("Подключиться", 286, 204, 150, () =>
                {
                    int idx = ToolDialog.Selection(list);
                    if ((uint)idx >= (uint)matches.Count)
                        throw new InvalidOperationException("Выберите процесс из списка.");
                    selected = matches[idx];
                    dialog.Close();
                }, true);
                dialog.Button("Отмена", 140, 204, 134, dialog.Close);
            });
        }

        if (selected is null) return;
        int pid = selected.ProcessId;
        string title = $"{selected.ProcessName} ({pid})";
        _busy = $"Подключение к консоли {title}…";
        _error = null;
        InvalidateChrome();

        ThreadPool.QueueUserWorkItem(_ =>
        {
            ConsoleCaptureSession? session = null;
            Exception? connectError = null;
            try
            {
                session = ConsoleCaptureSession.ConnectAsync(pid, title).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                connectError = ex;
            }

            DispatchUi(() =>
            {
                _busy = null;
                if (connectError is not null || session is null)
                {
                    _error = $"Не удалось подключиться к консоли {title}. {connectError?.Message}";
                    InvalidateChrome();
                    return;
                }

                var bookmark = new LogBookmark { AutoRefresh = true, FollowEnd = true };
                AddLogDocument(new FileTarget(session.Path, null), windows1251: false, bookmark, restored: false);
                var doc = _documents[^1];
                doc.Name = title;
                doc.CustomTitle = title;
                doc.IsConsole = true;
                doc.ConsoleSession = session;
                if (doc.Log is { } log)
                {
                    log.IsConsole = true;
                }

                _ = session.Completion.ContinueWith(t => DispatchUi(() =>
                {
                    if (session.IsDisposed || !_documents.Contains(doc)) return;
                    Exception? err = t.IsFaulted ? t.Exception?.GetBaseException() : t.Result;
                    string msg = err is null
                        ? "Подключённый процесс завершил работу."
                        : $"Подключение к консоли завершено с ошибкой. {err.Message}";
                    doc.Log?.MarkConsoleDisconnected(msg);
                }), TaskScheduler.Default);

                InvalidateChrome();
            });
        });
    }

    private bool? ChooseLargeFileModeDialog(string path, long sizeBytes)
    {
        if (_settings.OpenLargeFilesAsLog is { } remembered)
        {
            return remembered;
        }

        long mib = Math.Max(1, (long)Math.Round(sizeBytes / (1024.0 * 1024.0)));
        bool? result = null;
        bool remember = false;

        ToolDialog.Show(_hwnd, "Открытие большого файла", 500, 220, dialog =>
        {
            var titleLbl = dialog.Label($"{FileName(path)} · {LineNumbers.Format(mib)} МиБ", 16, 14, 460);
            dialog.Label("Файл больше 100 МиБ. Откройте его как лог или в постраничном редакторе.", 16, 42, 460, 36);

            nint hFont = Win32.SendMessageW(titleLbl, WM_GETFONT, 0, 0);
            nint checkHwnd = Win32.CreateWindowExW(
                0,
                "BUTTON",
                "Запомнить выбор",
                Win32.WS_CHILD | Win32.WS_VISIBLE | Win32.WS_TABSTOP | BS_AUTOCHECKBOX,
                (int)Math.Round(16 * (_dpi / 96.0)),
                (int)Math.Round(86 * (_dpi / 96.0)),
                (int)Math.Round(220 * (_dpi / 96.0)),
                (int)Math.Round(24 * (_dpi / 96.0)),
                dialog.Handle,
                901,
                _hInstance,
                null);
            if (hFont != 0) Win32.SendMessageW(checkHwnd, Win32.WM_SETFONT, (nuint)hFont, 1);

            void Accept(bool asLog)
            {
                remember = Win32.SendMessageW(checkHwnd, BM_GETCHECK, 0, 0) == BST_CHECKED;
                result = asLog;
                dialog.Close();
            }

            dialog.Button("Открыть как лог", 16, 130, 155, () => Accept(true), primary: true);
            dialog.Button("Открыть в редакторе", 181, 130, 175, () => Accept(false));
            dialog.Button("Отмена", 366, 130, 110, () => { result = null; dialog.Close(); });
        });

        if (result is { } chosen && remember)
        {
            _settings = _settings with { OpenLargeFilesAsLog = chosen };
            try { _settings.Save(); } catch (Exception ex) { _error = ex.Message; }
        }

        return result;
    }

    private void ToggleActiveDocumentLogMode()
    {
        if (_busy is not null || !HasDocument || _showHome) return;
        var doc = ActiveDocument;

        if (doc.Log is { } log)
        {
            if (doc.IsConsole) return;
            if (!ConfirmLeaveDirtyPageEdit(doc)) return;

            if (doc.File is { } file)
            {
                long fileBytes = file.Version?.Bytes ?? (long)log.Bookmark.Size;
                if (fileBytes > Files.MaxEditorBytes)
                {
                    if (log.EditsPagesByDefault)
                    {
                        DestroyPageEditorHost(doc);
                        log.EditsPagesByDefault = false;
                        doc.EditPages = false;
                        log.CancelPageEdit();
                    }
                    else
                    {
                        log.EditsPagesByDefault = true;
                        doc.EditPages = true;
                        EnterLogPageEdit(doc);
                    }
                    InvalidateChrome();
                    return;
                }

                bool win1251 = log.Windows1251;
                string path = file.Path;
                RemoveTab(doc.Id);
                OpenPaths([path], win1251, viewAsLog: false, autoDetectLog: false);
            }
            return;
        }

        if (doc.File is { } textFile && !doc.Dirty)
        {
            bool win1251 = doc.Format.Encoding == TextEncoding.Windows1251;
            string path = textFile.Path;
            DestroyEditorFilterPreview(doc);
            RemoveTab(doc.Id);
            OpenPaths([path], win1251, viewAsLog: true);
            return;
        }

        byte[] bytes = ScintillaInterop.CopyUtf8Bytes(doc.EditorHwnd);
        string draftPath = Path.Combine(Path.GetTempPath(), $"nettext-draft-{Guid.NewGuid():N}.log");
        File.WriteAllBytes(draftPath, bytes);
        string draftTitle = doc.Name;
        DestroyEditorFilterPreview(doc);
        RemoveTab(doc.Id);
        AddLogDocument(new FileTarget(draftPath, FileVersion.At(draftPath)), windows1251: false, new LogBookmark { AutoRefresh = false }, restored: false);
        var newDoc = _documents[^1];
        newDoc.Name = draftTitle;
        newDoc.CustomTitle = draftTitle;
        newDoc.IsExternalDraft = true;
        if (newDoc.Log is { } draftLog)
        {
            draftLog.IsDraft = true;
        }
        InvalidateChrome();
    }
}
