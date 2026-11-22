using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Unicode;
using NetText.Core;
using NetText.Native;

namespace NetText.UI;

internal sealed unsafe partial class MainWindow
{
    private const uint DT_WORDBREAK = 0x00000010;
    private static readonly TimeSpan IdleEditorAge = TimeSpan.FromMinutes(5);
    private bool _unloadingIdleEditors;

    private void RunIdleUnloadPass()
    {
        if (_windowClosed || _closingForSession || _unloadingIdleEditors || _busy is not null)
        {
            return;
        }

        _unloadingIdleEditors = true;
        try
        {
            var now = DateTimeOffset.UtcNow;
            bool unloadedAny = false;

            foreach (var doc in _documents.ToArray())
            {
                if (!CanUnloadIdleDocument(doc, now))
                {
                    continue;
                }

                if (doc.Log is null && doc.File is null && doc.EditorHwnd != 0 &&
                    ScintillaInterop.GetByteLength(doc.EditorHwnd) == 0)
                {
                    continue;
                }

                bool needsDraft = doc.Dirty || doc.File is null;
                if (needsDraft && !SaveSessionNow())
                {
                    continue;
                }

                if (_windowClosed || _closingForSession || !_documents.Contains(doc) || !CanUnloadIdleDocument(doc, now))
                {
                    continue;
                }

                var (captured, _) = SnapshotDocument(doc, includeDraft: needsDraft);
                doc.DeferredState = captured with
                {
                    AutoRefresh = doc.AutoRefresh,
                    CustomTitle = doc.CustomTitle,
                    EditPages = doc.EditPages,
                };
                doc.StoredLineCount = captured.LineCount;

                UnloadDocumentResources(doc);
                doc.DraftBytes = null;
                doc.IdleUnloaded = true;
                doc.Placeholder = true;
                unloadedAny = true;
            }

            if (unloadedAny)
            {
                InvalidateChrome();
            }
        }
        finally
        {
            _unloadingIdleEditors = false;
        }
    }

    private bool CanUnloadIdleDocument(Document doc, DateTimeOffset now)
    {
        if (doc.Id == _activeId || _groupTabs.GetValueOrDefault(doc.Group) == doc.Id)
        {
            return false;
        }

        if (doc.Placeholder || doc.IdleUnloaded || doc.CheckingSession || doc.IsConsole)
        {
            return false;
        }

        if (doc.Log is { } log &&
            (log.HasPendingEdit || log.IsSavingPage || log.PreparingTrim || log.IsWritingFile || log.Reading ||
             (log.IsEditingPage && !log.EditsPagesByDefault)))
        {
            return false;
        }

        if (IsFocusInsideDocument(doc, Win32.GetFocus()))
        {
            return false;
        }

        DateTimeOffset lastWorked = doc.LastWorked ?? now;
        return now - lastWorked >= IdleEditorAge;
    }

    private bool IsFocusInsideDocument(Document doc, nint focus)
    {
        if (focus == 0)
        {
            return false;
        }

        if (doc.EditorHwnd != 0 && (focus == doc.EditorHwnd || Win32.IsChild(doc.EditorHwnd, focus)))
        {
            return true;
        }

        if (doc.FilterPreviewHwnd != 0 && (focus == doc.FilterPreviewHwnd || Win32.IsChild(doc.FilterPreviewHwnd, focus)))
        {
            return true;
        }

        if (_pageEditors.TryGetValue(doc.Id, out nint pageHwnd) && pageHwnd != 0 &&
            (focus == pageHwnd || Win32.IsChild(pageHwnd, focus)))
        {
            return true;
        }

        return false;
    }

    private bool SaveSessionNow()
    {
        if (_sessionJournal is not { } journal || _closingForSession)
        {
            return false;
        }

        try
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
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void UnloadDocumentResources(Document doc)
    {
        if (IsFocusInsideDocument(doc, Win32.GetFocus()) && _hwnd != 0)
        {
            Win32.SetFocus(_hwnd);
        }

        DetachFileWatch(doc);
        DestroyEditorFilterPreview(doc);
        DestroyPageEditorHost(doc);

        if (doc.Log is { } log)
        {
            log.Dispose();
            doc.Log = null;
        }

        if (doc.Viewport is { } viewport)
        {
            viewport.Dispose();
            doc.Viewport = null;
            doc.EditorHwnd = 0;
        }
        else if (doc.EditorHwnd != 0)
        {
            nint hwnd = doc.EditorHwnd;
            doc.EditorHwnd = 0;
            Win32.DestroyWindow(hwnd);
        }

        _editorIndicators.Remove(doc.Id);
        _mapSamples.Remove(doc.Id);
        _scrollBounds.Remove(doc.Id);
        _lineNumberCache.Remove(doc.Id);
        _linkIndicatorCache.Remove(doc.Id);
        if (_minimapPreviews.Remove(doc.Id, out var preview))
        {
            preview.Dispose();
        }
    }

    private void RestoreDeferredOrIdleTab(Document doc)
    {
        if (!doc.IdleUnloaded && !doc.Placeholder)
        {
            return;
        }

        if (doc.CheckingSession && doc.DeferredState is null && doc.File is null)
        {
            return;
        }

        var state = doc.DeferredState;
        if (state is not null)
        {
            if (!string.IsNullOrWhiteSpace(state.CustomTitle))
            {
                doc.CustomTitle = state.CustomTitle;
                doc.Name = state.CustomTitle;
            }
            if (state.AutoRefresh is { } ar)
            {
                doc.AutoRefresh = ar;
            }
            if (state.EditPages is { } ep)
            {
                doc.EditPages = ep;
            }
        }

        FileTarget? fileTarget = doc.File ?? (state?.File is { } sf
            ? new FileTarget(sf.Path, sf.Version is { } sv ? new FileVersion(sv.Bytes, sv.ModifiedUtc, sv.CreatedUtc) : null)
            : null);

        if (state?.Log is { } savedBookmark && fileTarget is not null)
        {
            bool win1251 = string.Equals(state.Format.Encoding, nameof(TextEncoding.Windows1251), StringComparison.OrdinalIgnoreCase) ||
                           doc.Format.Encoding == TextEncoding.Windows1251;
            var bookmark = savedBookmark.Copy();
            bookmark.AutoRefresh = doc.AutoRefresh;
            bookmark.EditPages = doc.EditPages || bookmark.EditPages;
            RestoreLogOntoDocument(doc, fileTarget, win1251, bookmark, restored: true);
            doc.IdleUnloaded = false;
            doc.Placeholder = false;
            doc.CheckingSession = false;
            doc.DeferredState = null;
            doc.LastWorked = DateTimeOffset.UtcNow;
            AttachFileWatch(doc);
            LayoutAndSyncControls();
            if (_activeId == doc.Id && !_showHome)
            {
                FocusActiveEditor();
            }
            InvalidateChrome();
            return;
        }

        TextFormat restoredFormat = state?.Format.ToTextFormat() ?? doc.Format;
        bool win1251Doc = restoredFormat.Encoding == TextEncoding.Windows1251;

        if (state?.DraftRevision is not null || (doc.Dirty && doc.DraftBytes is not null))
        {
            byte[]? draftBytes = TryReadSessionDraftBytes(doc, state);
            if (draftBytes is not null)
            {
                if (doc.EditorHwnd == 0)
                {
                    doc.EditorHwnd = ScintillaInterop.CreateEditorControl(_hwnd, _hInstance, (int)MathF.Round(_fontSize), _wrap);
                    InstallEditorSubclass(doc.EditorHwnd);
                }

                ScintillaInterop.SetTextUtf8(doc.EditorHwnd, draftBytes);
                ScintillaInterop.ApplyLanguage(doc.EditorHwnd, fileTarget?.Path, (int)MathF.Round(_fontSize));
                UpdateEditorLinksAndLineNumbers(doc);

                doc.File = fileTarget;
                doc.Format = restoredFormat;
                doc.Dirty = true;
                doc.DraftRevision = state?.DraftRevision ?? doc.DraftRevision;
                doc.DraftBytes = draftBytes;
                doc.JsonDisabled = state?.JsonDisabled ?? doc.JsonDisabled;
                doc.DiskStatus = fileTarget is null ? DiskStatusKind.Current : DiskStatus.Check(fileTarget);
                doc.IdleUnloaded = false;
                doc.Placeholder = false;
                doc.CheckingSession = false;
                doc.DeferredState = null;
                doc.LastWorked = DateTimeOffset.UtcNow;
                doc.PendingEditorReady = true;

                if (state is not null)
                {
                    ApplyStoredTextPosition(doc, state);
                }

                UpdateUntitledName(doc);
                AttachFileWatch(doc);
                LayoutAndSyncControls();
                if (_activeId == doc.Id && !_showHome)
                {
                    FocusActiveEditor();
                }
                InvalidateChrome();
                return;
            }
        }

        if (fileTarget is not null)
        {
            try
            {
                var version = FileVersion.At(fileTarget.Path)
                    ?? throw new FileNotFoundException("Файл не найден", fileTarget.Path);
                if (version.Bytes > Files.MaxEditorBytes)
                {
                    var bookmark = new LogBookmark
                    {
                        AutoRefresh = doc.AutoRefresh,
                        EditPages = doc.EditPages,
                    };
                    RestoreLogOntoDocument(doc, new FileTarget(fileTarget.Path, version), win1251Doc, bookmark, restored: false);
                    doc.IdleUnloaded = false;
                    doc.Placeholder = false;
                    doc.CheckingSession = false;
                    doc.DeferredState = null;
                    doc.LastWorked = DateTimeOffset.UtcNow;
                    AttachFileWatch(doc);
                    LayoutAndSyncControls();
                    if (_activeId == doc.Id && !_showHome)
                    {
                        FocusActiveEditor();
                    }
                    InvalidateChrome();
                    return;
                }

                using var opened = Files.OpenForEditor(fileTarget.Path, win1251Doc);
                if (doc.EditorHwnd == 0)
                {
                    doc.EditorHwnd = ScintillaInterop.CreateEditorControl(_hwnd, _hInstance, (int)MathF.Round(_fontSize), _wrap);
                    InstallEditorSubclass(doc.EditorHwnd);
                }

                ScintillaInterop.SetTextFromOpened(doc.EditorHwnd, opened);
                ScintillaInterop.ApplyLanguage(doc.EditorHwnd, opened.Target.Path, (int)MathF.Round(_fontSize));
                ScintillaInterop.SetSavePoint(doc.EditorHwnd);
                UpdateEditorLinksAndLineNumbers(doc);

                doc.File = opened.Target;
                doc.Format = opened.Format;
                doc.Dirty = false;
                doc.DraftBytes = null;
                doc.DraftRevision = null;
                doc.DiskStatus = DiskStatusKind.Current;
                doc.JsonDisabled = state?.JsonDisabled ?? doc.JsonDisabled;
                doc.IdleUnloaded = false;
                doc.Placeholder = false;
                doc.CheckingSession = false;
                doc.DeferredState = null;
                doc.LastWorked = DateTimeOffset.UtcNow;
                doc.PendingEditorReady = true;

                if (state is not null)
                {
                    ApplyStoredTextPosition(doc, state);
                }

                AttachFileWatch(doc);
                LayoutAndSyncControls();
                if (_activeId == doc.Id && !_showHome)
                {
                    FocusActiveEditor();
                }
                InvalidateChrome();
                return;
            }
            catch (Exception ex)
            {
                if (doc.EditorHwnd == 0)
                {
                    doc.EditorHwnd = ScintillaInterop.CreateEditorControl(_hwnd, _hInstance, (int)MathF.Round(_fontSize), _wrap);
                }
                doc.File = fileTarget;
                doc.DiskStatus = DiskStatus.FromException(ex);
                doc.RecoveryStatus = ex.Message;
                doc.IdleUnloaded = false;
                doc.Placeholder = true;
                doc.CheckingSession = false;
                _error = $"Не удалось открыть «{doc.Name}». {ex.Message}";
                LayoutAndSyncControls();
                InvalidateChrome();
                return;
            }
        }

        if (doc.EditorHwnd == 0)
        {
            doc.EditorHwnd = ScintillaInterop.CreateEditorControl(_hwnd, _hInstance, (int)MathF.Round(_fontSize), _wrap);
            InstallEditorSubclass(doc.EditorHwnd);
        }
        doc.Format = restoredFormat;
        doc.IdleUnloaded = false;
        doc.Placeholder = false;
        doc.CheckingSession = false;
        doc.DeferredState = null;
        doc.LastWorked = DateTimeOffset.UtcNow;
        LayoutAndSyncControls();
        if (_activeId == doc.Id && !_showHome)
        {
            FocusActiveEditor();
        }
        InvalidateChrome();
    }

    private void RestoreLogOntoDocument(Document doc, FileTarget target, bool windows1251, LogBookmark bookmark, bool restored)
    {
        ulong id = doc.Id;
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
            if (!_showHome && _activeId != id)
            {
                SelectTab(id);
            }
        };
        viewport.Scrolled = () =>
        {
            var document = _documents.Find(d => d.Id == id);
            if (document is not null)
            {
                SyncDocumentScroll(document);
            }
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

        doc.File = target;
        if (string.IsNullOrWhiteSpace(doc.CustomTitle))
        {
            doc.Name = FileName(target.Path);
        }
        doc.Format = new TextFormat(windows1251 ? TextEncoding.Windows1251 : TextEncoding.Utf8, LineEndings.Lf);
        doc.EditorHwnd = editorHwnd;
        doc.Log = state;
        doc.Viewport = viewport;
        doc.Dirty = false;
        doc.DraftBytes = null;
        doc.DiskStatus = DiskStatusKind.Current;
        state.StartInitialLoad(restored);
    }

    private byte[]? TryReadSessionDraftBytes(Document doc, SessionTab? state)
    {
        if (doc.DraftBytes is { } cached)
        {
            return cached;
        }

        if (_sessionJournal is not { } journal)
        {
            return null;
        }

        ulong targetId = state?.Id ?? doc.Id;
        ulong? expectedRevision = state?.DraftRevision ?? doc.DraftRevision;

        try
        {
            lock (journal)
            {
                if (!Directory.Exists(journal.DirectoryPath))
                {
                    return null;
                }

                const string Prefix = "checkpoint-";
                var checkpoints = new List<(ulong Seq, string Path)>();
                foreach (var sub in Directory.EnumerateDirectories(journal.DirectoryPath))
                {
                    var name = Path.GetFileName(sub);
                    if (name.StartsWith(Prefix, StringComparison.Ordinal) &&
                        name.Length == Prefix.Length + 20 &&
                        ulong.TryParse(name.AsSpan(Prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out ulong seq))
                    {
                        checkpoints.Add((seq, sub));
                    }
                }
                checkpoints.Sort((a, b) => b.Seq.CompareTo(a.Seq));

                foreach (var (_, dir) in checkpoints)
                {
                    var manifestPath = Path.Combine(dir, "manifest.json");
                    if (!File.Exists(manifestPath))
                    {
                        continue;
                    }

                    try
                    {
                        var manifest = JsonSerializer.Deserialize(File.ReadAllBytes(manifestPath), SessionJsonContext.Default.SessionManifest);
                        if (manifest is null || manifest.Version != SessionJournal.FormatVersion ||
                            manifest.State.Tabs.Count != manifest.Drafts.Count)
                        {
                            continue;
                        }

                        for (int i = 0; i < manifest.State.Tabs.Count; i++)
                        {
                            var tab = manifest.State.Tabs[i];
                            if (tab.Id != targetId)
                            {
                                continue;
                            }
                            if (expectedRevision is { } rev && tab.DraftRevision != rev)
                            {
                                continue;
                            }

                            var info = manifest.Drafts[i];
                            if (info is null)
                            {
                                continue;
                            }

                            var draftPath = Path.Combine(dir, $"draft-{i}.txt");
                            if (!File.Exists(draftPath))
                            {
                                continue;
                            }

                            var bytes = File.ReadAllBytes(draftPath);
                            if (bytes.Length == info.Bytes && Crc32.Compute(bytes) == info.Checksum && Utf8.IsValid(bytes))
                            {
                                return bytes;
                            }
                        }
                    }
                    catch
                    {
                    }
                }
            }
        }
        catch
        {
        }

        return null;
    }

    private void PaintDeferredSurface(nint hdc, Document doc, Win32.RECT body)
    {
        if (body.Width <= 0 || body.Height <= 0)
        {
            return;
        }

        nint bgBrush = Win32.CreateSolidBrush(ThemePalette.Editor);
        try
        {
            Win32.FillRect(hdc, &body, bgBrush);
        }
        finally
        {
            Win32.DeleteObject(bgBrush);
        }

        if (!doc.CheckingSession && !doc.IdleUnloaded)
        {
            return;
        }

        bool checking = doc.CheckingSession;
        string message = checking
            ? "↻  Проверка сохранённой вкладки…"
            : "◷  Вкладка выгружена для экономии памяти · Загрузится при выборе";
        string buttonLabel = "Загрузить вкладку";

        int padX = S(24);
        int textMaxW = Math.Max(S(120), body.Width - padX * 2);
        int msgH = S(44);
        int btnW = Math.Min(textMaxW, Math.Max(S(160), MeasureButtonWidth(hdc, buttonLabel) + S(12)));
        int btnH = S(30);
        int gap = S(12);
        int totalH = msgH + gap + btnH;

        int topY = body.Top + Math.Max(S(12), (body.Height - totalH) / 2);
        var msgRect = new Win32.RECT
        {
            Left = body.Left + padX,
            Right = body.Right - padX,
            Top = topY,
            Bottom = topY + msgH,
        };

        Win32.SetBkMode(hdc, Win32.TRANSPARENT);
        Win32.SelectObject(hdc, _uiFont);
        Win32.SetTextColor(hdc, ThemePalette.Muted);
        Win32.DrawTextW(hdc, message, -1, &msgRect, Win32.DT_CENTER | DT_WORDBREAK | Win32.DT_NOPREFIX);

        int btnX = body.Left + Math.Max(0, (body.Width - btnW) / 2);
        int btnY = topY + msgH + gap;
        if (btnY + btnH <= body.Bottom - S(8))
        {
            var btnRect = new Win32.RECT
            {
                Left = btnX,
                Top = btnY,
                Right = btnX + btnW,
                Bottom = btnY + btnH,
            };
            bool hover = !checking && _hoverKind == HitKind.DeferredLoadTab && _hoverTabId == doc.Id;
            FillRoundRect(hdc, btnRect, S(6), hover ? ThemePalette.HoverBg : ThemePalette.Panel, ThemePalette.Border);
            Win32.SelectObject(hdc, _uiFont);
            Win32.SetTextColor(hdc, checking ? ThemePalette.Dim : ThemePalette.Text);
            Win32.DrawTextW(hdc, buttonLabel, -1, &btnRect,
                Win32.DT_CENTER | Win32.DT_VCENTER | Win32.DT_SINGLELINE | Win32.DT_NOPREFIX);
            if (!checking)
            {
                _hits.Add(new HitRect(btnRect, HitKind.DeferredLoadTab, doc.Id));
            }
        }
    }

    private string GetTabStatePrefix(Document doc)
    {
        if (doc.CheckingSession) return "↻ ";
        if (doc.IdleUnloaded) return "◷ ";
        if (doc.Dirty) return "● ";
        return "";
    }

    private void DisposeDocumentResources(Document doc)
    {
        if (doc.FileWatch is { } watch)
        {
            doc.FileWatch = null;
            watch.Dispose();
        }

        if (doc.ConsoleSession is { } console)
        {
            doc.ConsoleSession = null;
            console.Dispose();
        }

        DestroyEditorFilterPreview(doc);
        DestroyPageEditorHost(doc);

        if (doc.Log is { } log)
        {
            log.Dispose();
            doc.Log = null;
        }

        if (doc.Viewport is { } viewport)
        {
            viewport.Dispose();
            doc.Viewport = null;
            doc.EditorHwnd = 0;
        }
        else if (doc.EditorHwnd != 0)
        {
            nint hwnd = doc.EditorHwnd;
            doc.EditorHwnd = 0;
            Win32.DestroyWindow(hwnd);
        }

        _editorIndicators.Remove(doc.Id);
        _mapSamples.Remove(doc.Id);
        _scrollBounds.Remove(doc.Id);
        _lineNumberCache.Remove(doc.Id);
        _linkIndicatorCache.Remove(doc.Id);
        if (_minimapPreviews.Remove(doc.Id, out var preview))
        {
            preview.Dispose();
        }
        if (_scrollDrag?.Id == doc.Id)
        {
            _scrollDrag = null;
            Win32.ReleaseCapture();
        }

        doc.DraftBytes = null;
    }
}
