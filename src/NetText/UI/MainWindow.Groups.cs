using NetText.Core;
using NetText.Native;

namespace NetText.UI;

internal sealed unsafe partial class MainWindow
{
    private SessionPane _panes = new(0);
    private readonly Dictionary<int, ulong> _groupTabs = [];
    private readonly Dictionary<int, Win32.RECT> _groupBounds = [];
    private readonly Dictionary<int, Win32.RECT> _groupHeaderBounds = [];
    private readonly Dictionary<int, int> _groupTabScrollX = [];
    private readonly Dictionary<int, int> _groupMaxScrollX = [];
    private readonly List<(SessionPane Pane, Win32.RECT Bounds, Win32.RECT Handle)> _dividers = [];
    private (SessionPane Pane, Win32.RECT Bounds)? _dragDivider;
    private int _activeGroup;
    private int _nextGroup = 1;
    private int _dragStartY;
    private bool SplitView => _panes.First is not null;

    private void SplitGroup(bool vertical)
    {
        if (_busy is not null || _documents.Count == 0 || _showHome) return;
        if (_groupBounds.Count >= 8) return;
        int oldGroup = _activeGroup;
        int newGroup = _nextGroup++;
        _panes = EditPane(_panes, pane => pane.First is null && pane.Group == oldGroup
            ? new SessionPane(-1, vertical, .5, pane, new SessionPane(newGroup)) : pane);
        FocusEmptyGroup(newGroup);
    }

    private void FocusEmptyGroup(int group)
    {
        _activeGroup = group;
        _activeId = 0;
        _tabScrollX = 0;
        _showHome = false;
        _searchOpen = false;
        ResetEditorSearch();
        LayoutAndSyncControls();
        Win32.SetFocus(_hwnd);
        InvalidateChrome();
    }

    private void CloseEmptyGroup(int group)
    {
        if (!SplitView || _documents.Exists(doc => doc.Group == group)) return;
        RemoveEmptyGroup(group);
        if (_activeGroup == group && _documents.Count > 0)
            SelectTab(_documents[0].Id);
        else
        {
            LayoutAndSyncControls();
            InvalidateChrome();
        }
    }

    private static SessionPane EditPane(SessionPane pane, Func<SessionPane, SessionPane> edit)
    {
        if (pane.First is not null && pane.Second is not null)
        {
            var first = EditPane(pane.First, edit);
            var second = EditPane(pane.Second, edit);
            if (!ReferenceEquals(first, pane.First) || !ReferenceEquals(second, pane.Second))
                pane = pane with { First = first, Second = second };
        }
        return edit(pane);
    }

    private static SessionPane? PrunePane(SessionPane pane, int group)
    {
        if (pane.First is null || pane.Second is null) return pane.Group == group ? null : pane;
        var first = PrunePane(pane.First, group);
        var second = PrunePane(pane.Second, group);
        return first is null ? second : second is null ? first : pane with { First = first, Second = second };
    }

    private void RemoveEmptyGroup(int group)
    {
        if (_documents.Any(doc => doc.Group == group)) return;
        _groupTabs.Remove(group);
        _groupTabScrollX.Remove(group);
        _groupMaxScrollX.Remove(group);
        _groupHeaderBounds.Remove(group);
        _panes = PrunePane(_panes, group) ?? new SessionPane(0);
        if (_documents.Count == 0) _activeGroup = FirstGroup(_panes);
    }

    private static int FirstGroup(SessionPane pane) => pane.First is { } first ? FirstGroup(first) : pane.Group;

    private void MoveTabToGroup(ulong id, int group)
    {
        var doc = _documents.Find(item => item.Id == id);
        if (doc is null || doc.Group == group) return;
        int previous = doc.Group;
        doc.Group = group;
        _groupTabs.Remove(previous);
        RemoveEmptyGroup(previous);
        SelectTab(id);
    }

    private void RestoreGroups(SessionState state)
    {
        if (state.Panes is not { } root) return;
        var groups = new HashSet<int>();
        bool Visit(SessionPane pane, int depth)
        {
            if (depth > 8 || groups.Count >= 8 || !double.IsFinite(pane.Fraction)) return false;
            if (pane.First is { } first && pane.Second is { } second)
                return Visit(first, depth + 1) && Visit(second, depth + 1);
            return pane.First is null && pane.Second is null && pane.Group >= 0 && pane.Group < int.MaxValue && groups.Add(pane.Group);
        }
        if (!Visit(root, 0) || groups.Count == 0) return;
        _panes = root;
        _nextGroup = groups.Max() + 1;
        foreach (var doc in _documents)
            if (!groups.Contains(doc.Group)) doc.Group = groups.First();
        if (state.GroupTabs is null) return;
        foreach (var (group, index) in state.GroupTabs)
            if ((uint)index < (uint)_documents.Count && _documents[index].Group == group)
                _groupTabs[group] = _documents[index].Id;
    }

    private Dictionary<int, int>? CaptureGroupTabs() => !SplitView ? null : _groupTabs
        .ToDictionary(item => item.Key, item => _documents.FindIndex(doc => doc.Id == item.Value));

    private void LayoutGroups(Win32.RECT body)
    {
        _groupBounds.Clear();
        _groupHeaderBounds.Clear();
        _scrollBounds.Clear();
        _dividers.Clear();
        Place(_panes, body);
        void Place(SessionPane pane, Win32.RECT bounds)
        {
            if (pane.First is not { } first || pane.Second is not { } second)
            {
                _groupBounds[pane.Group] = bounds;
                return;
            }
            var a = bounds;
            var b = bounds;
            var divider = bounds;
            double fraction = Math.Clamp(pane.Fraction, .15, .85);
            if (pane.Vertical)
            {
                divider.Left = a.Right = bounds.Left + (int)(bounds.Width * fraction);
                divider.Right = b.Left = Math.Min(bounds.Right, a.Right + S(5));
            }
            else
            {
                divider.Top = a.Bottom = bounds.Top + (int)(bounds.Height * fraction);
                divider.Bottom = b.Top = Math.Min(bounds.Bottom, a.Bottom + S(5));
            }
            _dividers.Add((pane, bounds, divider));
            Place(first, a);
            Place(second, b);
        }

        foreach (var group in _groupBounds.Keys)
            if (!_groupTabs.TryGetValue(group, out ulong current) || !_documents.Any(doc => doc.Group == group && doc.Id == current))
                _groupTabs[group] = _documents.Find(doc => doc.Group == group)?.Id ?? 0;

        foreach (var doc in _documents)
        {
            bool visible = !_showHome && _groupBounds.TryGetValue(doc.Group, out _) && _groupTabs.GetValueOrDefault(doc.Group) == doc.Id;
            bool activeVisible = visible && !doc.CheckingSession && !doc.IdleUnloaded;
            if (activeVisible)
            {
                var area = _groupBounds[doc.Group];
                if (SplitView) area.Top += S(29);
                PositionDocument(doc, area);
            }
            bool showPageEditor = activeVisible && _pageEditors.TryGetValue(doc.Id, out nint pageHwnd) && pageHwnd != 0;
            bool showFilterPreview = activeVisible && !showPageEditor && doc.FilterPreviewHwnd != 0;
            bool showPrimaryEditor = activeVisible && !showPageEditor && !showFilterPreview;
            if (doc.EditorHwnd != 0) Win32.ShowWindow(doc.EditorHwnd, showPrimaryEditor ? Win32.SW_SHOW : Win32.SW_HIDE);
            if (doc.FilterPreviewHwnd != 0) Win32.ShowWindow(doc.FilterPreviewHwnd, showFilterPreview ? Win32.SW_SHOW : Win32.SW_HIDE);
            if (_pageEditors.TryGetValue(doc.Id, out nint peHwnd) && peHwnd != 0) Win32.ShowWindow(peHwnd, showPageEditor ? Win32.SW_SHOW : Win32.SW_HIDE);
        }
    }

    private void PaintGroupHeaders(nint hdc)
    {
        _groupHeaderBounds.Clear();
        _groupMaxScrollX.Clear();
        if (!SplitView || _showHome) return;

        int barH = Math.Max(2, S(2));
        foreach (var (group, bounds) in _groupBounds)
        {
            var header = bounds;
            header.Bottom = Math.Min(header.Bottom, header.Top + S(29));
            _groupHeaderBounds[group] = header;
            if (_groupTabs.GetValueOrDefault(group) == 0)
            {
                var empty = bounds;
                Win32.FillRect(hdc, &empty, _bgBrush);
                _hits.Add(new HitRect(empty, HitKind.EmptyGroup, (ulong)group));
                int buttonY = header.Top + S(2);
                DrawButtonFixed(hdc, header.Left + S(4), buttonY, S(29), S(25), "+", HitKind.NewInGroup, (ulong)group,
                    _busy is not null, _activeGroup == group);
                DrawButtonFixed(hdc, header.Left + S(38), buttonY, S(29), S(25), "×", HitKind.CloseEmptyGroup, (ulong)group,
                    _busy is not null, false);
                continue;
            }

            Win32.FillRect(hdc, &header, _tabBarBrush);
            int padLeft = header.Left + S(4);
            int padRight = Math.Max(padLeft + 1, header.Right - S(4));
            int availWidth = padRight - padLeft;
            int gap = S(3);

            var groupDocs = _documents.Where(doc => doc.Group == group).ToList();
            var widths = new int[groupDocs.Count];
            int totalWidth = 0;
            for (int i = 0; i < groupDocs.Count; i++)
            {
                int w = Math.Min(S(180), MeasureButtonWidth(hdc, TabLabel(groupDocs[i])));
                widths[i] = w;
                totalWidth += w + (i > 0 ? gap : 0);
            }

            int maxScroll = Math.Max(0, totalWidth - availWidth);
            _groupMaxScrollX[group] = maxScroll;
            int scrollX = Math.Clamp(_groupTabScrollX.GetValueOrDefault(group), 0, maxScroll);
            _groupTabScrollX[group] = scrollX;

            int x = padLeft - scrollX;
            int tabH = Math.Max(1, Math.Min(S(24), header.Height - barH - S(2)));
            for (int i = 0; i < groupDocs.Count; i++)
            {
                var doc = groupDocs[i];
                int width = widths[i];
                int shiftedX = x + _tabDragOffsets.GetValueOrDefault(doc.Id);
                if (shiftedX + width > padLeft && shiftedX < padRight)
                {
                    int drawLeft = Math.Max(padLeft, shiftedX);
                    int drawRight = Math.Min(padRight, shiftedX + width);
                    if (drawRight - drawLeft >= S(16))
                    {
                        DrawButtonFixed(hdc, drawLeft, header.Top + S(2), drawRight - drawLeft, tabH,
                            TabLabel(doc), HitKind.GroupTab, doc.Id, false, _groupTabs[group] == doc.Id);
                    }
                }
                x += width + gap;
            }

            if (maxScroll > 0 && header.Height >= barH)
            {
                var track = new Win32.RECT
                {
                    Left = padLeft,
                    Top = header.Bottom - barH,
                    Right = padRight,
                    Bottom = header.Bottom,
                };
                nint trackBrush = Win32.CreateSolidBrush(ThemePalette.Dim);
                Win32.FillRect(hdc, &track, trackBrush);
                Win32.DeleteObject(trackBrush);

                int thumbW = Math.Max(S(24), (int)MathF.Round(availWidth * ((float)availWidth / totalWidth)));
                thumbW = Math.Min(availWidth, thumbW);
                int thumbTravel = Math.Max(0, availWidth - thumbW);
                int thumbLeft = padLeft + (int)MathF.Round(thumbTravel * ((float)scrollX / maxScroll));
                var thumb = new Win32.RECT
                {
                    Left = thumbLeft,
                    Top = track.Top,
                    Right = Math.Min(padRight, thumbLeft + thumbW),
                    Bottom = track.Bottom,
                };
                nint thumbBrush = Win32.CreateSolidBrush(ThemePalette.Muted);
                Win32.FillRect(hdc, &thumb, thumbBrush);
                Win32.DeleteObject(thumbBrush);
            }
        }
        for (int i = 0; i < _dividers.Count; i++)
        {
            var handle = _dividers[i].Handle;
            Win32.FillRect(hdc, &handle, _tabBarBrush);
            _hits.Add(new HitRect(handle, HitKind.GroupDivider, (ulong)i));
        }
    }

    private void NewFileInGroup(int group)
    {
        if (_busy is not null) return;
        _activeGroup = group;
        NewFile();
    }

    private bool HandleGroupHeaderWheel(int clientX, int clientY, int delta, bool horizontal)
    {
        if (!SplitView || _showHome || delta == 0) return false;
        foreach (var (group, header) in _groupHeaderBounds)
        {
            if (clientX < header.Left || clientX >= header.Right || clientY < header.Top || clientY >= header.Bottom)
            {
                continue;
            }

            int maxScroll = _groupMaxScrollX.GetValueOrDefault(group);
            if (maxScroll <= 0) return true;

            int step = horizontal ? delta / 3 : -delta / 3;
            int current = _groupTabScrollX.GetValueOrDefault(group);
            int next = Math.Clamp(current + step, 0, maxScroll);
            if (next != current)
            {
                _groupTabScrollX[group] = next;
                InvalidateChrome();
            }
            return true;
        }
        return false;
    }

    private bool HandleGroupHeaderDoubleClick(int clientX, int clientY)
    {
        if (!SplitView || _showHome) return false;
        var hit = HitTest(clientX, clientY);
        if (hit is { Kind: HitKind.GroupTab, TabId: var tabId })
        {
            if (_justDraggedTabId != tabId)
            {
                CloseTab(tabId);
            }
            _justDraggedTabId = 0;
            return true;
        }

        if (hit is not null) return false;
        foreach (var (group, header) in _groupHeaderBounds)
        {
            if (clientX >= header.Left && clientX < header.Right && clientY >= header.Top && clientY < header.Bottom)
            {
                NewFileInGroup(group);
                return true;
            }
        }
        return false;
    }

    internal bool TryFindGroupDropTarget(int clientX, int clientY, ulong draggedTabId, out int targetGroup, out ulong insertBeforeId)
    {
        targetGroup = _activeGroup;
        insertBeforeId = 0;
        if (!SplitView || _showHome) return false;

        foreach (var (group, header) in _groupHeaderBounds)
        {
            if (clientX < header.Left || clientX >= header.Right || clientY < header.Top || clientY >= header.Bottom)
            {
                continue;
            }

            targetGroup = group;
            for (int i = 0; i < _hits.Count; i++)
            {
                var hit = _hits[i];
                if (hit.Kind == HitKind.GroupTab && hit.TabId != draggedTabId &&
                    hit.Rect.Left >= header.Left && hit.Rect.Right <= header.Right &&
                    hit.Rect.Top >= header.Top && hit.Rect.Bottom <= header.Bottom)
                {
                    int midX = (hit.Rect.Left + hit.Rect.Right) / 2;
                    if (clientX < midX)
                    {
                        insertBeforeId = hit.TabId;
                        return true;
                    }
                }
            }
            return true;
        }

        return false;
    }

    internal bool TryAcceptDroppedTab(Document doc, int targetGroup, ulong insertBeforeTabId = 0)
    {
        if (!_groupBounds.ContainsKey(targetGroup) && targetGroup != 0)
        {
            targetGroup = _activeGroup;
        }

        MoveTabToGroup(doc.Id, targetGroup);
        if (insertBeforeTabId != 0)
        {
            ReorderTabBefore(doc.Id, insertBeforeTabId);
        }
        return true;
    }

    private bool DragDivider(int x, int y)
    {
        if (_dragDivider is not { } drag) return false;
        double fraction = drag.Pane.Vertical
            ? (double)(x - drag.Bounds.Left) / Math.Max(1, drag.Bounds.Width)
            : (double)(y - drag.Bounds.Top) / Math.Max(1, drag.Bounds.Height);
        var pane = drag.Pane with { Fraction = Math.Clamp(fraction, .15, .85) };
        _panes = EditPane(_panes, current => ReferenceEquals(current, drag.Pane) ? pane : current);
        _dragDivider = (pane, drag.Bounds);
        LayoutAndSyncControls();
        InvalidateChrome();
        return true;
    }

}
