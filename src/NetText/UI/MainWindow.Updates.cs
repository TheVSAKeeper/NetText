using System.Diagnostics;
using System.Reflection;
using System.Text;
using NetText.Core;
using NetText.Native;

namespace NetText.UI;

internal sealed unsafe partial class MainWindow
{
    private static readonly TimeSpan HourlyUpdateInterval = TimeSpan.FromHours(1);

    private ReadyUpdate? _readyUpdate;
    private UpdateRelease? _availableRelease;
    private Version? _dismissedUpdateVersion;
    private CancellationTokenSource? _updateCheck;
    private DateTimeOffset _lastUpdateCheckUtc = DateTimeOffset.MinValue;
    private bool _updateCheckInFlight;
    private bool _restartForUpdate;
    private bool _updateHandedOff;
    private bool _hideUpdate;
    private bool _windowClosed;

    private bool HasAvailableUpdateBanner =>
        (_availableRelease is { } rel && _dismissedUpdateVersion != rel.Version) ||
        (_readyUpdate is not null && !_hideUpdate);

    private string? AvailableUpdateLabel =>
        _readyUpdate is { } ready && !_hideUpdate
            ? $"Установить {ready.Version.ToString(3)}"
            : _availableRelease is { } rel && _dismissedUpdateVersion != rel.Version
                ? $"Обновление {rel.Version.ToString(3)}"
                : null;

    private static string CurrentUpdateVersionString
    {
        get
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            return version is null ? "0.0.0" : version.ToString(3);
        }
    }

    public void ReportUpdateFailure(string details)
    {
        _error = $"Не удалось установить обновление. {details}";
        _hideUpdate = true;
        InvalidateChrome();
    }

    private void CheckForUpdates()
    {
        if (!_settings.AutoCheckUpdates) return;
        StartUpdateCheck(manual: false);
    }

    private void CheckUpdatesIfHourlyDue()
    {
        if (!_settings.AutoCheckUpdates || _windowClosed || _updateCheckInFlight) return;
        if (Measurements.IsEnabled || Environment.GetEnvironmentVariable("NETTEXT_DISABLE_UPDATES") == "1") return;
        if (DateTimeOffset.UtcNow - _lastUpdateCheckUtc < HourlyUpdateInterval) return;
        StartUpdateCheck(manual: false);
    }

    private void StartUpdateCheck(bool manual = false)
    {
        if (_windowClosed || _updateCheckInFlight) return;
        if (!manual && (!_settings.AutoCheckUpdates || Measurements.IsEnabled || Environment.GetEnvironmentVariable("NETTEXT_DISABLE_UPDATES") == "1"))
        {
            return;
        }

        _lastUpdateCheckUtc = DateTimeOffset.UtcNow;
        _updateCheck?.Cancel();
        _updateCheck?.Dispose();
        _updateCheck = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var token = _updateCheck.Token;
        _updateCheckInFlight = true;
        if (manual)
        {
            _dismissedUpdateVersion = null;
            _hideUpdate = false;
            _error = null;
            _busy = "Проверка обновлений…";
            InvalidateChrome();
        }

        _ = Task.Run(() => FetchLatestRelease(token, manual));
    }

    private void FetchLatestRelease(CancellationToken token, bool manual)
    {
        UpdateRelease? release = null;
        Exception? failure = null;
        try
        {
            Updates.CleanupCompleted();
            release = Updates.CheckLatestAsync(token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            DispatchUi(() =>
            {
                _updateCheckInFlight = false;
                if (manual && _busy == "Проверка обновлений…")
                {
                    _busy = null;
                    InvalidateChrome();
                }
            });
            return;
        }
        catch (Exception error)
        {
            failure = error;
        }

        DispatchUi(() =>
        {
            _updateCheckInFlight = false;
            if (manual && _busy == "Проверка обновлений…")
            {
                _busy = null;
            }
            if (_windowClosed) return;

            if (failure is not null)
            {
                if (manual)
                {
                    _error = $"Не удалось проверить обновления. {failure.Message}";
                }
                InvalidateChrome();
                return;
            }

            _availableRelease = release;
            if (release is null)
            {
                _dismissedUpdateVersion = null;
                InvalidateChrome();
                if (manual)
                {
                    Win32.MessageBoxW(_hwnd, $"Установлена актуальная версия {CurrentUpdateVersionString}.", "Обновления NetText", 0x00000040);
                }
                return;
            }

            if (manual)
            {
                _dismissedUpdateVersion = null;
                InvalidateChrome();
                ShowUpdateDialog();
                return;
            }

            InvalidateChrome();
        });
    }

    private void PaintUpdateNotice(nint hdc, int width, int y, int height)
    {
        if (!HasAvailableUpdateBanner) return;

        if (_readyUpdate is { } update && !_hideUpdate)
        {
            int right = width - S(12);
            int later = MeasureButtonWidth(hdc, "Позже");
            DrawButtonFixed(hdc, right - later, y, later, height, "Позже", HitKind.DismissUpdate, 0, false, false);
            string label = $"Установить {update.Version.ToString(3)}";
            int restart = MeasureButtonWidth(hdc, label);
            DrawButtonFixed(hdc, right - later - S(5) - restart, y, restart, height, label, HitKind.ApplyUpdate, 0, _busy is not null, true);
            return;
        }

        if (_availableRelease is { } available && _dismissedUpdateVersion != available.Version)
        {
            int right = width - S(12);
            int dismissW = S(26);
            DrawButtonFixed(hdc, right - dismissW, y, dismissW, height, "×", HitKind.DismissUpdate, 0, false, false);
            string label = AvailableUpdateLabel ?? $"Обновление {available.Version.ToString(3)}";
            int btnW = MeasureButtonWidth(hdc, label);
            DrawButtonFixed(hdc, right - dismissW - S(4) - btnW, y, btnW, height, label, HitKind.OpenUpdateDialog, 0, _busy is not null, true);
        }
    }

    private void DismissUpdate()
    {
        if (_availableRelease is { } rel)
        {
            _dismissedUpdateVersion = rel.Version;
        }
        _hideUpdate = true;
        InvalidateChrome();
    }

    private void ApplyReadyUpdate() => RestartForUpdate();

    private void CleanupPendingUpdate() => DisposeUpdate();

    private void ShowUpdateDialog()
    {
        var release = _availableRelease;
        if (release is null)
        {
            ShowReleaseHistoryDialog();
            return;
        }

        bool stageRequested = false;
        ReadyUpdate? stagedUpdate = null;

        ToolDialog.Show(_hwnd, "Обновление NetText", 680, 580, dialog =>
        {
            using var historyCts = new CancellationTokenSource();
            CancellationTokenSource? stageCts = null;
            bool showingHistory = false;
            bool historyLoaded = false;
            bool historyLoading = false;
            IReadOnlyList<ReleaseNotesEntry> historyEntries = [];

            dialog.Label($"Доступна новая версия · NetText Windows x64", 16, 12, 648, 22);
            dialog.Label($"Установлена: {CurrentUpdateVersionString}   →   Доступна: {release.Version.ToString(3)} ({release.Tag})", 16, 36, 648, 24);

            nint notesTabBtn = 0;
            nint historyTabBtn = 0;
            nint notesEdit = 0;
            nint historyList = 0;
            nint historyDetailsEdit = 0;
            nint statusLabel = 0;
            nint githubBtn = 0;
            nint laterBtn = 0;
            nint installBtn = 0;

            void SwitchTab(bool toHistory)
            {
                showingHistory = toHistory;
                ToolDialog.SetVisible(notesEdit, !toHistory);
                ToolDialog.SetVisible(historyList, toHistory);
                ToolDialog.SetVisible(historyDetailsEdit, toHistory);
                if (!toHistory)
                {
                    ToolDialog.SetEnabled(githubBtn, Updates.IsAllowedReleaseUrl(release.HtmlUrl));
                }
                else
                {
                    int idx = ToolDialog.Selection(historyList);
                    bool canOpen = (uint)idx < (uint)historyEntries.Count && Updates.IsAllowedReleaseUrl(historyEntries[idx].HtmlUrl);
                    ToolDialog.SetEnabled(githubBtn, canOpen);
                }
            }

            void LoadHistory()
            {
                if (historyLoaded || historyLoading || dialog.Handle == 0) return;
                historyLoading = true;
                ToolDialog.SetText(statusLabel, "Загружаются опубликованные версии…");
                var token = historyCts.Token;
                _ = Task.Run(() =>
                {
                    try
                    {
                        var entries = Updates.GetReleaseHistoryAsync(token).GetAwaiter().GetResult();
                        DispatchUi(() =>
                        {
                            historyLoading = false;
                            if (dialog.Handle == 0 || token.IsCancellationRequested) return;
                            historyEntries = entries;
                            historyLoaded = true;
                            if (entries.Count == 0)
                            {
                                ToolDialog.SetItems(historyList, []);
                                ToolDialog.SetText(historyDetailsEdit, "Опубликованных выпусков пока нет.");
                                ToolDialog.SetText(statusLabel, "");
                                return;
                            }

                            ToolDialog.SetItems(historyList, entries.Select(item => $"{item.Version.ToString(3)} ({item.Tag}) · {ReleaseSummaryText(item.Body)}"));
                            ToolDialog.SetSelection(historyList, 0);
                            ToolDialog.SetText(historyDetailsEdit, FormatMarkdownForEdit(entries[0].Body));
                            ToolDialog.SetText(statusLabel, $"Выпусков: {entries.Count}");
                            if (showingHistory)
                            {
                                ToolDialog.SetEnabled(githubBtn, Updates.IsAllowedReleaseUrl(entries[0].HtmlUrl));
                            }
                        });
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                    }
                    catch (Exception error)
                    {
                        DispatchUi(() =>
                        {
                            historyLoading = false;
                            if (dialog.Handle == 0 || token.IsCancellationRequested) return;
                            ToolDialog.SetText(statusLabel, $"Не удалось загрузить историю выпусков. {error.Message}");
                        });
                    }
                });
            }

            notesTabBtn = dialog.Button("Что нового", 16, 68, 128, () => SwitchTab(false));
            historyTabBtn = dialog.Button("История версий", 152, 68, 148, () =>
            {
                SwitchTab(true);
                LoadHistory();
            });

            string formattedNotes = string.IsNullOrWhiteSpace(release.Body)
                ? "Описание изменений пока не опубликовано."
                : FormatMarkdownForEdit(release.Body);
            notesEdit = dialog.Edit(formattedNotes, 16, 108, 648, 356, multiline: true, readOnly: true);

            historyList = dialog.ListBox(16, 108, 648, 134, onSelect: () =>
            {
                int idx = ToolDialog.Selection(historyList);
                if ((uint)idx >= (uint)historyEntries.Count) return;
                var entry = historyEntries[idx];
                ToolDialog.SetText(historyDetailsEdit, string.IsNullOrWhiteSpace(entry.Body)
                    ? "Описание изменений пока не опубликовано."
                    : FormatMarkdownForEdit(entry.Body));
                if (showingHistory)
                {
                    ToolDialog.SetEnabled(githubBtn, Updates.IsAllowedReleaseUrl(entry.HtmlUrl));
                }
            });
            historyDetailsEdit = dialog.Edit("", 16, 250, 648, 214, multiline: true, readOnly: true);
            ToolDialog.ShowGroup(false, historyList, historyDetailsEdit);

            statusLabel = dialog.Label("Сессия открытых окон сохранится перед перезапуском.", 16, 474, 648, 22);

            githubBtn = dialog.Button("Открыть выпуск на GitHub", 16, 504, 210, () =>
            {
                string url = release.HtmlUrl;
                if (showingHistory)
                {
                    int idx = ToolDialog.Selection(historyList);
                    if ((uint)idx >= (uint)historyEntries.Count) return;
                    url = historyEntries[idx].HtmlUrl;
                }
                OpenValidatedReleaseUrl(url);
            });
            ToolDialog.SetEnabled(githubBtn, Updates.IsAllowedReleaseUrl(release.HtmlUrl));

            laterBtn = dialog.Button("Позже", 342, 504, 106, () =>
            {
                stageCts?.Cancel();
                historyCts.Cancel();
                DismissUpdate();
                dialog.Close();
            });

            installBtn = dialog.Button("Скачать и перезапустить", 458, 504, 206, () =>
            {
                if (stageCts is not null) return;
                stageCts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                var token = stageCts.Token;
                ToolDialog.SetEnabled(installBtn, false);
                ToolDialog.SetEnabled(laterBtn, false);
                ToolDialog.SetText(statusLabel, "Скачивание и проверка архива обновления…");

                _ = Task.Run(() =>
                {
                    try
                    {
                        var staged = Updates.StageAsync(release, token).GetAwaiter().GetResult();
                        DispatchUi(() =>
                        {
                            if (dialog.Handle == 0 || _windowClosed)
                            {
                                Updates.CleanupStage(staged.Stage);
                                return;
                            }
                            stagedUpdate = staged;
                            stageRequested = true;
                            historyCts.Cancel();
                            dialog.Close();
                        });
                    }
                    catch (OperationCanceledException)
                    {
                        DispatchUi(() =>
                        {
                            stageCts?.Dispose();
                            stageCts = null;
                            if (dialog.Handle != 0)
                            {
                                ToolDialog.SetEnabled(installBtn, true);
                                ToolDialog.SetEnabled(laterBtn, true);
                                ToolDialog.SetText(statusLabel, "Загрузка отменена.");
                            }
                        });
                    }
                    catch (Exception error)
                    {
                        DispatchUi(() =>
                        {
                            stageCts?.Dispose();
                            stageCts = null;
                            if (dialog.Handle != 0)
                            {
                                ToolDialog.SetEnabled(installBtn, true);
                                ToolDialog.SetEnabled(laterBtn, true);
                                ToolDialog.SetText(statusLabel, $"Ошибка загрузки: {error.Message}");
                            }
                        });
                    }
                });
            }, primary: true);

            dialog.OnCancel(() =>
            {
                stageCts?.Cancel();
                historyCts.Cancel();
                dialog.Close();
            });
        });

        if (stageRequested && stagedUpdate is not null)
        {
            if (_readyUpdate is { } previous && previous.Stage != stagedUpdate.Stage)
            {
                Updates.CleanupStage(previous.Stage);
            }
            _readyUpdate = stagedUpdate;
            _hideUpdate = false;
            ApplyReadyUpdate();
        }
    }

    private void ShowReleaseHistoryDialog()
    {
        ToolDialog.Show(_hwnd, "История выпусков NetText", 680, 560, dialog =>
        {
            using var cts = new CancellationTokenSource();
            IReadOnlyList<ReleaseNotesEntry> entries = [];
            bool loading = false;

            dialog.Label($"Опубликованные версии NetText · текущая {CurrentUpdateVersionString}", 16, 12, 648, 24);
            nint list = 0;
            nint details = 0;
            nint githubBtn = 0;
            nint retryBtn = 0;
            list = dialog.ListBox(16, 42, 648, 150, onSelect: () =>
            {
                int idx = ToolDialog.Selection(list);
                if ((uint)idx >= (uint)entries.Count) return;
                UpdateSelection(entries[idx]);
            });
            details = dialog.Edit("Загружаются опубликованные версии…", 16, 202, 648, 244, multiline: true, readOnly: true);
            nint status = dialog.Label("", 16, 456, 648, 22);

            void UpdateSelection(ReleaseNotesEntry entry)
            {
                ToolDialog.SetText(details, string.IsNullOrWhiteSpace(entry.Body)
                    ? "Описание изменений пока не опубликовано."
                    : FormatMarkdownForEdit(entry.Body));
                ToolDialog.SetEnabled(githubBtn, Updates.IsAllowedReleaseUrl(entry.HtmlUrl));
            }

            void Load()
            {
                if (loading || dialog.Handle == 0) return;
                loading = true;
                ToolDialog.SetEnabled(retryBtn, false);
                ToolDialog.SetText(status, "Загружаются опубликованные версии…");
                var token = cts.Token;
                _ = Task.Run(() =>
                {
                    try
                    {
                        var fetched = Updates.GetReleaseHistoryAsync(token).GetAwaiter().GetResult();
                        DispatchUi(() =>
                        {
                            loading = false;
                            if (dialog.Handle == 0 || token.IsCancellationRequested) return;
                            ToolDialog.SetEnabled(retryBtn, true);
                            entries = fetched;
                            if (fetched.Count == 0)
                            {
                                ToolDialog.SetItems(list, []);
                                ToolDialog.SetText(details, "Опубликованных выпусков пока нет.");
                                ToolDialog.SetText(status, "");
                                ToolDialog.SetEnabled(githubBtn, false);
                                return;
                            }

                            ToolDialog.SetItems(list, fetched.Select(item => $"{item.Version.ToString(3)} ({item.Tag}) · {ReleaseSummaryText(item.Body)}"));
                            ToolDialog.SetSelection(list, 0);
                            UpdateSelection(fetched[0]);
                            ToolDialog.SetText(status, $"Загружено выпусков: {fetched.Count}");
                        });
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                    }
                    catch (Exception error)
                    {
                        DispatchUi(() =>
                        {
                            loading = false;
                            if (dialog.Handle == 0 || token.IsCancellationRequested) return;
                            ToolDialog.SetEnabled(retryBtn, true);
                            ToolDialog.SetText(details, $"Не удалось загрузить историю выпусков.\r\n\r\n{error.Message}");
                            ToolDialog.SetText(status, "Проверьте подключение к интернету и повторите попытку.");
                        });
                    }
                });
            }

            githubBtn = dialog.Button("Открыть выпуск на GitHub", 16, 486, 210, () =>
            {
                int idx = ToolDialog.Selection(list);
                if ((uint)idx < (uint)entries.Count)
                {
                    OpenValidatedReleaseUrl(entries[idx].HtmlUrl);
                }
            });
            ToolDialog.SetEnabled(githubBtn, false);
            retryBtn = dialog.Button("Повторить", 238, 486, 120, Load);
            dialog.Button("Закрыть", 544, 486, 120, () =>
            {
                cts.Cancel();
                dialog.Close();
            }, primary: true);

            dialog.OnCancel(() =>
            {
                cts.Cancel();
                dialog.Close();
            });

            Load();
        });
    }

    private static void OpenValidatedReleaseUrl(string url)
    {
        if (!Updates.IsAllowedReleaseUrl(url)) return;
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private static string ReleaseSummaryText(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return "Выпуск";
        foreach (string raw in markdown.Split('\n'))
        {
            string line = raw.Trim().TrimStart('#', '-', '*', ' ');
            if (line.Length == 0) continue;
            return line.Length > 70 ? line[..67] + "…" : line;
        }
        return "Выпуск";
    }

    private static string FormatMarkdownForEdit(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return "";
        var sb = new StringBuilder(markdown.Length + 64);
        string[] lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        foreach (string raw in lines)
        {
            string line = raw.TrimEnd();
            string trimmed = line.TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal)) continue;

            int hashes = 0;
            while (hashes < trimmed.Length && trimmed[hashes] == '#') hashes++;
            if (hashes is > 0 and <= 4 && trimmed.Length > hashes && trimmed[hashes] == ' ')
            {
                string heading = StripInlineMarkdown(trimmed[(hashes + 1)..].Trim());
                if (sb.Length > 0) sb.Append("\r\n");
                sb.Append(heading.ToUpperInvariant()).Append("\r\n");
                continue;
            }

            if (trimmed.StartsWith("- ", StringComparison.Ordinal) ||
                trimmed.StartsWith("* ", StringComparison.Ordinal) ||
                trimmed.StartsWith("+ ", StringComparison.Ordinal))
            {
                sb.Append("  • ").Append(StripInlineMarkdown(trimmed[2..].Trim())).Append("\r\n");
                continue;
            }

            sb.Append(StripInlineMarkdown(trimmed)).Append("\r\n");
        }
        return sb.ToString().TrimEnd();
    }

    private static string StripInlineMarkdown(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            if (i + 1 < text.Length && text[i] == '*' && text[i + 1] == '*')
            {
                int end = text.IndexOf("**", i + 2, StringComparison.Ordinal);
                if (end > i + 2)
                {
                    sb.Append(text, i + 2, end - (i + 2));
                    i = end + 2;
                    continue;
                }
            }
            if (text[i] == '`')
            {
                int end = text.IndexOf('`', i + 1);
                if (end > i + 1)
                {
                    sb.Append(text, i + 1, end - (i + 1));
                    i = end + 1;
                    continue;
                }
            }
            if (text[i] == '[')
            {
                int labelEnd = text.IndexOf("](", i + 1, StringComparison.Ordinal);
                int urlEnd = labelEnd < 0 ? -1 : text.IndexOf(')', labelEnd + 2);
                if (labelEnd > i + 1 && urlEnd > labelEnd + 2)
                {
                    sb.Append(text, i + 1, labelEnd - (i + 1));
                    sb.Append(" (");
                    sb.Append(text, labelEnd + 2, urlEnd - (labelEnd + 2));
                    sb.Append(')');
                    i = urlEnd + 1;
                    continue;
                }
            }
            sb.Append(text[i++]);
        }
        return sb.ToString();
    }

    private void RestartForUpdate()
    {
        if (_readyUpdate is null || _busy is not null || _documents.Any(doc => doc.Log?.IsWritingFile == true)) return;
        _restartForUpdate = true;
        if (BeginSessionShutdown()) FinishClosing();
    }

    private void FinishClosing()
    {
        if (_restartForUpdate && _readyUpdate is { } update)
        {
            try
            {
                Updates.Schedule(update);
                _updateHandedOff = true;
            }
            catch (Exception error)
            {
                _restartForUpdate = false;
                _sessionShutdownDone = false;
                _error = $"Не удалось установить обновление. {error.Message}";
                InvalidateChrome();
                return;
            }
        }
        SaveRecentPositions();
        RecordClosingWindowTabsToHistory();
        RecoverDetachedDraftsOnClose();
        _closingWindow = true;
        Win32.DestroyWindow(_hwnd);
    }

    private void DisposeUpdate()
    {
        _windowClosed = true;
        _updateCheck?.Cancel();
        _updateCheck?.Dispose();
        if (!_updateHandedOff && _readyUpdate is { } update) Updates.CleanupStage(update.Stage);
    }
}
