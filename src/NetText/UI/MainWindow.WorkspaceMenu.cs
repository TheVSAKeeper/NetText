using NetText.Core;
using NetText.Native;

namespace NetText.UI;

internal sealed unsafe partial class MainWindow
{
    private const string DefaultWorkspaceSymbol = "›";
    private const string EmptyWorkspaceSymbol = "○";
    private static readonly string[] WorkspaceSymbols = ["◆", "●", "▲", "★", "✦", "⚙", "▣"];

    private WorkspaceEntry? CurrentWorkspaceEntry()
    {
        if (_startEmpty) return null;
        var catalog = WorkspaceSession.Current.CachedCatalog ?? WorkspaceSession.Current.EnsureCatalog();
        return catalog.Workspaces.FirstOrDefault(workspace => workspace.Id == _workspaceId);
    }

    private void PaintWorkspaceBadge(nint hdc, Win32.RECT rect)
    {
        int size = S(27);
        int left = rect.Left + Math.Max(0, (rect.Width - size) / 2);
        int top = rect.Top + Math.Max(0, (rect.Height - size) / 2);
        var badge = new Win32.RECT
        {
            Left = left,
            Top = top,
            Right = left + size,
            Bottom = top + size,
        };

        var workspace = CurrentWorkspaceEntry();
        string symbol = _startEmpty ? EmptyWorkspaceSymbol : workspace?.Symbol ?? DefaultWorkspaceSymbol;
        uint borderColor = TryParseHexColorToBgr(workspace?.Color, out uint customColor)
            ? customColor
            : ThemePalette.AppIconBorder;
        bool hovered = _busy is null && _hoverKind == HitKind.WorkspaceBadge;
        uint fillColor = hovered ? ThemePalette.HoverBg : ThemePalette.AppIconBackground;

        int penWidth = Math.Max(1, (int)MathF.Round(1.5f * Scale));
        nint brush = Win32.CreateSolidBrush(fillColor);
        nint pen = Win32.CreatePen(Win32.PS_SOLID, penWidth, borderColor);
        nint oldBrush = Win32.SelectObject(hdc, brush);
        nint oldPen = Win32.SelectObject(hdc, pen);
        Win32.RoundRect(hdc, badge.Left, badge.Top, badge.Right, badge.Bottom, S(6), S(6));
        Win32.SelectObject(hdc, oldBrush);
        Win32.SelectObject(hdc, oldPen);
        Win32.DeleteObject(brush);
        Win32.DeleteObject(pen);

        nint oldFont = Win32.SelectObject(hdc, _uiFont);
        Win32.SetTextColor(hdc, workspace?.Symbol is null && !_startEmpty ? ThemePalette.AppIconForeground : ThemePalette.Text);
        Win32.DrawTextW(hdc, symbol, -1, &badge, Win32.DT_CENTER | Win32.DT_VCENTER | Win32.DT_SINGLELINE | Win32.DT_NOPREFIX);
        Win32.SelectObject(hdc, oldFont);

        _hits.Add(new HitRect(badge, HitKind.WorkspaceBadge, 0));
    }

    private void ShowWorkspaceMenu(Win32.RECT anchor)
    {
        if (_busy is not null) return;
        var catalog = WorkspaceSession.Current.EnsureCatalog();
        var items = new List<PopupMenuItem>(catalog.Workspaces.Count + 5);

        foreach (var workspace in catalog.Workspaces)
        {
            string id = workspace.Id;
            bool current = !_startEmpty && id == _workspaceId;
            string glyph = workspace.Symbol ?? DefaultWorkspaceSymbol;
            string label = current ? $"{glyph}  {workspace.Name} · открыто" : $"{glyph}  {workspace.Name}";
            items.Add(PopupMenuItem.Cmd(label, () => SwitchToWorkspace(id), enabled: !current, @checked: current));
        }

        if (_windowId == WorkspaceCatalogService.PrimaryWindowId)
        {
            string emptyLabel = _startEmpty ? $"{EmptyWorkspaceSymbol}  Пустое окно · открыто" : $"{EmptyWorkspaceSymbol}  Пустое окно";
            items.Add(PopupMenuItem.Cmd(emptyLabel, SwitchToEmptyWindow, enabled: !_startEmpty, @checked: _startEmpty));
        }

        items.Add(PopupMenuItem.Sep());
        items.Add(PopupMenuItem.Cmd("Новая рабочая область…", ShowCreateWorkspaceDialog));
        items.Add(PopupMenuItem.Cmd("Управление…", ShowManageWorkspacesDialog));

        ShowNestedMenu(anchor, [.. items]);
    }
}
