using NetText.Core;
using NetText.Native;

namespace NetText.UI;

internal sealed unsafe partial class MainWindow
{
    internal void CollectOpenFileViews(string normalizedPath, TextEncoding encoding, List<OpenFileView> target)
    {
        for (int i = 0; i < _documents.Count; i++)
        {
            var doc = _documents[i];
            if (doc.Log is not null || doc.File is null || doc.EditorHwnd == 0) continue;
            if (!string.Equals(doc.File.Path, normalizedPath, StringComparison.OrdinalIgnoreCase)) continue;
            if (encoding != TextEncoding.None && doc.Format.Encoding != encoding) continue;

            target.Add(new OpenFileView(
                this,
                doc.Id,
                doc.EditorHwnd,
                doc.Format,
                doc.DiskStatus,
                doc.Dirty,
                doc.Placeholder,
                doc.File));
        }
    }

    private bool TryAttachSharedScintillaDocument(Document doc)
    {
        if (doc.File is not { } file || doc.Log is not null || doc.EditorHwnd == 0)
        {
            return false;
        }

        var views = WorkspaceSession.Current.FindOpenViewsForFile(file.Path, doc.Format.Encoding, excludeWindow: this);
        var source = views.FirstOrDefault(view => view.EditorHwnd != 0 && !view.Placeholder);
        if (source is null)
        {
            return false;
        }

        nint docPtr = ScintillaInterop.Send(source.EditorHwnd, ScintillaInterop.SCI_GETDOCPOINTER, 0, 0);
        if (docPtr == 0)
        {
            return false;
        }

        ScintillaInterop.Send(source.EditorHwnd, ScintillaInterop.SCI_ADDREFDOCUMENT, 0, docPtr);
        ScintillaInterop.Send(doc.EditorHwnd, ScintillaInterop.SCI_SETDOCPOINTER, 0, docPtr);

        doc.File = source.File ?? doc.File;
        doc.Format = source.Format;
        doc.Dirty = source.Dirty;
        doc.DiskStatus = source.DiskStatus;
        doc.Placeholder = false;
        doc.PendingEditorReady = false;
        ScintillaInterop.ApplyLanguage(doc.EditorHwnd, file.Path, (int)MathF.Round(_fontSize));
        return true;
    }

    private void PropagateSharedDocumentChange(Document source)
    {
        if (source.File is not { } file || source.Log is not null || source.EditorHwnd == 0) return;

        nint srcPtr = ScintillaInterop.Send(source.EditorHwnd, ScintillaInterop.SCI_GETDOCPOINTER, 0, 0);
        if (srcPtr == 0) return;

        var views = WorkspaceSession.Current.FindOpenViewsForFile(file.Path, source.Format.Encoding, excludeWindow: this);
        foreach (var view in views)
        {
            if (view.EditorHwnd == 0) continue;
            nint targetPtr = ScintillaInterop.Send(view.EditorHwnd, ScintillaInterop.SCI_GETDOCPOINTER, 0, 0);
            if (targetPtr == srcPtr)
            {
                view.Window.ApplySharedDocumentChange(
                    view.DocumentId,
                    source.Dirty,
                    source.DiskStatus,
                    source.Revision,
                    source.LastWorked);
            }
        }
    }

    private void PropagateSharedDocumentSaved(Document source)
    {
        if (source.File is not { } file || source.Log is not null || source.EditorHwnd == 0) return;

        nint srcPtr = ScintillaInterop.Send(source.EditorHwnd, ScintillaInterop.SCI_GETDOCPOINTER, 0, 0);
        var views = WorkspaceSession.Current.FindOpenViewsForFile(file.Path, source.Format.Encoding, excludeWindow: this);
        foreach (var view in views)
        {
            if (view.EditorHwnd == 0) continue;
            nint targetPtr = ScintillaInterop.Send(view.EditorHwnd, ScintillaInterop.SCI_GETDOCPOINTER, 0, 0);
            if (srcPtr == 0 || targetPtr == srcPtr)
            {
                view.Window.ApplySharedDocumentSaved(
                    view.DocumentId,
                    file,
                    source.Format,
                    source.Dirty,
                    source.DiskStatus,
                    source.Revision);
            }
        }
    }

    internal void ApplySharedDocumentChange(
        ulong docId,
        bool dirty,
        DiskStatusKind diskStatus,
        ulong revision,
        DateTimeOffset? lastWorked)
    {
        var doc = _documents.Find(d => d.Id == docId);
        if (doc is null || doc.Log is not null) return;

        doc.Dirty = dirty;
        doc.DiskStatus = diskStatus;
        doc.Revision = Math.Max(doc.Revision, revision);
        doc.LastWorked = lastWorked ?? doc.LastWorked;
        doc.DraftBytes = null;
        doc.MeasuredLines = null;
        doc.MeasuredWidth = 0;
        RefreshJsonIndicator(doc);
        InvalidateMinimap(doc.Id);
        InvalidateChrome();
    }

    internal void ApplySharedDocumentSaved(
        ulong docId,
        FileTarget savedTarget,
        TextFormat format,
        bool dirty,
        DiskStatusKind diskStatus,
        ulong revision)
    {
        var doc = _documents.Find(d => d.Id == docId);
        if (doc is null || doc.Log is not null) return;

        doc.File = savedTarget;
        doc.Format = format;
        if (doc.CustomTitle is null)
        {
            doc.Name = FileName(savedTarget.Path);
        }
        doc.Dirty = dirty;
        doc.Placeholder = false;
        doc.DiskStatus = diskStatus;
        doc.Revision = Math.Max(doc.Revision, revision);
        doc.DraftBytes = null;
        if (!dirty && doc.EditorHwnd != 0)
        {
            ScintillaInterop.SetSavePoint(doc.EditorHwnd);
        }
        InvalidateChrome();
    }
}
