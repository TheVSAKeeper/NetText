using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NetText.Native;

namespace NetText.UI;

internal sealed unsafe partial class MainWindow
{
    [LibraryImport("user32.dll")]
    private static partial nint GetCapture();

    private sealed class TabDragPreview
    {
        private const string PreviewClassName = "NetTextTabDragPreview";
        private static readonly Dictionary<nint, TabDragPreview> Instances = [];
        private static ushort _previewClass;

        private readonly string _title;
        private readonly bool _dirty;
        private readonly uint _dpi;
        private readonly int _width;
        private readonly int _height;
        private nint _hwnd;
        private nint _font;

        public TabDragPreview(nint owner, nint hInstance, uint dpi, string title, bool dirty, int tabWidth)
        {
            _title = string.IsNullOrWhiteSpace(title) ? "Без имени" : title;
            _dirty = dirty;
            _dpi = Math.Max(96, dpi);
            _width = Math.Clamp(tabWidth + Scale(24), Scale(142), Scale(260));
            _height = Scale(40);

            if (_previewClass == 0)
            {
                fixed (char* clsName = PreviewClassName)
                {
                    var wc = new Win32.WNDCLASSEXW
                    {
                        cbSize = (uint)sizeof(Win32.WNDCLASSEXW),
                        lpfnWndProc = &PreviewWndProc,
                        hInstance = hInstance,
                        hCursor = Win32.LoadCursorW(0, Win32.IDC_ARROW),
                        lpszClassName = clsName,
                    };
                    _previewClass = Win32.RegisterClassExW(&wc);
                }
            }

            _font = Win32.CreateFontW(
                -Scale(13), 0, 0, 0, Win32.FW_NORMAL, 0, 0, 0,
                Win32.DEFAULT_CHARSET, 0, 0, Win32.CLEARTYPE_QUALITY, Win32.DEFAULT_PITCH, "Segoe UI");

            uint exStyle = Win32.WS_EX_LAYERED | Win32.WS_EX_TOPMOST | Win32.WS_EX_TOOLWINDOW |
                           Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TRANSPARENT;
            _hwnd = Win32.CreateWindowExW(
                exStyle,
                PreviewClassName,
                "",
                Win32.WS_POPUP,
                0, 0, _width, _height,
                owner, 0, hInstance, null);

            if (_hwnd != 0)
            {
                Instances[_hwnd] = this;
                Win32.SetLayeredWindowAttributes(_hwnd, 0, 235, Win32.LWA_ALPHA);
            }
        }

        public void Follow(int screenX, int screenY)
        {
            if (_hwnd == 0) return;
            Win32.SetWindowPos(
                _hwnd,
                Win32.HWND_TOPMOST,
                screenX + 10,
                screenY + 12,
                0,
                0,
                Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
            if (!WorkspaceSession.IsWindowVisible(_hwnd))
            {
                Win32.ShowWindow(_hwnd, Win32.SW_SHOWNOACTIVATE);
            }
        }

        public void Dispose()
        {
            if (_hwnd != 0)
            {
                Win32.DestroyWindow(_hwnd);
                _hwnd = 0;
            }
            if (_font != 0)
            {
                Win32.DeleteObject(_font);
                _font = 0;
            }
        }

        private int Scale(int px) => (int)MathF.Round(px * (_dpi / 96.0f));

        private void PaintPreview(nint hdc)
        {
            var rect = new Win32.RECT { Left = 0, Top = 0, Right = _width, Bottom = _height };
            nint bgBrush = Win32.CreateSolidBrush(ThemePalette.TabBar);
            Win32.FillRect(hdc, &rect, bgBrush);
            Win32.DeleteObject(bgBrush);

            var card = new Win32.RECT
            {
                Left = Scale(2),
                Top = Scale(2),
                Right = _width - Scale(2),
                Bottom = _height - Scale(2),
            };
            FillRoundRect(hdc, card, Scale(6), ThemePalette.TabActiveBg, ThemePalette.TabActiveBorder);

            var textRect = new Win32.RECT
            {
                Left = card.Left + Scale(10),
                Top = card.Top,
                Right = card.Right - Scale(10),
                Bottom = card.Bottom,
            };
            string caption = _dirty ? _title + "  ●" : _title;
            nint oldFont = Win32.SelectObject(hdc, _font);
            Win32.SetBkMode(hdc, Win32.TRANSPARENT);
            Win32.SetTextColor(hdc, ThemePalette.Text);
            Win32.DrawTextW(hdc, caption, -1, &textRect,
                Win32.DT_LEFT | Win32.DT_VCENTER | Win32.DT_SINGLELINE | Win32.DT_END_ELLIPSIS | Win32.DT_NOPREFIX);
            Win32.SelectObject(hdc, oldFont);
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static nint PreviewWndProc(nint hwnd, uint msg, nuint wParam, nint lParam)
        {
            if (Instances.TryGetValue(hwnd, out var preview))
            {
                switch (msg)
                {
                    case Win32.WM_ERASEBKGND:
                        return 1;
                    case Win32.WM_PAINT:
                        Win32.PAINTSTRUCT ps;
                        nint hdc = Win32.BeginPaint(hwnd, &ps);
                        preview.PaintPreview(hdc);
                        Win32.EndPaint(hwnd, &ps);
                        return 0;
                    case Win32.WM_NCDESTROY:
                        Instances.Remove(hwnd);
                        preview._hwnd = 0;
                        break;
                }
            }

            return Win32.DefWindowProcW(hwnd, msg, wParam, lParam);
        }
    }

    private readonly Dictionary<ulong, int> _tabDragOffsets = [];
    private ulong _justDraggedTabId;
    private TabDragPreview? _tabDragPreview;
    private MainWindow? _dragHoverWindow;
    private int _dragHoverGroup;
    private ulong _dragInsertBeforeId;
    private int _draggedTabWidth;

    internal bool CancelActiveTabDrag()
    {
        if (_dragTabId == 0 && !_draggingTab && _tabDragPreview is null)
        {
            return false;
        }
        CancelTabDrag();
        return true;
    }

    private void BeginTabDrag(ulong tabId, int clientX, int clientY)
    {
        _justDraggedTabId = 0;
        _dragTabId = tabId;
        _dragStartX = clientX;
        _dragStartY = clientY;
        _draggingTab = false;
        _dragHoverWindow = null;
        _dragHoverGroup = 0;
        _dragInsertBeforeId = 0;
        _draggedTabWidth = MeasureTabWidthEstimate(tabId);
        Win32.SetCapture(_hwnd);
    }

    private bool UpdateTabDrag(int clientX, int clientY)
    {
        if (_dragTabId == 0) return false;

        if (!_draggingTab)
        {
            int dx = clientX - _dragStartX;
            int dy = clientY - _dragStartY;
            int threshold = S(7);
            if (dx * dx + dy * dy <= threshold * threshold)
            {
                return false;
            }

            var doc = _documents.Find(d => d.Id == _dragTabId);
            if (doc is null || doc.IsConsole)
            {
                CancelTabDrag();
                return false;
            }

            _draggingTab = true;
            _justDraggedTabId = _dragTabId;
            _tabDragPreview = new TabDragPreview(_hwnd, _hInstance, _dpi, TabLabel(doc), doc.Dirty, _draggedTabWidth);
        }

        var screenPt = new Win32.POINT { X = clientX, Y = clientY };
        Win32.ClientToScreen(_hwnd, &screenPt);
        _tabDragPreview?.Follow(screenPt.X, screenPt.Y);

        var targetWindow = WorkspaceSession.Current.HitTestWindowAtScreenPoint(screenPt, this);
        int targetGroup = 0;
        ulong insertBeforeId = 0;
        if (targetWindow is not null)
        {
            targetWindow.TryComputeTabDropTarget(screenPt, _dragTabId, _draggedTabWidth, out targetGroup, out insertBeforeId);
        }

        if (!ReferenceEquals(_dragHoverWindow, targetWindow))
        {
            if (_dragHoverWindow is not null)
            {
                _dragHoverWindow._tabDragOffsets.Clear();
                _dragHoverWindow.InvalidateChrome();
            }
            _dragHoverWindow = targetWindow;
        }

        _dragHoverGroup = targetGroup;
        _dragInsertBeforeId = insertBeforeId;
        targetWindow?.ApplyTabDragOffsets(_dragTabId, targetGroup, insertBeforeId, _draggedTabWidth);
        return true;
    }

    private bool FinishTabDrag(int clientX, int clientY)
    {
        if (_dragTabId == 0) return false;

        ulong draggedId = _dragTabId;
        bool wasDragging = _draggingTab;

        var screenPt = new Win32.POINT { X = clientX, Y = clientY };
        Win32.ClientToScreen(_hwnd, &screenPt);

        var targetWindow = wasDragging ? WorkspaceSession.Current.HitTestWindowAtScreenPoint(screenPt, this) : null;
        int targetGroup = 0;
        ulong insertBeforeId = 0;
        bool hasDropTarget = targetWindow is not null &&
            targetWindow.TryComputeTabDropTarget(screenPt, draggedId, _draggedTabWidth, out targetGroup, out insertBeforeId);

        CancelTabDrag();

        if (!wasDragging)
        {
            return false;
        }

        var doc = _documents.Find(d => d.Id == draggedId);
        if (doc is null || doc.IsConsole)
        {
            return true;
        }

        if (targetWindow is not null && hasDropTarget)
        {
            TransferTabToWindow(doc, targetWindow, targetGroup, insertBeforeId);
        }
        else if (targetWindow is null)
        {
            OpenTabInNewWindow(doc, screenPt);
        }

        return true;
    }

    private void CancelTabDrag()
    {
        if (_dragHoverWindow is not null)
        {
            _dragHoverWindow._tabDragOffsets.Clear();
            _dragHoverWindow.InvalidateChrome();
            _dragHoverWindow = null;
        }

        _tabDragOffsets.Clear();
        _tabDragPreview?.Dispose();
        _tabDragPreview = null;
        _dragTabId = 0;
        _draggingTab = false;
        _dragHoverGroup = 0;
        _dragInsertBeforeId = 0;

        if (GetCapture() == _hwnd)
        {
            Win32.ReleaseCapture();
        }
        InvalidateChrome();
    }

    internal bool TryComputeTabDropTarget(
        Win32.POINT screenPoint,
        ulong draggedTabId,
        int draggedWidth,
        out int targetGroup,
        out ulong insertBeforeId)
    {
        targetGroup = _activeGroup;
        insertBeforeId = 0;

        var pt = screenPoint;
        Win32.ScreenToClient(_hwnd, &pt);

        if (_showHome)
        {
            targetGroup = _activeGroup;
            insertBeforeId = 0;
            return true;
        }

        if (SplitView && TryFindGroupDropTarget(pt.X, pt.Y, draggedTabId, out targetGroup, out insertBeforeId))
        {
            return true;
        }

        int barTop = S(ToolbarLogicalHeight);
        int barBottom = barTop + S(TabBarLogicalHeight);
        if (pt.Y >= barTop && pt.Y < barBottom)
        {
            targetGroup = _activeGroup;
            for (int i = 0; i < _hits.Count; i++)
            {
                var hit = _hits[i];
                if (hit.Kind == HitKind.TabSelect && hit.TabId != draggedTabId)
                {
                    int midX = (hit.Rect.Left + hit.Rect.Right) / 2;
                    if (pt.X < midX)
                    {
                        insertBeforeId = hit.TabId;
                        return true;
                    }
                }
            }
            return true;
        }

        if (SplitView)
        {
            foreach (var (group, bounds) in _groupBounds)
            {
                if (pt.X >= bounds.Left && pt.X < bounds.Right && pt.Y >= bounds.Top && pt.Y < bounds.Bottom)
                {
                    targetGroup = group;
                    insertBeforeId = 0;
                    return true;
                }
            }
        }

        targetGroup = _activeGroup;
        insertBeforeId = 0;
        return true;
    }

    private void ApplyTabDragOffsets(ulong draggedTabId, int targetGroup, ulong insertBeforeId, int draggedWidth)
    {
        _tabDragOffsets.Clear();
        int shift = Math.Max(S(64), draggedWidth) + S(4);
        var groupDocs = _documents.Where(d => d.Group == targetGroup && d.Id != draggedTabId).ToList();

        int insertIndex = insertBeforeId == 0
            ? groupDocs.Count
            : groupDocs.FindIndex(d => d.Id == insertBeforeId);
        if (insertIndex < 0) insertIndex = groupDocs.Count;

        for (int i = insertIndex; i < groupDocs.Count; i++)
        {
            _tabDragOffsets[groupDocs[i].Id] = shift;
        }

        InvalidateChrome();
    }

    private int MeasureTabWidthEstimate(ulong tabId)
    {
        var doc = _documents.Find(d => d.Id == tabId);
        if (doc is null || _hwnd == 0) return S(140);
        nint hdc = Win32.GetDC(_hwnd);
        if (hdc == 0) return S(140);
        try
        {
            int labelW = Math.Min(S(280), MeasureTextWidth(hdc, TabLabel(doc), _uiFont) + S(20));
            return labelW + S(24);
        }
        finally
        {
            Win32.ReleaseDC(_hwnd, hdc);
        }
    }
}
