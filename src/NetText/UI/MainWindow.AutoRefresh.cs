using System.IO;
using System.Security.Cryptography;
using System.Text;
using NetText.Core;
using NetText.Native;

namespace NetText.UI;

internal sealed unsafe partial class MainWindow
{
    internal const string ExternalDraftStatus = "Файл обновлён с диска. Локальные правки перенесены в отдельную вкладку-черновик.";

    private readonly HashSet<ulong> _autoRefreshInFlight = [];
    private readonly Dictionary<ulong, string> _lastKnownFileHashes = [];

    private sealed record DiskRefreshSnapshot(
        FileVersion Version,
        string? RawSha256,
        OpenedFile? Opened,
        bool Oversized,
        bool UnchangedByVersion);

    private void AttachFileWatch(Document doc)
    {
        if (!doc.AutoRefresh || doc.IsConsole || doc.IdleUnloaded || doc.Placeholder || doc.File is not { } file)
        {
            DetachFileWatch(doc);
            return;
        }

        string fullPath = Path.GetFullPath(file.Path);
        if (doc.FileWatch is { } existing && string.Equals(existing.Path, fullPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        DetachFileWatch(doc);
        doc.FileWatch = new EditorFileWatch(fullPath, () => OnExternalFileChanged(doc), DispatchUi);
    }

    private void DetachFileWatch(Document doc)
    {
        if (doc.FileWatch is { } watch)
        {
            doc.FileWatch = null;
            watch.Dispose();
        }
    }

    partial void OnDocumentAutoRefreshToggled(Document doc)
    {
        if (doc.AutoRefresh)
        {
            AttachFileWatch(doc);
        }
        else
        {
            DetachFileWatch(doc);
        }

        if (doc.Log is { } log)
        {
            log.Bookmark.AutoRefresh = doc.AutoRefresh;
        }

        if (doc.DeferredState is { } saved)
        {
            LogBookmark? savedLog = saved.Log is null ? null : saved.Log.Copy();
            if (savedLog is not null)
            {
                savedLog.AutoRefresh = doc.AutoRefresh;
            }
            doc.DeferredState = saved with
            {
                AutoRefresh = doc.AutoRefresh,
                Log = savedLog,
            };
        }

        QueueSessionTick();
        InvalidateChrome();
    }

    private void OnExternalFileChanged(Document doc)
    {
        if (_windowClosed || _closingForSession || _busy is not null)
        {
            return;
        }

        if (!doc.AutoRefresh || doc.IsConsole || doc.IdleUnloaded || doc.Placeholder || doc.File is not { } target)
        {
            return;
        }

        if (doc.Log is { } logState &&
            ((!logState.IsEditingPage && !logState.EditsPagesByDefault) ||
             logState.IsSavingPage || logState.PreparingTrim || logState.IsWritingFile))
        {
            return;
        }

        if (!_autoRefreshInFlight.Add(doc.Id))
        {
            return;
        }

        ulong docId = doc.Id;
        string path = target.Path;
        FileVersion? knownVersion = target.Version;
        bool win1251 = doc.Format.Encoding == TextEncoding.Windows1251 || doc.Log?.Windows1251 == true;
        bool isLog = doc.Log is not null;
        string? prevHash = _lastKnownFileHashes.GetValueOrDefault(docId);
        CancellationToken watchToken = doc.FileWatch?.Token ?? CancellationToken.None;

        _ = Task.Run(() =>
        {
            try
            {
                var snapshot = EditorFileWatch.ReadWithRetryAsync(ct =>
                {
                    ct.ThrowIfCancellationRequested();
                    var currentVersion = FileVersion.At(path)
                        ?? throw new FileNotFoundException("Файл отсутствует на диске.", path);

                    if (knownVersion is { } kv && currentVersion == kv)
                    {
                        return Task.FromResult(new DiskRefreshSnapshot(currentVersion, null, null, Oversized: false, UnchangedByVersion: true));
                    }

                    if (isLog || currentVersion.Bytes > Files.MaxEditorBytes)
                    {
                        return Task.FromResult(new DiskRefreshSnapshot(
                            currentVersion,
                            null,
                            null,
                            Oversized: currentVersion.Bytes > Files.MaxEditorBytes,
                            UnchangedByVersion: false));
                    }

                    byte[] rawBytes = ReadAllBytesShared(path, ct);
                    if (FileVersion.At(path) != currentVersion)
                    {
                        throw new IOException("Файл изменился во время чтения.");
                    }

                    string rawSha = Convert.ToHexString(SHA256.HashData(rawBytes));
                    if (prevHash is not null && string.Equals(prevHash, rawSha, StringComparison.Ordinal))
                    {
                        return Task.FromResult(new DiskRefreshSnapshot(currentVersion, rawSha, null, Oversized: false, UnchangedByVersion: true));
                    }

                    var (utf8Bytes, format) = TextFormat.DecodeOwnedToUtf8(rawBytes, win1251);
                    var opened = new OpenedFile(new FileTarget(path, currentVersion), utf8Bytes, format);
                    return Task.FromResult(new DiskRefreshSnapshot(currentVersion, rawSha, opened, Oversized: false, UnchangedByVersion: false));
                }, watchToken).GetAwaiter().GetResult();

                DispatchUi(() => ApplyExternalFileChange(docId, target, snapshot, null));
            }
            catch (OperationCanceledException) when (watchToken.IsCancellationRequested)
            {
                DispatchUi(() => _autoRefreshInFlight.Remove(docId));
            }
            catch (Exception ex)
            {
                DispatchUi(() => ApplyExternalFileChange(docId, target, null, ex));
            }
        });
    }

    private static byte[] ReadAllBytesShared(string path, CancellationToken ct)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        long length = stream.Length;
        if (length > Files.MaxEditorBytes)
        {
            throw new InvalidDataException("Файл превышает предел редактора.");
        }

        int expected = (int)length;
        byte[] bytes = GC.AllocateUninitializedArray<byte>(expected);
        int offset = 0;
        while (offset < expected)
        {
            ct.ThrowIfCancellationRequested();
            int read = stream.Read(bytes, offset, expected - offset);
            if (read == 0)
            {
                break;
            }
            offset += read;
        }
        return offset == expected ? bytes : bytes[..offset];
    }

    private void ApplyExternalFileChange(ulong docId, FileTarget expectedTarget, DiskRefreshSnapshot? snapshot, Exception? error)
    {
        _autoRefreshInFlight.Remove(docId);

        var doc = _documents.Find(d => d.Id == docId);
        if (_windowClosed || _closingForSession || doc is null || !doc.AutoRefresh ||
            doc.IsConsole || doc.IdleUnloaded || doc.Placeholder || doc.File != expectedTarget)
        {
            return;
        }

        if (error is not null)
        {
            doc.DiskStatus = DiskStatus.FromException(error);
            if (doc.Id == _activeId)
            {
                InvalidateChrome();
            }
            return;
        }

        if (snapshot is null)
        {
            return;
        }

        if (snapshot.RawSha256 is not null)
        {
            _lastKnownFileHashes[doc.Id] = snapshot.RawSha256;
        }

        if (snapshot.UnchangedByVersion)
        {
            doc.File = expectedTarget with { Version = snapshot.Version };
            if (doc.DiskStatus != DiskStatusKind.Current)
            {
                doc.DiskStatus = DiskStatusKind.Current;
                InvalidateChrome();
            }
            return;
        }

        if (doc.Log is { } log)
        {
            if (log.IsSavingPage || log.PreparingTrim || log.IsWritingFile)
            {
                return;
            }

            bool dirtyPageEdit = log.IsEditingPage && log.HasPendingEdit;
            if (dirtyPageEdit)
            {
                if (_pageEditors.TryGetValue(doc.Id, out nint pageHwnd) && pageHwnd != 0)
                {
                    string editedText = ScintillaInterop.GetTextString(pageHwnd);
                    PreservePageDraft(doc, editedText);
                }
                CancelLogPageEdit(doc);
                doc.RecoveryStatus = ExternalDraftStatus;
                log.Notice = ExternalDraftStatus;
            }
            else if (log.IsEditingPage)
            {
                CancelLogPageEdit(doc);
            }
            else if (!log.EditsPagesByDefault)
            {
                return;
            }

            doc.File = expectedTarget with { Version = snapshot.Version };
            doc.DiskStatus = DiskStatusKind.Current;
            log.ResetIndex();
            log.Navigate(LogDestination.AtOffset(log.Bookmark.Offset));
            if (dirtyPageEdit)
            {
                if (!SaveSessionNow())
                {
                    QueueSessionTick();
                }
            }
            InvalidateChrome();
            return;
        }

        if (!doc.Dirty && snapshot.Opened is { } cleanCheck && doc.EditorHwnd != 0 &&
            doc.Format == cleanCheck.Format &&
            ScintillaInterop.ByteSpan(doc.EditorHwnd, 0, ScintillaInterop.GetByteLength(doc.EditorHwnd)).SequenceEqual(cleanCheck.Utf8Bytes))
        {
            doc.File = cleanCheck.Target;
            if (doc.DiskStatus != DiskStatusKind.Current)
            {
                doc.DiskStatus = DiskStatusKind.Current;
                InvalidateChrome();
            }
            return;
        }

        if (snapshot.Oversized || snapshot.Opened is null)
        {
            bool wasDirty = doc.Dirty;
            if (wasDirty && doc.EditorHwnd != 0)
            {
                byte[] draftBytes = ScintillaInterop.CopyUtf8Bytes(doc.EditorHwnd);
                var selection = ScintillaInterop.GetSelection(doc.EditorHwnd);
                var scroll = ScintillaInterop.GetScroll(doc.EditorHwnd);
                AddExternalDraftTab(doc, draftBytes, doc.Format, selection, scroll);
            }

            bool wasActive = _activeId == doc.Id;
            if (wasActive)
            {
                _activeId = 0;
            }
            DestroyEditorFilterPreview(doc);
            if (doc.EditorHwnd != 0)
            {
                nint oldHwnd = doc.EditorHwnd;
                doc.EditorHwnd = 0;
                Win32.DestroyWindow(oldHwnd);
            }
            if (wasActive)
            {
                _activeId = doc.Id;
            }

            bool win1251 = doc.Format.Encoding == TextEncoding.Windows1251;
            var newTarget = expectedTarget with { Version = snapshot.Version };
            var bookmark = new LogBookmark
            {
                AutoRefresh = doc.AutoRefresh,
                EditPages = true,
            };
            RestoreLogOntoDocument(doc, newTarget, win1251, bookmark, restored: false);
            doc.DraftRevision = null;
            doc.EditPages = true;
            if (wasDirty && doc.Log is { } newLog)
            {
                doc.RecoveryStatus = ExternalDraftStatus;
                newLog.Notice = ExternalDraftStatus;
                if (!SaveSessionNow())
                {
                    QueueSessionTick();
                }
            }

            LayoutAndSyncControls();
            if (wasActive && !_showHome)
            {
                FocusActiveEditor();
            }
            InvalidateChrome();
            return;
        }

        var opened = snapshot.Opened;
        if (!doc.Dirty)
        {
            var (selStart, selEnd, reversed) = ScintillaInterop.GetSelection(doc.EditorHwnd);
            var (sx, sy) = ScintillaInterop.GetScroll(doc.EditorHwnd);

            ScintillaInterop.SetTextUtf8(doc.EditorHwnd, opened.Utf8Bytes);
            ScintillaInterop.ApplyLanguage(doc.EditorHwnd, opened.Target.Path, (int)MathF.Round(_fontSize));
            ScintillaInterop.SetSavePoint(doc.EditorHwnd);

            int newLen = ScintillaInterop.GetByteLength(doc.EditorHwnd);
            ScintillaInterop.SetSelection(doc.EditorHwnd, Math.Clamp(selStart, 0, newLen), Math.Clamp(selEnd, 0, newLen), reversed);
            ScintillaInterop.SetScroll(doc.EditorHwnd, sx, sy);

            doc.File = opened.Target;
            doc.Format = opened.Format;
            doc.Dirty = false;
            doc.DraftBytes = null;
            doc.DiskStatus = DiskStatusKind.Current;
            doc.Revision++;
            doc.MeasuredLines = null;
            doc.MeasuredWidth = 0;
            doc.JsonDue = Environment.TickCount64 + 300;
            RefreshJsonIndicator(doc);
            InvalidateMinimap(doc.Id);
            if (doc.ActiveLogFilter != LogLevel.All)
            {
                SyncEditorLogFilter(doc);
            }
            if (doc.Id == _activeId)
            {
                UpdateActiveCursor();
            }
            InvalidateChrome();
            return;
        }

        byte[] dirtyBytes = ScintillaInterop.CopyUtf8Bytes(doc.EditorHwnd);
        var dirtySel = ScintillaInterop.GetSelection(doc.EditorHwnd);
        var dirtyScroll = ScintillaInterop.GetScroll(doc.EditorHwnd);
        AddExternalDraftTab(doc, dirtyBytes, doc.Format, dirtySel, dirtyScroll);

        ScintillaInterop.SetTextUtf8(doc.EditorHwnd, opened.Utf8Bytes);
        ScintillaInterop.ApplyLanguage(doc.EditorHwnd, opened.Target.Path, (int)MathF.Round(_fontSize));
        ScintillaInterop.SetSavePoint(doc.EditorHwnd);

        doc.File = opened.Target;
        doc.Format = opened.Format;
        doc.Dirty = false;
        doc.DraftBytes = null;
        doc.DraftRevision = null;
        doc.DiskStatus = DiskStatusKind.Current;
        doc.RecoveryStatus = ExternalDraftStatus;
        doc.Revision++;
        doc.MeasuredLines = null;
        doc.MeasuredWidth = 0;
        doc.JsonDue = Environment.TickCount64 + 300;
        RefreshJsonIndicator(doc);
        InvalidateMinimap(doc.Id);
        if (doc.ActiveLogFilter != LogLevel.All)
        {
            SyncEditorLogFilter(doc);
        }
        if (doc.Id == _activeId)
        {
            UpdateActiveCursor();
        }

        if (!SaveSessionNow())
        {
            QueueSessionTick();
        }
        LayoutAndSyncControls();
        InvalidateChrome();
    }

    private Document AddExternalDraftTab(
        Document source,
        byte[] utf8Bytes,
        TextFormat format,
        (int Start, int End, bool Reversed) selection,
        (int XOffset, int FirstVisibleLine) scroll)
    {
        ulong id = _nextId++;
        string draftTitle = $"{source.Name} (черновик)";
        nint editorHwnd = ScintillaInterop.CreateEditorControl(_hwnd, _hInstance, (int)MathF.Round(_fontSize), _wrap);
        ScintillaInterop.SetTextUtf8(editorHwnd, utf8Bytes);
        ScintillaInterop.ApplyLanguage(editorHwnd, source.File?.Path, (int)MathF.Round(_fontSize));
        ScintillaInterop.SetSelection(editorHwnd, selection.Start, selection.End, selection.Reversed);
        ScintillaInterop.SetScroll(editorHwnd, scroll.XOffset, scroll.FirstVisibleLine);

        var draft = new Document
        {
            Id = id,
            Group = source.Group,
            Name = draftTitle,
            CustomTitle = draftTitle,
            File = null,
            Format = format,
            EditorHwnd = editorHwnd,
            Dirty = true,
            DraftRevision = 1,
            DraftBytes = utf8Bytes,
            IsExternalDraft = true,
            LastWorked = DateTimeOffset.UtcNow,
        };
        _documents.Add(draft);
        Win32.ShowWindow(editorHwnd, Win32.SW_HIDE);
        return draft;
    }

    private void PreservePageDraft(Document source, string text)
    {
        byte[] utf8Bytes = Encoding.UTF8.GetBytes(text);
        if (utf8Bytes.Length <= Files.MaxEditorBytes)
        {
            AddExternalDraftTab(source, utf8Bytes, source.Format, default, default);
            return;
        }

        string draftTitle = $"{source.Name} (черновик)";
        string draftPath = Path.Combine(Path.GetTempPath(), $"nettext-draft-{Guid.NewGuid():N}.log");
        File.WriteAllBytes(draftPath, utf8Bytes);
        var target = new FileTarget(draftPath, FileVersion.At(draftPath));

        ulong id = _nextId++;
        var draft = new Document
        {
            Id = id,
            Group = source.Group,
            Name = draftTitle,
            CustomTitle = draftTitle,
            File = target,
            Format = source.Format,
            AutoRefresh = false,
            IsExternalDraft = true,
            LastWorked = DateTimeOffset.UtcNow,
        };
        RestoreLogOntoDocument(draft, target, source.Format.Encoding == TextEncoding.Windows1251, new LogBookmark { AutoRefresh = false }, restored: false);
        if (draft.Log is { } draftLog)
        {
            draftLog.IsDraft = true;
        }
        _documents.Add(draft);
        if (draft.EditorHwnd != 0)
        {
            Win32.ShowWindow(draft.EditorHwnd, Win32.SW_HIDE);
        }
    }
}
