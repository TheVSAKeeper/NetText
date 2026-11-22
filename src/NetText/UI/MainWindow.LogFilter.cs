using System.Text;
using NetText.Core;
using NetText.Native;

namespace NetText.UI;

internal sealed unsafe partial class MainWindow
{
    private const int LogFilterPanelLogicalHeight = 36;

    private static readonly (LogLevel Level, string Label)[] FilterOptions =
    [
        (LogLevel.Fatal, "Fatal"),
        (LogLevel.Error, "Error"),
        (LogLevel.Warning, "Warn"),
        (LogLevel.Info, "Info"),
        (LogLevel.Debug, "Debug"),
        (LogLevel.Trace, "Trace"),
        (LogLevel.Other, "Без уровня"),
    ];

    private readonly Dictionary<ulong, CancellationTokenSource> _editorFilterCts = [];
    private readonly Dictionary<ulong, long> _editorFilterGen = [];
    private bool _logFilterOpen;

    private int GetLogFilterPanelHeight() =>
        _logFilterOpen && HasDocument && !_showHome ? S(LogFilterPanelLogicalHeight) : 0;

    private void ToggleLogLevelsPanel()
    {
        if (!HasDocument || _showHome) return;
        if (ActiveDocument.Log is { IsEditingPage: true, EditsPagesByDefault: false }) return;
        _logFilterOpen = !_logFilterOpen;
        LayoutAndSyncControls();
        InvalidateChrome();
    }

    private LogLevel GetActiveDocFilterLevels(Document doc) =>
        doc.Log is { } log ? log.SelectedLevels : doc.ActiveLogFilter;

    private void PaintLogLevelFilterPanel(nint hdc, int width, int top)
    {
        if (!_logFilterOpen || !HasDocument || _showHome) return;

        var doc = ActiveDocument;
        int panelH = S(LogFilterPanelLogicalHeight);
        var panelRect = new Win32.RECT { Left = 0, Top = top, Right = width, Bottom = top + panelH };
        Win32.FillRect(hdc, &panelRect, _panelBrush);
        DrawHLine(hdc, 0, top, width, ThemePalette.Border);

        LogLevel selected = GetActiveDocFilterLevels(doc);
        int pad = S(12);
        int gap = S(4);
        int btnH = S(26);
        int y = top + S(5);
        int x = pad;

        foreach (var (level, label) in FilterOptions)
        {
            bool active = (selected & level) != 0;
            x = DrawButton(hdc, x, y, btnH, label, HitKind.LogFilterToggle, (ulong)level, false, active) + gap;
        }

        string allLabel = selected == LogLevel.All ? "Снять все" : "Выбрать все";
        x = DrawButton(hdc, x, y, btnH, allLabel, HitKind.LogFilterAll, 0, false, false) + S(8);

        int closeW = S(28);
        int closeX = width - pad - closeW;
        DrawButtonFixed(hdc, closeX, y, closeW, btnH, "×", HitKind.LogFilterClose, 0, false, false);

        string? status = doc.Log is { } log
            ? log.FilterStatus
            : doc.ActiveLogFilter != LogLevel.All
                ? $"Просмотр фильтра · только чтение · {LineNumbers.Format(doc.FilterSourceLines?.Length ?? 0)} строк"
                : null;
        if (!string.IsNullOrEmpty(status) && x < closeX - gap)
        {
            var stRect = new Win32.RECT { Left = x, Top = y, Right = closeX - gap, Bottom = y + btnH };
            Win32.SelectObject(hdc, _smallFont);
            Win32.SetTextColor(hdc, ThemePalette.Muted);
            Win32.DrawTextW(hdc, status, -1, &stRect,
                Win32.DT_LEFT | Win32.DT_VCENTER | Win32.DT_SINGLELINE | Win32.DT_END_ELLIPSIS | Win32.DT_NOPREFIX);
        }
    }

    private void HandleLogFilterHit(HitRect hit)
    {
        if (!HasDocument || _showHome) return;
        var doc = ActiveDocument;
        LogLevel current = GetActiveDocFilterLevels(doc);

        switch (hit.Kind)
        {
            case HitKind.LogFilterToggle:
                ApplyDocLogFilter(doc, current ^ (LogLevel)hit.TabId);
                break;

            case HitKind.LogFilterAll:
                ApplyDocLogFilter(doc, current == LogLevel.All ? LogLevel.None : LogLevel.All);
                break;

            case HitKind.LogFilterClose:
                ApplyDocLogFilter(doc, LogLevel.All);
                _logFilterOpen = false;
                LayoutAndSyncControls();
                FocusActiveEditor();
                InvalidateChrome();
                break;
        }
    }

    private void ApplyDocLogFilter(Document doc, LogLevel levels)
    {
        if (doc.Log is { } log)
        {
            log.SetLevelFilter(levels);
            InvalidateChrome();
            return;
        }

        doc.ActiveLogFilter = levels;
        SyncEditorLogFilter(doc);
        InvalidateChrome();
    }

    private void SyncEditorLogFilter(Document doc)
    {
        if (_editorFilterCts.TryGetValue(doc.Id, out var prevCts))
        {
            prevCts.Cancel();
            prevCts.Dispose();
            _editorFilterCts.Remove(doc.Id);
        }

        long gen = _editorFilterGen.TryGetValue(doc.Id, out long prevGen) ? prevGen + 1 : 1;
        _editorFilterGen[doc.Id] = gen;

        if (doc.Log is not null || doc.ActiveLogFilter == LogLevel.All)
        {
            DestroyEditorFilterPreview(doc);
            doc.FilterSourceLines = null;
            if (doc.EditorHwnd != 0)
            {
                ScintillaInterop.Send(doc.EditorHwnd, ScintillaInterop.SCI_SETREADONLY, 0, 0);
                if (doc.Id == _activeId && !_showHome)
                {
                    Win32.ShowWindow(doc.EditorHwnd, Win32.SW_SHOW);
                    Win32.SetFocus(doc.EditorHwnd);
                }
            }
            return;
        }

        ScintillaInterop.Send(doc.EditorHwnd, ScintillaInterop.SCI_SETREADONLY, 1, 0);

        nint preview = doc.FilterPreviewHwnd;
        if (preview == 0)
        {
            preview = ScintillaInterop.CreateEditorControl(_hwnd, _hInstance, (int)MathF.Round(_fontSize), _wrap);
            ScintillaInterop.Send(preview, ScintillaInterop.SCI_SETMARGINTYPEN, 0, ScintillaInterop.SC_MARGIN_RTEXT);
            ScintillaInterop.Send(preview, ScintillaInterop.SCI_SETREADONLY, 1, 0);
            doc.FilterPreviewHwnd = preview;
        }

        SyncFilterPreviewBounds(doc, preview);
        Win32.ShowWindow(doc.EditorHwnd, Win32.SW_HIDE);
        Win32.ShowWindow(preview, Win32.SW_SHOW);

        string liveText = ScintillaInterop.GetTextString(doc.EditorHwnd);
        LogLevel levels = doc.ActiveLogFilter;
        var cts = new CancellationTokenSource();
        _editorFilterCts[doc.Id] = cts;
        CancellationToken ct = cts.Token;

        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                FilteredEditorProjection projection = EditorLogFilter.Filter(liveText, levels, ct);
                DispatchUi(() =>
                {
                    if (ct.IsCancellationRequested || !_documents.Contains(doc) ||
                        !_editorFilterGen.TryGetValue(doc.Id, out long curGen) || curGen != gen ||
                        doc.FilterPreviewHwnd == 0)
                    {
                        return;
                    }

                    ApplyEditorFilterProjection(doc, doc.FilterPreviewHwnd, projection);
                    InvalidateChrome();
                });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
        });
    }

    private void SyncFilterPreviewBounds(Document doc, nint previewHwnd)
    {
        if (doc.EditorHwnd == 0 || previewHwnd == 0) return;
        Win32.RECT rc;
        Win32.GetWindowRect(doc.EditorHwnd, &rc);
        var tl = new Win32.POINT { X = rc.Left, Y = rc.Top };
        var br = new Win32.POINT { X = rc.Right, Y = rc.Bottom };
        Win32.ScreenToClient(_hwnd, &tl);
        Win32.ScreenToClient(_hwnd, &br);
        Win32.MoveWindow(previewHwnd, tl.X, tl.Y, Math.Max(0, br.X - tl.X), Math.Max(0, br.Y - tl.Y), true);
    }

    private static void ApplyEditorFilterProjection(Document doc, nint previewHwnd, FilteredEditorProjection projection)
    {
        int count = projection.SourceLines.Count;
        var sourceLines = new int[count];
        int maxLine = 1;
        for (int i = 0; i < count; i++)
        {
            int line = projection.SourceLines[i];
            sourceLines[i] = line;
            if (line > maxLine) maxLine = line;
        }
        doc.FilterSourceLines = sourceLines;

        ScintillaInterop.Send(previewHwnd, ScintillaInterop.SCI_SETREADONLY, 0, 0);
        ScintillaInterop.SetTextUtf8(previewHwnd, Encoding.UTF8.GetBytes(projection.Text));
        ScintillaInterop.Send(previewHwnd, ScintillaInterop.SCI_SETMARGINTYPEN, 0, ScintillaInterop.SC_MARGIN_RTEXT);

        string sample = LineNumbers.Format(maxLine) + " ";
        byte[] sampleBytes = Encoding.UTF8.GetBytes(sample + "\0");
        fixed (byte* pSample = sampleBytes)
        {
            int marginWidth = (int)ScintillaInterop.Send(previewHwnd, ScintillaInterop.SCI_TEXTWIDTH, ScintillaInterop.STYLE_LINENUMBER, (nint)pSample);
            ScintillaInterop.Send(previewHwnd, ScintillaInterop.SCI_SETMARGINWIDTHN, 0, marginWidth + 10);
        }

        for (int i = 0; i < count; i++)
        {
            string label = LineNumbers.Format(sourceLines[i]);
            byte[] labelBytes = Encoding.UTF8.GetBytes(label + "\0");
            fixed (byte* pLabel = labelBytes)
            {
                ScintillaInterop.Send(previewHwnd, ScintillaInterop.SCI_MARGINSETTEXT, (nuint)i, (nint)pLabel);
            }
            ScintillaInterop.Send(previewHwnd, ScintillaInterop.SCI_MARGINSETSTYLE, (nuint)i, ScintillaInterop.STYLE_LINENUMBER);
        }

        ScintillaInterop.Send(previewHwnd, ScintillaInterop.SCI_SETREADONLY, 1, 0);
    }

    private void DestroyEditorFilterPreview(Document doc)
    {
        if (_editorFilterCts.TryGetValue(doc.Id, out var prevCts))
        {
            prevCts.Cancel();
            prevCts.Dispose();
            _editorFilterCts.Remove(doc.Id);
        }

        nint preview = doc.FilterPreviewHwnd;
        if (preview == 0) return;

        doc.FilterPreviewHwnd = 0;
        if (Win32.GetFocus() == preview)
        {
            Win32.SetFocus(doc.EditorHwnd != 0 ? doc.EditorHwnd : _hwnd);
        }
        Win32.DestroyWindow(preview);
    }
}
