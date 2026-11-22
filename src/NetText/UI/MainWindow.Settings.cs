using System.Globalization;
using NetText.Core;
using NetText.Native;

namespace NetText.UI;

internal sealed unsafe partial class MainWindow
{
    private EditorSettings _settings = new();

    private void ShowEditorSettings()
    {
        List<WorkspaceEntry> workspaces;
        try
        {
            workspaces = WorkspaceCatalogService.OpenDefault().LoadAsync().GetAwaiter().GetResult().Workspaces;
        }
        catch
        {
            workspaces = [];
        }
        if (workspaces.Count == 0)
        {
            workspaces.Add(new WorkspaceEntry(WorkspaceCatalogService.DefaultWorkspaceId, WorkspaceCatalogService.DefaultWorkspaceName));
        }

        var initial = _settings with { WordWrap = _wrap, FontSize = _fontSize };

        ToolDialog.Show(_hwnd, "Настройки", 720, 470, dialog =>
        {
            nint sectionList = 0;

            nint edHeading = dialog.Label("Редактор", 214, 14, 480, 24);
            nint edThemeLabel = dialog.Label("Тема оформления", 214, 48, 220, 22);
            nint edThemeCombo = dialog.ComboBox(446, 45, 248, 180);
            ToolDialog.SetComboItems(edThemeCombo, ["Фокус", "Неон", "Vice City"],
                ThemePalette.ParseKind(initial.Theme) switch
                {
                    ThemeKind.Neon => 1,
                    ThemeKind.ViceCity => 2,
                    _ => 0,
                });
            nint edWordWrap = dialog.CheckBox("Переносить длинные строки", initial.WordWrap, 214, 84, 480, 24);
            nint edFontLabel = dialog.Label("Размер текста (8–48 px)", 214, 118, 220, 22);
            nint edFontEdit = dialog.Edit(initial.FontSize.ToString("0.#", CultureInfo.CurrentCulture), 446, 115, 248, 26);
            nint edMinimap = dialog.CheckBox("Показывать миникарту", initial.Minimap, 214, 154, 480, 24);
            nint[] editorControls = [edHeading, edThemeLabel, edThemeCombo, edWordWrap, edFontLabel, edFontEdit, edMinimap];

            nint flHeading = dialog.Label("Файлы и версии", 214, 14, 480, 24);
            nint flLargeLabel = dialog.Label("Файлы больше 100 МиБ", 214, 48, 220, 22);
            nint flLargeCombo = dialog.ComboBox(446, 45, 248, 180);
            ToolDialog.SetComboItems(flLargeCombo, ["Спрашивать", "Открывать как лог", "Открывать в редакторе"],
                initial.OpenLargeFilesAsLog switch { true => 1, false => 2, null => 0 });
            nint flPerFileLabel = dialog.Label("Лимит снимка файла, МиБ (0–64)", 214, 86, 220, 22);
            nint flPerFileEdit = dialog.Edit((initial.VersionFileBytes / (1024L * 1024L)).ToString(CultureInfo.CurrentCulture), 446, 83, 248, 26);
            nint flTotalLabel = dialog.Label("Общий лимит снимков, МиБ (0–1024)", 214, 124, 220, 22);
            nint flTotalEdit = dialog.Edit((initial.VersionTotalBytes / (1024L * 1024L)).ToString(CultureInfo.CurrentCulture), 446, 121, 248, 26);
            nint[] fileControls = [flHeading, flLargeLabel, flLargeCombo, flPerFileLabel, flPerFileEdit, flTotalLabel, flTotalEdit];

            nint wsHeading = dialog.Label("Рабочие пространства", 214, 14, 480, 24);
            nint wsStartupLabel = dialog.Label("Открывать при запуске", 214, 48, 220, 22);
            nint wsStartupCombo = 0;
            nint wsTargetCombo = 0;
            wsStartupCombo = dialog.ComboBox(446, 45, 248, 180, () =>
                ToolDialog.SetEnabled(wsTargetCombo, ToolDialog.ComboSelection(wsStartupCombo) == 1));
            ToolDialog.SetComboItems(wsStartupCombo, ["Последнее пространство", "Выбранное пространство", "Пустое окно"],
                initial.WorkspaceStartupMode switch
                {
                    WorkspaceStartupMode.Specific => 1,
                    WorkspaceStartupMode.Empty => 2,
                    _ => 0,
                });
            nint wsTargetLabel = dialog.Label("Стартовое пространство", 214, 86, 220, 22);
            wsTargetCombo = dialog.ComboBox(446, 83, 248, 220);
            int selectedWsIndex = workspaces.FindIndex(item => item.Id == initial.StartupWorkspaceId);
            if (selectedWsIndex < 0) selectedWsIndex = 0;
            ToolDialog.SetComboItems(wsTargetCombo, workspaces.Select(item => item.Name), selectedWsIndex);
            ToolDialog.SetEnabled(wsTargetCombo, ToolDialog.ComboSelection(wsStartupCombo) == 1);
            nint wsHistoryLabel = dialog.Label("Хранить удалённые пространства (0–200 или «Безлимит»)", 214, 122, 220, 38);
            nint wsHistoryEdit = dialog.Edit(
                initial.WorkspaceHistoryLimit < 0
                    ? "Безлимит"
                    : initial.WorkspaceHistoryLimit.ToString(CultureInfo.CurrentCulture),
                446, 124, 248, 26);
            nint[] workspaceControls = [wsHeading, wsStartupLabel, wsStartupCombo, wsTargetLabel, wsTargetCombo, wsHistoryLabel, wsHistoryEdit];

            nint drHeading = dialog.Label("Черновики и архив", 214, 14, 480, 24);
            nint drShowLines = dialog.CheckBox("Показывать число строк у вкладок без имени", initial.ShowDraftLineCount, 214, 48, 480, 24);
            nint drLimitLabel = dialog.Label("Символов в заголовке пустой вкладки (1–200)", 214, 84, 260, 22);
            nint drLimitEdit = dialog.Edit(initial.UntitledLimit.ToString(CultureInfo.CurrentCulture), 484, 81, 210, 26);

            (string Label, int Value, int Count, int Unit)[] archiveAfterPresets =
            [
                ("1 час", 60, 1, 1),
                ("5 часов", 300, 5, 1),
                ("1 день", 1440, 1, 2),
                ("7 дней", 10_080, 7, 2),
                ("Никогда", 0, 0, 0),
            ];
            int[] archiveAfterMultipliers = [1, 60, 1440];
            int afterSelected = Array.FindIndex(archiveAfterPresets, item => item.Value == initial.ArchiveClosedDraftAfterMinutes);

            nint drAfterLabel = dialog.Label("Архивировать закрытые черновики через", 214, 122, 260, 22);
            nint drAfterPreset = 0;
            nint drAfterCount = 0;
            nint drAfterUnit = 0;
            void SyncAfterVisibility(bool pageActive)
            {
                bool manual = pageActive && ToolDialog.ComboSelection(drAfterPreset) == archiveAfterPresets.Length;
                ToolDialog.SetVisible(drAfterCount, manual);
                ToolDialog.SetVisible(drAfterUnit, manual);
            }
            drAfterPreset = dialog.ComboBox(484, 119, 210, 200, () => SyncAfterVisibility(ToolDialog.Selection(sectionList) == 3));
            ToolDialog.SetComboItems(drAfterPreset,
                archiveAfterPresets.Select(item => item.Label).Append("Вручную"),
                afterSelected >= 0 ? afterSelected : archiveAfterPresets.Length);
            drAfterCount = dialog.Edit(
                (afterSelected >= 0 ? archiveAfterPresets[afterSelected].Count : initial.ArchiveClosedDraftAfterMinutes).ToString(CultureInfo.CurrentCulture),
                484, 152, 86, 26);
            drAfterUnit = dialog.ComboBox(578, 152, 116, 160);
            ToolDialog.SetComboItems(drAfterUnit, ["минут", "часов", "дней"], afterSelected >= 0 ? archiveAfterPresets[afterSelected].Unit : 0);

            (string Label, int Value, int Count, int Unit)[] retentionPresets =
            [
                ("1 день", 1, 1, 0),
                ("7 дней", 7, 7, 0),
                ("30 дней", 30, 30, 0),
                ("1 год", 365, 1, 3),
                ("Безлимит", 0, 0, 0),
            ];
            int[] retentionMultipliers = [1, 7, 30, 365];
            int retentionSelected = Array.FindIndex(retentionPresets, item => item.Value == initial.ArchivedDraftRetentionDays);

            nint drRetLabel = dialog.Label("Хранить архивные черновики", 214, 192, 260, 22);
            nint drRetPreset = 0;
            nint drRetCount = 0;
            nint drRetUnit = 0;
            void SyncRetentionVisibility(bool pageActive)
            {
                bool manual = pageActive && ToolDialog.ComboSelection(drRetPreset) == retentionPresets.Length;
                ToolDialog.SetVisible(drRetCount, manual);
                ToolDialog.SetVisible(drRetUnit, manual);
            }
            drRetPreset = dialog.ComboBox(484, 189, 210, 200, () => SyncRetentionVisibility(ToolDialog.Selection(sectionList) == 3));
            ToolDialog.SetComboItems(drRetPreset,
                retentionPresets.Select(item => item.Label).Append("Вручную"),
                retentionSelected >= 0 ? retentionSelected : retentionPresets.Length);
            drRetCount = dialog.Edit(
                (retentionSelected >= 0 ? retentionPresets[retentionSelected].Count : initial.ArchivedDraftRetentionDays).ToString(CultureInfo.CurrentCulture),
                484, 222, 86, 26);
            drRetUnit = dialog.ComboBox(578, 222, 116, 180);
            ToolDialog.SetComboItems(drRetUnit, ["дней", "недель", "месяцев", "лет"], retentionSelected >= 0 ? retentionPresets[retentionSelected].Unit : 0);

            nint drNote = dialog.Label("Открытые вкладки не архивируются. Для старых записей срок начинается после первого сохранения новой версии.", 214, 262, 480, 44);
            nint[] draftControls = [drHeading, drShowLines, drLimitLabel, drLimitEdit, drAfterLabel, drAfterPreset, drAfterCount, drAfterUnit, drRetLabel, drRetPreset, drRetCount, drRetUnit, drNote];

            nint upHeading = dialog.Label("Обновления", 214, 14, 480, 24);
            nint upAutoCheck = dialog.CheckBox("Проверять обновления при запуске и каждый час", initial.AutoCheckUpdates, 214, 48, 480, 24);
            nint[] updateControls = [upHeading, upAutoCheck];

            nint[][] pages = [editorControls, fileControls, workspaceControls, draftControls, updateControls];

            void ShowSection(int index)
            {
                if (index < 0 || index >= pages.Length) index = 0;
                for (int i = 0; i < pages.Length; i++)
                {
                    ToolDialog.ShowGroup(i == index, pages[i]);
                }
                SyncAfterVisibility(index == 3);
                SyncRetentionVisibility(index == 3);
                if (index == 2)
                {
                    ToolDialog.SetEnabled(wsTargetCombo, ToolDialog.ComboSelection(wsStartupCombo) == 1);
                }
            }

            sectionList = dialog.ListBox(16, 14, 180, 356, onSelect: () => ShowSection(ToolDialog.Selection(sectionList)));
            ToolDialog.SetItems(sectionList, ["Редактор", "Файлы и версии", "Рабочие пространства", "Черновики и архив", "Обновления"]);
            ToolDialog.SetSelection(sectionList, 0);
            ShowSection(0);

            nint validationLabel = dialog.Label("", 16, 392, 438, 32);
            dialog.Button("Отмена", 468, 392, 108, dialog.Close);
            dialog.Button("Сохранить", 586, 392, 108, () =>
            {
                void ShowInvalid(int sectionIndex, nint control, string message)
                {
                    ToolDialog.SetSelection(sectionList, sectionIndex);
                    ShowSection(sectionIndex);
                    ToolDialog.SetText(validationLabel, message);
                    Win32.SetFocus(control);
                }

                if (!float.TryParse(ToolDialog.Text(edFontEdit).Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out float parsedFontSize) ||
                    !float.IsFinite(parsedFontSize) || parsedFontSize < MinFontSize || parsedFontSize > MaxFontSize)
                {
                    ShowInvalid(0, edFontEdit, "Введите размер текста от 8 до 48 px.");
                    return;
                }

                if (!long.TryParse(ToolDialog.Text(flPerFileEdit).Trim(), NumberStyles.None, CultureInfo.CurrentCulture, out long fileLimitMib) ||
                    fileLimitMib < 0 || fileLimitMib > 64)
                {
                    ShowInvalid(1, flPerFileEdit, "Введите целый лимит снимка от 0 до 64 МиБ.");
                    return;
                }

                if (!long.TryParse(ToolDialog.Text(flTotalEdit).Trim(), NumberStyles.None, CultureInfo.CurrentCulture, out long totalLimitMib) ||
                    totalLimitMib < 0 || totalLimitMib > 1024)
                {
                    ShowInvalid(1, flTotalEdit, "Введите целый общий лимит от 0 до 1024 МиБ.");
                    return;
                }

                string historyText = ToolDialog.Text(wsHistoryEdit).Trim();
                int historyLimit = -1;
                if (!string.Equals(historyText, "Безлимит", StringComparison.OrdinalIgnoreCase) &&
                    (!int.TryParse(historyText, NumberStyles.None, CultureInfo.CurrentCulture, out historyLimit) || historyLimit < 0 || historyLimit > 200))
                {
                    ShowInvalid(2, wsHistoryEdit, "Введите число от 0 до 200 или «Безлимит».");
                    return;
                }

                if (!int.TryParse(ToolDialog.Text(drLimitEdit).Trim(), NumberStyles.None, CultureInfo.CurrentCulture, out int untitledLimit) ||
                    untitledLimit < 1 || untitledLimit > 200)
                {
                    ShowInvalid(3, drLimitEdit, "Введите число символов от 1 до 200.");
                    return;
                }

                if (!TryReadDuration(drAfterPreset, drAfterCount, drAfterUnit, archiveAfterPresets, archiveAfterMultipliers, 525_600, out int archiveMinutes))
                {
                    ShowInvalid(3, drAfterCount, "Укажите срок до архивации от 0 до 365 дней.");
                    return;
                }

                if (!TryReadDuration(drRetPreset, drRetCount, drRetUnit, retentionPresets, retentionMultipliers, 3650, out int retentionDays))
                {
                    ShowInvalid(3, drRetCount, "Укажите срок хранения от 0 до 3650 дней.");
                    return;
                }

                string theme = ToolDialog.ComboSelection(edThemeCombo) switch
                {
                    1 => "neon",
                    2 => "vicecity",
                    _ => "focus",
                };
                bool wordWrap = ToolDialog.IsChecked(edWordWrap);
                bool minimap = ToolDialog.IsChecked(edMinimap);
                bool? largeFiles = ToolDialog.ComboSelection(flLargeCombo) switch
                {
                    1 => true,
                    2 => false,
                    _ => null,
                };
                var startupMode = ToolDialog.ComboSelection(wsStartupCombo) switch
                {
                    1 => WorkspaceStartupMode.Specific,
                    2 => WorkspaceStartupMode.Empty,
                    _ => WorkspaceStartupMode.LastUsed,
                };
                int wsIdx = ToolDialog.ComboSelection(wsTargetCombo);
                string? startupWorkspaceId = (uint)wsIdx < (uint)workspaces.Count
                    ? workspaces[wsIdx].Id
                    : initial.StartupWorkspaceId;

                var previous = _settings;
                var next = (_settings with
                {
                    UntitledLimit = untitledLimit,
                    Minimap = minimap,
                    VersionFileBytes = fileLimitMib * 1024L * 1024L,
                    VersionTotalBytes = totalLimitMib * 1024L * 1024L,
                    Theme = theme,
                    FontSize = parsedFontSize,
                    WordWrap = wordWrap,
                    OpenLargeFilesAsLog = largeFiles,
                    WorkspaceStartupMode = startupMode,
                    StartupWorkspaceId = startupWorkspaceId,
                    WorkspaceHistoryLimit = historyLimit,
                    ShowDraftLineCount = ToolDialog.IsChecked(drShowLines),
                    ArchiveClosedDraftAfterMinutes = archiveMinutes,
                    ArchivedDraftRetentionDays = retentionDays,
                    AutoCheckUpdates = ToolDialog.IsChecked(upAutoCheck),
                    AutoCheckUpdatesChoiceMade = true,
                }).Normalize();

                next.Save();
                _settings = next;

                if (!string.Equals(ThemePalette.CurrentName, next.Theme, StringComparison.OrdinalIgnoreCase))
                {
                    ApplyActiveTheme(next.Theme);
                }

                if (_wrap != next.WordWrap)
                {
                    _wrap = next.WordWrap;
                    foreach (var doc in _documents)
                    {
                        if (doc.Viewport is { } vp) vp.SetWrap(_wrap);
                        else ScintillaInterop.SetWrap(doc.EditorHwnd, _wrap);
                        SyncDocumentScroll(doc);
                    }
                }

                if (Math.Abs(_fontSize - next.FontSize) >= 0.01f)
                {
                    _fontSize = next.FontSize;
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

                foreach (var doc in _documents) UpdateUntitledName(doc);
                if (_showHome) UpdateHistory();
                LayoutAndSyncControls();
                InvalidateScrollbars();
                InvalidateChrome();

                if (!previous.AutoCheckUpdates && next.AutoCheckUpdates)
                {
                    StartUpdateCheck();
                }
                if (previous.ArchiveClosedDraftAfterMinutes != next.ArchiveClosedDraftAfterMinutes ||
                    previous.ArchivedDraftRetentionDays != next.ArchivedDraftRetentionDays)
                {
                    RunDraftArchivePass();
                }

                dialog.Close();
            }, primary: true);
        });
        FocusActiveEditor();

        static bool TryReadDuration(
            nint presetCombo,
            nint countEdit,
            nint unitCombo,
            (string Label, int Value, int Count, int Unit)[] presets,
            int[] multipliers,
            int maximum,
            out int result)
        {
            int preset = ToolDialog.ComboSelection(presetCombo);
            if ((uint)preset < (uint)presets.Length)
            {
                result = presets[preset].Value;
                return true;
            }

            result = 0;
            int unit = ToolDialog.ComboSelection(unitCombo);
            if (!int.TryParse(ToolDialog.Text(countEdit).Trim(), NumberStyles.None, CultureInfo.CurrentCulture, out int count) ||
                (uint)unit >= (uint)multipliers.Length ||
                count > maximum / multipliers[unit])
            {
                return false;
            }

            result = count * multipliers[unit];
            return true;
        }
    }
}
