using System.Runtime.InteropServices;
using NetText.Core;
using NetText.Native;

namespace NetText.UI;

internal sealed unsafe partial class MainWindow
{
    private void TickJsonValidation()
    {
        foreach (var doc in _documents)
        {
            if (doc.UntitledNamePending)
            {
                doc.UntitledNamePending = false;
                UpdateUntitledName(doc);
                InvalidateChrome();
            }
        }
        if (_showHome || !HasDocument) return;
        var active = ActiveDocument;
        if (active.Log is not null || active.Placeholder || active.JsonDisabled ||
            active.JsonRevision == active.Revision || active.JsonDue > Environment.TickCount64) return;

        ulong revision = active.Revision;
        active.JsonRevision = revision;
        int length = ScintillaInterop.GetByteLength(active.EditorHwnd);
        string? filePath = active.File?.Path;

        var prefix = ScintillaInterop.ByteSpan(active.EditorHwnd, 0, length);
        if (!JsonFormat.LooksLikeJson(filePath, prefix))
        {
            active.JsonResultRevision = revision;
            active.JsonResult = null;
            active.JsonErrorPosition = -1;
            RefreshJsonIndicator(active);
            InvalidateChrome();
            return;
        }
        if (length > JsonFormat.ValidationLimit)
        {
            active.JsonResultRevision = revision;
            active.JsonResult = "Проверка JSON ограничена документами до 64 МиБ.";
            active.JsonErrorPosition = -1;
            RefreshJsonIndicator(active);
            InvalidateChrome();
            return;
        }

        var snapshot = ScintillaInterop.CopyUtf8BytesNative(active.EditorHwnd);
        try
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string? message = null;
                int position = -1;
                using (snapshot)
                {
                    if (!JsonFormat.TryValidate(snapshot.Span, filePath, out string error, out int offset, out int _))
                    {
                        message = error;
                        position = offset;
                    }
                }
                DispatchUi(() =>
                {
                    if (!_documents.Contains(active) || active.Revision != revision) return;
                    active.JsonResultRevision = revision;
                    active.JsonResult = message;
                    active.JsonErrorPosition = position;
                    RefreshJsonIndicator(active);
                    InvalidateChrome();
                });
            });
        }
        catch
        {
            snapshot.Dispose();
            throw;
        }
    }

    private void FormatActiveDocumentJson()
    {
        if (_busy is not null || !HasDocument || _showHome) return;
        var doc = ActiveDocument;
        if (doc.Placeholder || doc.Log is not null || doc.EditorHwnd == 0) return;

        CollapseToPrimaryCaret(doc.EditorHwnd);

        var (selStart, selEnd, _) = ScintillaInterop.GetSelection(doc.EditorHwnd);
        int totalLen = ScintillaInterop.GetByteLength(doc.EditorHwnd);
        bool hasSelection = selEnd > selStart;
        int rangeStart = hasSelection ? selStart : 0;
        int rangeEnd = hasSelection ? selEnd : totalLen;
        ReadOnlySpan<byte> newline = doc.Format.LineEndingsLabel == "CRLF" ? "\r\n"u8 : "\n"u8;

        var fullSpan = ScintillaInterop.ByteSpan(doc.EditorHwnd, 0, totalLen);
        bool jsonLines = JsonFormat.UsesJsonLines(doc.File?.Path, fullSpan);
        var slice = ScintillaInterop.ByteSpan(doc.EditorHwnd, rangeStart, rangeEnd - rangeStart);

        _error = null;
        if (!JsonFormat.TryFormatUtf8Native(slice, newline, jsonLines, out byte* formattedPtr, out int formattedLen, out string error))
        {
            _error = error;
            InvalidateChrome();
            return;
        }

        try
        {
            var formattedSpan = new ReadOnlySpan<byte>(formattedPtr, formattedLen);
            if (!formattedSpan.SequenceEqual(slice))
            {
                ScintillaInterop.Send(doc.EditorHwnd, ScintillaInterop.SCI_BEGINUNDOACTION, 0, 0);
                try
                {
                    ScintillaInterop.Send(doc.EditorHwnd, ScintillaInterop.SCI_SETTARGETSTART, (nuint)rangeStart, 0);
                    ScintillaInterop.Send(doc.EditorHwnd, ScintillaInterop.SCI_SETTARGETEND, (nuint)rangeEnd, 0);
                    ScintillaInterop.Send(doc.EditorHwnd, ScintillaInterop.SCI_REPLACETARGET, (nuint)formattedLen,
                        formattedLen == 0 ? 0 : (nint)formattedPtr);
                    if (hasSelection || rangeStart > 0)
                    {
                        ScintillaInterop.SetSelection(doc.EditorHwnd, rangeStart, rangeStart + formattedLen, false);
                    }
                }
                finally
                {
                    ScintillaInterop.Send(doc.EditorHwnd, ScintillaInterop.SCI_ENDUNDOACTION, 0, 0);
                }

                doc.Dirty = true;
                doc.Revision++;
                doc.JsonDue = 0;
                UpdateActiveCursor();
            }
        }
        finally
        {
            if (formattedPtr is not null) NativeMemory.Free(formattedPtr);
        }

        FocusActiveEditor();
        InvalidateChrome();
    }

    private void ToggleJsonValidation()
    {
        if (!HasDocument || _showHome) return;
        var doc = ActiveDocument;
        doc.JsonDisabled = !doc.JsonDisabled;
        RefreshJsonIndicator(doc);
        InvalidateChrome();
    }

    private void OpenJsonError()
    {
        var doc = ActiveDocument;
        if (doc.JsonError is null || doc.JsonErrorPosition < 0) return;
        int position = doc.JsonErrorPosition;
        ScintillaInterop.SetSelection(doc.EditorHwnd, position, position, false);
        FocusActiveEditor();
    }

    private static void RefreshJsonIndicator(Document doc)
    {
        int position = doc.JsonError is null ? -1 : doc.JsonErrorPosition;
        if (position < 0 && !doc.JsonMarked) return;
        ScintillaInterop.SetJsonIndicator(doc.EditorHwnd, position);
        doc.JsonMarked = position >= 0;
    }
}
