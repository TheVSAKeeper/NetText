using System.Globalization;
using System.IO;
using System.Text;
using NetText.Core;
using NetText.Native;

namespace NetText.UI;

internal sealed unsafe partial class MainWindow
{
    private const uint MF_CHECKED = 0x00000008;

    private sealed class PopupMenuItem
    {
        public string? Label;
        public Action? Run;
        public bool Enabled = true;
        public bool Checked;
        public bool Separator;
        public List<PopupMenuItem>? Submenu;

        public static PopupMenuItem Sep() => new() { Separator = true };
        public static PopupMenuItem Cmd(string label, Action run, bool enabled = true, bool @checked = false) =>
            new() { Label = label, Run = run, Enabled = enabled, Checked = @checked };
        public static PopupMenuItem Group(string label, params PopupMenuItem[] children) =>
            new() { Label = label, Submenu = [.. children] };
    }

    partial void OnDocumentAutoRefreshToggled(Document doc);
    partial void OnMoveTabToNewWindow(Document doc);

    private void ShowFileMenu(Win32.RECT anchor)
    {
        var doc = !HasDocument || _showHome ? null : ActiveDocument;
        ShowMenu(anchor,
            ("Новая вкладка\tCtrl+N", NewFile, _busy is null),
            ("Открыть…\tCtrl+O", () => OpenPicker(false, false), _busy is null),
            ("Открыть в Windows-1251…", () => OpenPicker(true, false), _busy is null),
            ("Открыть как лог…", () => OpenPicker(false, true), _busy is null),
            ("Открыть лог в Windows-1251…", () => OpenPicker(true, true), _busy is null),
            ("Сохранить\tCtrl+S", () => Save(false), _busy is null && doc is { Log: null }),
            ("Сохранить как…\tCtrl+Shift+S", () => Save(true), _busy is null && doc is { Log: null }),
            ("Недавние файлы и закрытые вкладки…", () => ShowRecentMenu(anchor), _busy is null),
            ("Подключиться к консоли…", ShowAttachConsoleDialog, _busy is null),
            ("История файлов", ToggleHome, true),
            ("Настроить открытие файлов в Windows…", () =>
            {
                WindowsIntegration.RegisterFileAssociations();
                WindowsIntegration.OpenDefaultApps();
            }, true));
    }

    private void ShowToolsMenu(Win32.RECT anchor)
    {
        var doc = !HasDocument || _showHome ? null : ActiveDocument;
        bool text = doc is { Log: null, Placeholder: false };
        bool hasActiveDoc = doc is not null;
        bool idle = _busy is null;

        var editorItems = new List<PopupMenuItem>
        {
            PopupMenuItem.Cmd("Перейти к строке…\tCtrl+G", GoToLine, text),
            PopupMenuItem.Cmd("Перенос строк", ToggleWrap, true, _wrap),
            PopupMenuItem.Group("Масштаб текста",
                PopupMenuItem.Cmd("Увеличить", () => Zoom(1), hasActiveDoc),
                PopupMenuItem.Cmd("Уменьшить", () => Zoom(-1), hasActiveDoc),
                PopupMenuItem.Cmd("Сбросить", () => Zoom(12.0f - _fontSize), hasActiveDoc)),
            PopupMenuItem.Cmd("Разделить вправо", () => SplitGroup(true), hasActiveDoc && idle),
            PopupMenuItem.Cmd("Разделить вниз", () => SplitGroup(false), hasActiveDoc && idle),
            PopupMenuItem.Cmd("Уровни лога…", ToggleLogLevelsPanel, doc is { Placeholder: false }),
        };

        if (doc?.Log is { } log)
        {
            editorItems.Add(PopupMenuItem.Sep());
            editorItems.Add(PopupMenuItem.Cmd("Обновить лог", () => log.Navigate(LogDestination.AtOffset(log.Bookmark.Offset)), log.IsEnabled));
            editorItems.Add(PopupMenuItem.Cmd("Пауза / продолжение индексации", log.ToggleIndex, true));
            editorItems.Add(PopupMenuItem.Cmd("Сбросить индекс", log.ResetIndex, true));
            editorItems.Add(PopupMenuItem.Cmd("Скрыть начало…", () => ShowLogTrim(log, false), log.IsEnabled));
            editorItems.Add(PopupMenuItem.Cmd("Удалить начало…", () => ShowLogTrim(log, true), log.IsEnabled));
            editorItems.Add(PopupMenuItem.Cmd("Показать скрытое начало", log.RevealAll, log.IsEnabled));
        }

        var ghostItems = new List<PopupMenuItem>();
        for (int i = _closed.Count - 1; i >= 0; i--)
        {
            var (tab, bytes) = _closed[i];
            if (tab.File is not null || SessionJournal.IsKnownEmptyUntitledDraft(tab, bytes)) continue;
            ulong tabId = tab.Id;
            string name = !string.IsNullOrWhiteSpace(tab.CustomTitle) ? tab.CustomTitle : ClosedTabLabel(tab, bytes);
            string label = name + HistoryMetadata(_settings.ShowDraftLineCount ? tab.LineCount : null, tab.LastWorked);
            ghostItems.Add(PopupMenuItem.Cmd(label, () => ReopenClosedTab(tabId), idle));
        }
        if (ghostItems.Count == 0)
        {
            ghostItems.Add(PopupMenuItem.Cmd("Призрачных вкладок нет", static () => { }, enabled: false));
        }

        var rootItems = new PopupMenuItem[]
        {
            PopupMenuItem.Group("Редактор и просмотр", [.. editorItems]),
            PopupMenuItem.Group("Форматирование и данные",
                PopupMenuItem.Cmd("Форматировать JSON\tCtrl+Shift+J", FormatActiveDocumentJson, text && idle),
                PopupMenuItem.Cmd(doc?.JsonDisabled == true ? "Включить проверку JSON" : "Отключить проверку JSON", ToggleJsonValidation, text),
                PopupMenuItem.Sep(),
                PopupMenuItem.Cmd("Генератор строк…", ShowLineGenerator, idle),
                PopupMenuItem.Cmd("Сравнить списки…", ShowListComparison, idle)),
            PopupMenuItem.Group("История и черновики",
                PopupMenuItem.Cmd("Версии файла…", ShowFileVersions, text && doc?.File is not null && idle),
                PopupMenuItem.Sep(),
                new PopupMenuItem { Label = "Призрачные вкладки", Submenu = ghostItems },
                PopupMenuItem.Cmd("Архив черновиков…", ShowDraftArchiveDialog, idle)),
            PopupMenuItem.Group("Обновления",
                PopupMenuItem.Cmd("Проверить обновления…", () => StartUpdateCheck(manual: true), idle),
                PopupMenuItem.Cmd("История выпусков…", ShowReleaseHistoryDialog, idle)),
            PopupMenuItem.Cmd("Найти окно процесса…", ShowWindowFinder, true),
            PopupMenuItem.Sep(),
            PopupMenuItem.Cmd("Настройки…", ShowEditorSettings, idle),
        };

        ShowNestedMenu(anchor, rootItems);
    }

    private void ShowThemeMenu(Win32.RECT anchor)
    {
        void ApplyThemeChoice(string name)
        {
            ApplyActiveTheme(name);
            _settings = (_settings with { Theme = name }).Normalize();
            try { _settings.Save(); }
            catch (Exception error) { _error = error.Message; }
        }

        var current = ThemePalette.CurrentKind;
        ShowNestedMenu(anchor,
            PopupMenuItem.Cmd("Фокус", () => ApplyThemeChoice("focus"), true, current == ThemeKind.Focus),
            PopupMenuItem.Cmd("Неон", () => ApplyThemeChoice("neon"), true, current == ThemeKind.Neon),
            PopupMenuItem.Cmd("Vice City", () => ApplyThemeChoice("vicecity"), true, current == ThemeKind.ViceCity));
    }

    private void ShowTabMenu(int x, int y)
    {
        var hit = HitTest(x, y);
        if (hit is not { Kind: HitKind.TabSelect or HitKind.GroupTab }) return;
        var doc = _documents.Find(value => value.Id == hit.Value.TabId);
        if (doc is null) return;
        var point = new Win32.RECT { Left = x, Bottom = y };
        var items = new List<PopupMenuItem>
        {
            PopupMenuItem.Cmd("Переименовать вкладку…", () => RenameTabDialog(doc), _busy is null),
            PopupMenuItem.Cmd("Режим лога", () =>
            {
                if (_activeId != doc.Id) SelectTab(doc.Id);
                ToggleActiveDocumentLogMode();
            }, _busy is null && !doc.Placeholder && !doc.IsConsole, doc.Log is not null),
        };

        if (doc.File is not null && doc.Log is null)
        {
            items.Add(PopupMenuItem.Cmd("Автообновление с диска", () =>
            {
                doc.AutoRefresh = !doc.AutoRefresh;
                OnDocumentAutoRefreshToggled(doc);
                QueueSessionTick();
                InvalidateChrome();
            }, _busy is null, doc.AutoRefresh));
        }

        items.Add(PopupMenuItem.Cmd("Сохранить копию…", () => SaveDocumentCopy(doc), _busy is null && doc.Log is null && !doc.Placeholder));
        items.Add(PopupMenuItem.Cmd("Переместить в новое окно", () => OnMoveTabToNewWindow(doc), _busy is null && _documents.Count > 0));
        items.Add(PopupMenuItem.Sep());
        items.Add(PopupMenuItem.Cmd("Показать в Проводнике", () => WindowsIntegration.RevealFile(doc.File!.Path), doc.File is not null));
        items.Add(PopupMenuItem.Cmd("Скопировать путь", () => Win32.CopyText(_hwnd, doc.File!.Path), doc.File is not null));
        items.Add(PopupMenuItem.Sep());
        items.Add(PopupMenuItem.Cmd("Закрыть вкладку", () => CloseTab(doc.Id), _busy is null));

        ShowNestedMenu(point, [.. items]);
    }

    private void RenameTabDialog(Document doc)
    {
        string current = doc.CustomTitle ?? doc.Name;
        ToolDialog.Show(_hwnd, "Переименовать вкладку", 420, 170, dialog =>
        {
            dialog.Label("Название вкладки (оставьте пустым для имени по умолчанию)", 16, 12, 388, 22);
            nint edit = dialog.Edit(current, 16, 40, 388, 26);
            dialog.Button("Отмена", 180, 82, 108, dialog.Close);
            dialog.Button("Сохранить", 296, 82, 108, () =>
            {
                string next = ToolDialog.Text(edit).Trim();
                doc.CustomTitle = string.IsNullOrEmpty(next) ? null : next;
                if (doc.CustomTitle is not null)
                {
                    doc.Name = doc.CustomTitle;
                }
                else if (doc.File is { } file)
                {
                    doc.Name = FileName(file.Path);
                }
                else
                {
                    UpdateUntitledName(doc);
                }
                QueueSessionTick();
                InvalidateChrome();
                dialog.Close();
            }, primary: true);
        });
        FocusActiveEditor();
    }

    private void SaveDocumentCopy(Document doc)
    {
        if (doc.Log is not null || doc.Placeholder || _busy is not null) return;
        string initialDir = doc.File is not null ? Path.GetDirectoryName(doc.File.Path) ?? "" : "";
        string suggested = doc.File is not null ? doc.Name : "Без имени.txt";
        string? pickedPath = PickSavePath(_hwnd, suggested, initialDir, "Сохранить копию");
        if (pickedPath is null) return;

        byte[] bytes = ScintillaInterop.CopyUtf8Bytes(doc.EditorHwnd);
        var format = doc.Format;
        Files.Save(FileTarget.FromPicker(pickedPath), format, bytes);
    }

    private void ShowMenu(Win32.RECT anchor, params (string Label, Action Run, bool Enabled)[] items)
    {
        nint menu = Win32.CreatePopupMenu();
        int selected;
        try
        {
            for (int i = 0; i < items.Length; i++)
                Win32.AppendMenuW(menu, Win32.MF_STRING | (items[i].Enabled ? 0 : Win32.MF_GRAYED), (nuint)(i + 1), items[i].Label);
            var point = new Win32.POINT { X = anchor.Left, Y = anchor.Bottom };
            Win32.ClientToScreen(_hwnd, &point);
            selected = Win32.TrackPopupMenuEx(menu, Win32.TPM_RETURNCMD, point.X, point.Y, _hwnd, null);
        }
        finally { Win32.DestroyMenu(menu); }
        if (selected <= 0 || selected > items.Length || !items[selected - 1].Enabled) return;
        try { items[selected - 1].Run(); }
        catch (Exception error) { Win32.MessageBoxW(_hwnd, error.Message, "NetText", Win32.MB_ICONWARNING); }
    }

    private void ShowNestedMenu(Win32.RECT anchor, params PopupMenuItem[] items)
    {
        var actions = new Dictionary<int, (Action Run, bool Enabled)>();
        var unattached = new List<nint>();
        int nextCommandId = 1;

        nint Build(IReadOnlyList<PopupMenuItem> list)
        {
            nint hMenu = Win32.CreatePopupMenu();
            unattached.Add(hMenu);
            foreach (var item in list)
            {
                if (item.Separator)
                {
                    Win32.AppendMenuW(hMenu, Win32.MF_SEPARATOR, 0, "");
                    continue;
                }
                if (item.Submenu is { } sub)
                {
                    nint child = Build(sub);
                    Win32.AppendMenuW(hMenu, Win32.MF_POPUP, (nuint)child, item.Label ?? "");
                    unattached.Remove(child);
                    continue;
                }
                int id = nextCommandId++;
                uint flags = Win32.MF_STRING;
                if (!item.Enabled) flags |= Win32.MF_GRAYED;
                if (item.Checked) flags |= MF_CHECKED;
                Win32.AppendMenuW(hMenu, flags, (nuint)id, item.Label ?? "");
                if (item.Run is not null) actions[id] = (item.Run, item.Enabled);
            }
            return hMenu;
        }

        nint root = 0;
        int selected = 0;
        try
        {
            root = Build(items);
            unattached.Remove(root);
            var point = new Win32.POINT { X = anchor.Left, Y = anchor.Bottom };
            Win32.ClientToScreen(_hwnd, &point);
            selected = Win32.TrackPopupMenuEx(root, Win32.TPM_RETURNCMD, point.X, point.Y, _hwnd, null);
        }
        finally
        {
            if (root != 0) Win32.DestroyMenu(root);
            foreach (nint orphan in unattached)
            {
                if (orphan != 0) Win32.DestroyMenu(orphan);
            }
        }

        if (selected > 0 && actions.TryGetValue(selected, out var entry) && entry.Enabled)
        {
            try { entry.Run(); }
            catch (Exception error) { Win32.MessageBoxW(_hwnd, error.Message, "NetText", Win32.MB_ICONWARNING); }
        }
    }

    private void ShowLineGenerator()
    {
        ToolDialog.Show(_hwnd, "Генератор строк", 560, 272, dialog =>
        {
            dialog.Label("Шаблон", 16, 12, 518);
            var pattern = dialog.Edit("{int_from_1} {guid} {rand_string}", 16, 35, 520);
            dialog.Label("Количество строк", 16, 73, 518);
            var count = dialog.Edit("100", 16, 96, 520);
            var status = dialog.Label("", 16, 135, 520, 32);
            CancellationTokenSource? running = null;
            nint generate = 0;
            nint toFile = 0;
            void Cancel()
            {
                if (running is null) dialog.Close();
                else running.Cancel();
            }
            dialog.OnCancel(Cancel);
            void Start(bool file)
            {
                if (running is not null) return;
                if (!long.TryParse(ToolDialog.Text(count), NumberStyles.None, CultureInfo.InvariantCulture, out long rows) || rows == 0)
                    throw new FormatException("Введите положительное количество строк.");
                var template = new LineTemplate(ToolDialog.Text(pattern), rows);
                FileTarget? target = null;
                if (file || template.EstimatedBytes > LineTemplate.TabLimit)
                {
                    var path = PickSavePath(dialog.Handle, "Строки.txt", "", "Сохранить результат");
                    if (path is null) return;
                    target = FileTarget.FromPicker(path);
                }
                var cancel = new CancellationTokenSource();
                running = cancel;
                Win32.EnableWindow(generate, false);
                Win32.EnableWindow(toFile, false);
                Win32.SetWindowTextW(status, "Генерация…");
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    string? text = null;
                    string? message = null;
                    long nextUpdate = 0;
                    try
                    {
                        if (target is null)
                        {
                            using var buffer = new StringWriter(CultureInfo.InvariantCulture);
                            template.Write(buffer, cancel.Token);
                            text = buffer.ToString();
                        }
                        else template.WriteFile(target, cancel.Token, done =>
                        {
                            if (Environment.TickCount64 < nextUpdate) return;
                            nextUpdate = Environment.TickCount64 + 150;
                            DispatchUi(() => Win32.SetWindowTextW(status, $"Строк: {done:N0} из {rows:N0}"));
                        });
                    }
                    catch (OperationCanceledException) { message = "Генерация отменена."; }
                    catch (Exception error) { message = error.Message; }
                    DispatchUi(() =>
                    {
                        running = null;
                        cancel.Dispose();
                        Win32.EnableWindow(generate, true);
                        Win32.EnableWindow(toFile, true);
                        if (message is not null) Win32.SetWindowTextW(status, message);
                        else if (text is not null)
                        {
                            dialog.Close();
                            AddTextResult(text);
                        }
                        else Win32.SetWindowTextW(status, $"Файл сохранён. {target!.Path}");
                    });
                });
            }
            generate = dialog.Button("Создать", 16, 178, 135, () => Start(false), true);
            toFile = dialog.Button("В файл…", 165, 178, 135, () => Start(true));
            dialog.Button("Отмена", 400, 178, 136, Cancel);
        });
        FocusActiveEditor();
    }

    private void ShowListComparison()
    {
        ToolDialog.Show(_hwnd, "Сравнить списки", 760, 590, dialog =>
        {
            dialog.Label("Левый список", 16, 12, 350);
            dialog.Label("Правый список", 380, 12, 350);
            var left = dialog.Edit("", 16, 36, 350, 168, multiline: true);
            var right = dialog.Edit("", 380, 36, 350, 168, multiline: true);
            var status = dialog.Label("", 16, 218, 714);
            var result = dialog.Edit("", 16, 243, 714, 225, multiline: true, readOnly: true);
            string? report = null;
            dialog.Button("Сравнить", 16, 486, 145, () =>
            {
                var comparison = ListComparison.Compare(ToolDialog.Text(left), ToolDialog.Text(right));
                report = comparison.Render();
                Win32.SetWindowTextW(result, report.Replace("\n", "\r\n", StringComparison.Ordinal));
                Win32.SetWindowTextW(status, $"Общие: {comparison.Common.Length} · слева: {comparison.OnlyLeft.Length} · справа: {comparison.OnlyRight.Length}");
            }, true);
            dialog.Button("Копировать", 176, 486, 145, () => { if (report is not null) Win32.CopyText(dialog.Handle, report); });
            dialog.Button("Открыть во вкладке", 336, 486, 180, () =>
            {
                if (report is null) return;
                dialog.Close();
                AddTextResult(report);
            });
        });
        FocusActiveEditor();
    }

    private void ShowFileVersions()
    {
        var doc = ActiveDocument;
        if (doc.File is not { } file) return;
        var store = new FileVersions();
        var versions = store.List(file.Path);
        ToolDialog.Show(_hwnd, "Версии файла", 740, 610, dialog =>
        {
            dialog.Label(file.Path, 16, 12, 704);
            nint list = 0;
            nint compareCheck = 0;
            nint legend = 0;
            nint content = 0;
            string? currentSnapshotText = null;
            bool currentSnapshotKnownEncoding = false;

            void RenderPreview()
            {
                if (currentSnapshotText is null)
                {
                    ToolDialog.SetText(content, "");
                    ToolDialog.SetVisible(legend, false);
                    return;
                }

                bool showDiff = currentSnapshotKnownEncoding && ToolDialog.IsChecked(compareCheck);
                ToolDialog.SetVisible(legend, showDiff);
                if (!showDiff)
                {
                    Win32.SetWindowTextW(content, currentSnapshotText.ReplaceLineEndings("\r\n"));
                    return;
                }

                string editorText = ScintillaInterop.GetTextString(doc.EditorHwnd);
                var diff = VersionDiff.Compare(editorText, currentSnapshotText);
                var sb = new StringBuilder();
                foreach (var line in diff)
                {
                    string prefix = line.Kind switch
                    {
                        VersionDiffKind.Added => "+ ",
                        VersionDiffKind.Removed => "− ",
                        VersionDiffKind.Info => "· ",
                        _ => "  ",
                    };
                    sb.Append(prefix).Append(line.Text.TrimEnd('\r')).Append("\r\n");
                }
                Win32.SetWindowTextW(content, sb.ToString());
            }

            FileSnapshot? Selection()
            {
                int index = ToolDialog.Selection(list);
                return (uint)index < (uint)versions.Count ? versions[index] : null;
            }

            void LoadSelected()
            {
                if (Selection() is not { } selected)
                {
                    currentSnapshotText = null;
                    currentSnapshotKnownEncoding = false;
                    ToolDialog.SetEnabled(compareCheck, false);
                    ToolDialog.SetChecked(compareCheck, false);
                    RenderPreview();
                    return;
                }

                if (selected.Encoding == TextEncoding.None)
                {
                    currentSnapshotKnownEncoding = false;
                    currentSnapshotText = "У старой версии не записана кодировка. Сравнение и восстановление отключены.";
                    ToolDialog.SetChecked(compareCheck, false);
                    ToolDialog.SetEnabled(compareCheck, false);
                    RenderPreview();
                    return;
                }

                var (text, _) = store.Read(selected);
                currentSnapshotText = text;
                currentSnapshotKnownEncoding = true;
                ToolDialog.SetEnabled(compareCheck, true);
                RenderPreview();
            }

            list = dialog.ListBox(16, 39, 704, 126, onSelect: LoadSelected);
            compareCheck = dialog.CheckBox("Подсветить отличия от текущего текста", false, 16, 173, 340, 24, _ => RenderPreview());
            ToolDialog.SetEnabled(compareCheck, false);
            legend = dialog.Label("«+» – только в текущем тексте; «−» – только в версии; «·» – пояснение.", 16, 200, 704, 22);
            ToolDialog.SetVisible(legend, false);
            content = dialog.Edit("", 16, 226, 704, 274, multiline: true, readOnly: true);

            ToolDialog.SetItems(list, versions.Select(item =>
            {
                string enc = item.Encoding == TextEncoding.None ? "кодировка неизвестна" : item.Encoding.ToString();
                return $"{item.Captured.LocalDateTime:g} · {item.Size:N0} Б · {enc}";
            }));
            if (versions.Count > 0)
            {
                ToolDialog.SetSelection(list, 0);
                LoadSelected();
            }

            dialog.Button("Просмотр", 16, 514, 116, LoadSelected, true);
            dialog.Button("Восстановить", 146, 514, 145, () =>
            {
                if (Selection() is not { } selected) return;
                if (selected.Encoding == TextEncoding.None)
                    throw new IOException("Кодировка старой версии неизвестна. Выберите другую версию.");
                if (doc.Dirty || doc.Placeholder || FileVersion.At(file.Path) != file.Version)
                    throw new IOException("Сохраните правки или перечитайте изменившийся файл перед восстановлением.");
                var (text, format) = store.Read(selected);
                store.Capture(file, doc.Format.Encoding, _settings, required: true);
                if (FileVersion.At(file.Path) != file.Version) throw new IOException("Исходный файл изменился на диске.");
                ScintillaInterop.ReplaceByteRange(doc.EditorHwnd, 0, ScintillaInterop.GetByteLength(doc.EditorHwnd), text);
                doc.Format = format;
                _error = "Версия открыта в редакторе. Ctrl+S сохранит файл, Ctrl+Z отменит замену.";
                InvalidateChrome();
                dialog.Close();
            });
            dialog.Button("Лимиты…", 306, 514, 115, () => { dialog.Close(); DispatchUi(ShowEditorSettings); });
            dialog.Button("Закрыть", 598, 514, 122, dialog.Close);
        });
        FocusActiveEditor();
    }

    private void ShowWindowFinder()
    {
        ToolDialog.Show(_hwnd, "Найти окно процесса", 650, 390, dialog =>
        {
            var query = dialog.Edit("", 16, 16, 453);
            bool byPid = false;
            nint mode = 0;
            mode = dialog.Button("По заголовку", 484, 15, 135, () =>
            {
                byPid = !byPid;
                Win32.SetWindowTextW(mode, byPid ? "По PID" : "По заголовку");
            });
            var list = dialog.List(16, 57, 603, 204);
            var status = dialog.Label("", 16, 271, 603);
            IReadOnlyList<WindowsIntegration.Window> windows = [];
            dialog.Button("Найти", 16, 299, 126, () =>
            {
                windows = WindowsIntegration.FindWindows(ToolDialog.Text(query), byPid)
                    .Where(item => item.Handle != dialog.Handle && item.Handle != _hwnd).ToArray();
                ToolDialog.SetItems(list, windows.Select(window => $"{window.ProcessId} · {window.Title}"));
                Win32.SetWindowTextW(status, windows.Count == 0 ? "Окна не найдены." : $"Окон: {windows.Count}");
            }, true);
            dialog.Button("Вывести", 157, 299, 126, () =>
            {
                int index = ToolDialog.Selection(list);
                if ((uint)index >= (uint)windows.Count) return;
                var window = windows[index];
                if (WindowsIntegration.BringToFront(window))
                {
                    dialog.RestoreOwnerFocus = false;
                    dialog.Close();
                }
                else Win32.SetWindowTextW(status, "Windows отклонила вывод окна либо окно уже закрыто.");
            });
        });
    }

    private void AddTextResult(string text)
    {
        AddDocument(null);
        var doc = ActiveDocument;
        ScintillaInterop.ReplaceByteRange(doc.EditorHwnd, 0, 0, text);
        ScintillaInterop.SetSelection(doc.EditorHwnd, 0, 0, false);
        UpdateUntitledName(doc);
    }
}
