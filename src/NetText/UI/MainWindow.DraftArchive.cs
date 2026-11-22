using System.Text;
using NetText.Core;
using NetText.Native;

namespace NetText.UI;

internal sealed unsafe partial class MainWindow
{
    private bool _draftArchivePassInFlight;

    private static DraftArchiveService OpenDraftArchive() =>
        new(WorkspaceCatalogService.GetProfileRoot());

    private void RunDraftArchivePass()
    {
        if (_windowClosed || _closingForSession || _draftArchivePassInFlight) return;
        var settings = _settings.Normalize();
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        for (int i = 0; i < _closed.Count; i++)
        {
            var (tab, draftBytes) = _closed[i];
            if (tab.File is null && tab.ClosedAtMs is null)
            {
                _closed[i] = (tab with { ClosedAtMs = nowMs }, draftBytes);
            }
        }

        var candidates = new List<(SessionTab Tab, byte[] DraftBytes, string DisplayName)>();
        if (settings.ArchiveClosedDraftAfterMinutes > 0)
        {
            long cutoff = DateTimeOffset.UtcNow.AddMinutes(-settings.ArchiveClosedDraftAfterMinutes).ToUnixTimeMilliseconds();
            foreach (var (tab, draftBytes) in _closed)
            {
                if (tab.File is null &&
                    draftBytes is { Length: > 0 } &&
                    !SessionJournal.IsKnownEmptyUntitledDraft(tab, draftBytes) &&
                    tab.ClosedAtMs <= cutoff)
                {
                    string displayName = !string.IsNullOrWhiteSpace(tab.CustomTitle)
                        ? tab.CustomTitle
                        : ClosedTabLabel(tab, draftBytes);
                    candidates.Add((tab, draftBytes, displayName));
                }
            }
        }

        if (settings.ArchivedDraftRetentionDays <= 0 && candidates.Count == 0) return;

        _draftArchivePassInFlight = true;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            var archived = new List<(ulong TabId, long? ClosedAtMs, string DisplayName)>();
            string? firstError = null;
            try
            {
                var service = OpenDraftArchive();
                if (settings.ArchivedDraftRetentionDays > 0)
                {
                    service.PruneExpiredAsync(settings.ArchivedDraftRetentionDays).GetAwaiter().GetResult();
                }

                foreach (var (tab, draftBytes, displayName) in candidates)
                {
                    try
                    {
                        var entry = service.ArchiveAsync(tab, draftBytes, displayName).GetAwaiter().GetResult();
                        archived.Add((tab.Id, tab.ClosedAtMs, entry.DisplayName));
                    }
                    catch (Exception ex)
                    {
                        firstError ??= ex.Message;
                    }
                }
            }
            catch (Exception ex)
            {
                firstError ??= ex.Message;
            }

            DispatchUi(() =>
            {
                _draftArchivePassInFlight = false;
                if (_windowClosed) return;

                if (archived.Count > 0)
                {
                    foreach (var item in archived)
                    {
                        _closed.RemoveAll(entry => entry.Tab.Id == item.TabId && entry.Tab.ClosedAtMs == item.ClosedAtMs);
                    }
                    if (_showHome) UpdateHistory();
                    QueueSessionTick();
                    InvalidateChrome();
                }
                else if (firstError is not null)
                {
                    _error ??= $"Не удалось архивировать черновик. {firstError}";
                    InvalidateChrome();
                }
            });
        });
    }

    private void ShowDraftArchiveDialog()
    {
        var service = OpenDraftArchive();
        ToolDialog.Show(_hwnd, "Архив черновиков", 660, 470, dialog =>
        {
            IReadOnlyList<DraftArchiveEntry> entries = [];
            dialog.Label("Архивные закрытые черновики", 16, 12, 628, 22);
            nint list = dialog.ListBox(16, 40, 628, 304);
            nint status = dialog.Label("", 16, 354, 628, 24);
            nint restoreBtn = 0;
            nint deleteBtn = 0;

            void Refresh()
            {
                try
                {
                    entries = service.ListAsync().GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    entries = [];
                    ToolDialog.SetItems(list, []);
                    ToolDialog.SetText(status, ex.Message);
                    ToolDialog.SetEnabled(restoreBtn, false);
                    ToolDialog.SetEnabled(deleteBtn, false);
                    return;
                }

                if (entries.Count == 0)
                {
                    ToolDialog.SetItems(list, []);
                    ToolDialog.SetText(status, "Архив пуст.");
                    ToolDialog.SetEnabled(restoreBtn, false);
                    ToolDialog.SetEnabled(deleteBtn, false);
                    return;
                }

                ToolDialog.SetItems(list, entries.Select(entry =>
                {
                    string name = !string.IsNullOrWhiteSpace(entry.Tab.CustomTitle) ? entry.Tab.CustomTitle : entry.DisplayName;
                    string when = DateTimeOffset.FromUnixTimeMilliseconds(entry.ClosedAtMs).ToLocalTime().ToString("g");
                    return $"{name} · {FormatHistorySize(entry.Bytes)} · закрыт {when}";
                }));
                ToolDialog.SetSelection(list, 0);
                ToolDialog.SetText(status, $"Записей в архиве: {entries.Count}");
                ToolDialog.SetEnabled(restoreBtn, true);
                ToolDialog.SetEnabled(deleteBtn, true);
            }

            restoreBtn = dialog.Button("Восстановить", 16, 390, 146, () =>
            {
                int idx = ToolDialog.Selection(list);
                if ((uint)idx >= (uint)entries.Count) return;
                var entry = entries[idx];

                var (tab, draftBytes) = service.RestoreAsync(entry).GetAwaiter().GetResult();
                string draftText = Encoding.UTF8.GetString(draftBytes);
                ulong newId = RestoreTextTab(tab with { Id = 0 }, draftText);
                var restoredDoc = _documents.Find(d => d.Id == newId);
                if (restoredDoc is not null)
                {
                    restoredDoc.CustomTitle = tab.CustomTitle;
                    if (!string.IsNullOrWhiteSpace(tab.CustomTitle))
                    {
                        restoredDoc.Name = tab.CustomTitle;
                    }
                }
                SelectTab(newId);

                if (_sessionJournal is { } journal)
                {
                    var capture = CaptureSession();
                    var queued = _sessionWriter.Queue(capture);
                    if (queued is { Revision: var rev, Capture: var cap })
                    {
                        lock (journal)
                        {
                            journal.Commit(rev, cap);
                        }
                        _sessionWriter.Complete(rev, cap, true);
                    }
                }

                service.DeleteAsync(entry).GetAwaiter().GetResult();
                if (_showHome) UpdateHistory();
                InvalidateChrome();
                Refresh();
            }, primary: true);

            deleteBtn = dialog.Button("Удалить", 174, 390, 130, () =>
            {
                int idx = ToolDialog.Selection(list);
                if ((uint)idx >= (uint)entries.Count) return;
                var entry = entries[idx];
                string name = !string.IsNullOrWhiteSpace(entry.Tab.CustomTitle) ? entry.Tab.CustomTitle : entry.DisplayName;
                int answer = Win32.MessageBoxW(
                    dialog.Handle,
                    $"Удалить архивный черновик «{name}»?",
                    "Удалить черновик?",
                    Win32.MB_YESNO | Win32.MB_ICONWARNING);
                if (answer != Win32.IDYES) return;

                service.DeleteAsync(entry).GetAwaiter().GetResult();
                Refresh();
            });

            dialog.Button("Закрыть", 524, 390, 120, dialog.Close);
            Refresh();
        });
        FocusActiveEditor();
    }
}
