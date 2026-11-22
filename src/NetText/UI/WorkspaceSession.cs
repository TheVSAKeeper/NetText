using System.IO;
using System.Runtime.InteropServices;
using NetText.Core;
using NetText.Native;

namespace NetText.UI;

internal sealed record OpenFileView(
    MainWindow Window,
    ulong DocumentId,
    nint EditorHwnd,
    TextFormat Format,
    DiskStatusKind DiskStatus,
    bool Dirty,
    bool Placeholder,
    FileTarget? File);

internal sealed unsafe partial class WorkspaceSession
{
    private const uint GA_ROOT = 2;
    private const uint GW_HWNDNEXT = 2;

    [LibraryImport("user32.dll")]
    private static partial nint GetAncestor(nint hwnd, uint gaFlags);

    [LibraryImport("user32.dll")]
    private static partial nint GetParent(nint hwnd);

    [LibraryImport("user32.dll")]
    private static partial nint GetTopWindow(nint hWnd);

    [LibraryImport("user32.dll")]
    private static partial nint GetWindow(nint hWnd, uint uCmd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindowVisible(nint hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsIconic(nint hWnd);

    public static WorkspaceSession Current { get; } = new();

    public WorkspaceCatalogService Catalog { get; } = WorkspaceCatalogService.OpenDefault();
    public WorkspaceCatalog? CachedCatalog { get; set; }
    public List<MainWindow> Windows { get; } = [];
    public MainWindow? LastActiveWindow { get; set; }
    public Dictionary<(string WorkspaceId, string WindowId), SessionJournal> Journals { get; } = [];
    public Dictionary<string, List<(SessionTab Tab, byte[]? DraftBytes)>> DetachedDrafts { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<(string WorkspaceId, string WindowId)> SharedSessionAliases { get; } = [];
    public SingleInstance? SingleInstance { get; set; }

    internal Dictionary<string, string> DetachedPrimaryDirectories { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void RegisterWindow(MainWindow window)
    {
        if (!Windows.Contains(window))
        {
            Windows.Add(window);
        }

        LastActiveWindow ??= window;
    }

    public bool UnregisterWindow(MainWindow window)
    {
        Windows.Remove(window);
        if (ReferenceEquals(LastActiveWindow, window))
        {
            LastActiveWindow = Windows.LastOrDefault(candidate => !candidate._closingWindow && IsWindowVisible(candidate.Hwnd))
                ?? Windows.LastOrDefault(candidate => !candidate._closingWindow);
        }

        return Windows.Count == 0;
    }

    public MainWindow? FindWindow(nint hwnd)
    {
        if (hwnd == 0) return null;

        nint root = GetAncestor(hwnd, GA_ROOT);
        for (int i = 0; i < Windows.Count; i++)
        {
            var candidate = Windows[i];
            if (candidate.Hwnd == hwnd || (root != 0 && candidate.Hwnd == root))
            {
                return candidate;
            }
        }

        nint current = hwnd;
        while (current != 0)
        {
            for (int i = 0; i < Windows.Count; i++)
            {
                if (Windows[i].Hwnd == current)
                {
                    return Windows[i];
                }
            }
            current = GetParent(current);
        }

        return null;
    }

    public bool TryHandleKeyMessage(Win32.MSG* msg)
    {
        if (msg is null) return false;
        int vk = (int)msg->wParam;

        if (vk == Win32.VK_ESCAPE)
        {
            for (int i = 0; i < Windows.Count; i++)
            {
                if (Windows[i].CancelActiveTabDrag())
                {
                    return true;
                }
            }
        }

        var owner = FindWindow(msg->hwnd);
        return owner is not null && owner.TryHandleKeyMessage(msg->hwnd, vk);
    }

    public void RouteIpcPaths(string[] paths)
    {
        var target = ResolveIpcTargetWindow();
        if (target is null) return;

        if (IsIconic(target.Hwnd))
        {
            Win32.ShowWindow(target.Hwnd, 9);
        }
        else
        {
            Win32.ShowWindow(target.Hwnd, Win32.SW_SHOW);
        }

        Win32.SetForegroundWindow(target.Hwnd);
        if (paths.Length > 0)
        {
            target.OpenPathsFromIpc(paths);
        }
    }

    public void RouteIncomingIpc(SingleInstance? singleInstance)
    {
        if (singleInstance is null) return;
        while (singleInstance.TryReceive(out var request) && request is not null)
        {
            var target = ResolveIpcTargetWindow();
            if (target is null) return;

            if (request.Activate)
            {
                if (IsIconic(target.Hwnd))
                {
                    Win32.ShowWindow(target.Hwnd, 9);
                }
                else
                {
                    Win32.ShowWindow(target.Hwnd, Win32.SW_SHOW);
                }
                Win32.SetForegroundWindow(target.Hwnd);
            }

            var abs = request.ResolveAbsolutePaths();
            if (abs.Length > 0)
            {
                target.OpenPathsFromIpc(abs);
                break;
            }
        }
    }

    public MainWindow? FindOtherSessionWindow(MainWindow window)
    {
        string targetDir = NormalizeDirectory(GetSessionDirectory(window));
        for (int i = Windows.Count - 1; i >= 0; i--)
        {
            var candidate = Windows[i];
            if (ReferenceEquals(candidate, window) || candidate._closingWindow) continue;
            if (!IsWindowVisible(candidate.Hwnd)) continue;
            if (string.Equals(NormalizeDirectory(GetSessionDirectory(candidate)), targetDir, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return null;
    }

    public bool IsWorkspaceOpen(string workspaceId) =>
        FindWindowForWorkspace(workspaceId) is not null;

    public MainWindow? FindWindowForWorkspace(string workspaceId)
    {
        for (int i = Windows.Count - 1; i >= 0; i--)
        {
            var candidate = Windows[i];
            if (!candidate._closingWindow && !candidate._startEmpty &&
                string.Equals(candidate._workspaceId, workspaceId, StringComparison.Ordinal))
            {
                return candidate;
            }
        }

        return null;
    }

    public List<OpenFileView> FindOpenViewsForFile(string fullPath, TextEncoding encoding, MainWindow? excludeWindow = null)
    {
        var result = new List<OpenFileView>();
        if (string.IsNullOrWhiteSpace(fullPath)) return result;

        string normalized = Path.GetFullPath(fullPath);
        for (int i = 0; i < Windows.Count; i++)
        {
            var window = Windows[i];
            if (ReferenceEquals(window, excludeWindow) || window._closingWindow) continue;
            window.CollectOpenFileViews(normalized, encoding, result);
        }

        return result;
    }

    public WorkspaceCatalog EnsureCatalog()
    {
        if (CachedCatalog is { } cached) return cached;
        var loaded = Catalog.LoadAsync().GetAwaiter().GetResult();
        CachedCatalog = loaded;
        return loaded;
    }

    public void PublishCatalog(WorkspaceCatalog catalog)
    {
        CachedCatalog = catalog;
        for (int i = 0; i < Windows.Count; i++)
        {
            Windows[i].RefreshWorkspaceChrome();
        }
    }

    public SessionJournal GetOrOpenJournal(string workspaceId, string windowId, bool shareDefaultSession = false)
    {
        string effectiveWorkspace = workspaceId;
        string effectiveWindow = windowId;
        if (workspaceId == WorkspaceCatalogService.DefaultWorkspaceId &&
            (windowId == WorkspaceCatalogService.PrimaryWindowId || shareDefaultSession))
        {
            effectiveWindow = WorkspaceCatalogService.PrimaryWindowId;
        }

        var key = (effectiveWorkspace, effectiveWindow);
        if (Journals.TryGetValue(key, out var existing))
        {
            return existing;
        }

        string dir = NormalizeDirectory(Catalog.GetSessionDirectory(effectiveWorkspace, effectiveWindow));
        foreach (var open in Journals.Values)
        {
            if (string.Equals(NormalizeDirectory(open.DirectoryPath), dir, StringComparison.OrdinalIgnoreCase))
            {
                Journals[key] = open;
                return open;
            }
        }

        var opened = SessionJournal.Open(dir);
        Journals[key] = opened;
        return opened;
    }

    public void ShareSession(string workspaceId, string windowId, SessionJournal journal)
    {
        var key = (workspaceId, windowId);
        Journals[key] = journal;
        SharedSessionAliases.Add(key);
    }

    public void ReleaseSharedSession(string workspaceId, string windowId, SessionJournal journal)
    {
        var key = (workspaceId, windowId);
        if (Journals.TryGetValue(key, out var existing) && ReferenceEquals(existing, journal))
        {
            Journals.Remove(key);
            SharedSessionAliases.Remove(key);
        }
    }

    public void ReleaseJournalIfUnused(SessionJournal? journal)
    {
        if (journal is null) return;
        for (int i = 0; i < Windows.Count; i++)
        {
            if (Windows[i].UsesJournal(journal))
            {
                return;
            }
        }

        var keysToRemove = new List<(string WorkspaceId, string WindowId)>();
        foreach (var (key, value) in Journals)
        {
            if (ReferenceEquals(value, journal))
            {
                keysToRemove.Add(key);
            }
        }

        foreach (var key in keysToRemove)
        {
            Journals.Remove(key);
            SharedSessionAliases.Remove(key);
        }

        journal.Dispose();
    }

    public MainWindow? HitTestWindowAtScreenPoint(Win32.POINT screenPoint, MainWindow? preferred = null)
    {
        for (nint hwnd = GetTopWindow(0); hwnd != 0; hwnd = GetWindow(hwnd, GW_HWNDNEXT))
        {
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) continue;
            long exStyle = Win32.GetWindowLongPtrW(hwnd, -20);
            if ((exStyle & Win32.WS_EX_TRANSPARENT) != 0) continue;

            Win32.RECT rect;
            if (!Win32.GetWindowRect(hwnd, &rect)) continue;
            if (screenPoint.X < rect.Left || screenPoint.X >= rect.Right ||
                screenPoint.Y < rect.Top || screenPoint.Y >= rect.Bottom)
            {
                continue;
            }

            for (int i = 0; i < Windows.Count; i++)
            {
                var candidate = Windows[i];
                if (candidate.Hwnd == hwnd)
                {
                    return candidate._closingWindow ? null : candidate;
                }
            }

            return null;
        }

        if (preferred is not null && IsEligibleDropTarget(preferred, screenPoint))
        {
            return preferred;
        }

        return null;
    }

    private static bool IsEligibleDropTarget(MainWindow window, Win32.POINT screenPoint)
    {
        if (window.Hwnd == 0 || window._closingWindow || !IsWindowVisible(window.Hwnd) || IsIconic(window.Hwnd))
        {
            return false;
        }

        Win32.RECT rect;
        if (!Win32.GetWindowRect(window.Hwnd, &rect))
        {
            return false;
        }

        return screenPoint.X >= rect.Left && screenPoint.X < rect.Right &&
               screenPoint.Y >= rect.Top && screenPoint.Y < rect.Bottom;
    }

    private MainWindow? ResolveIpcTargetWindow()
    {
        if (LastActiveWindow is { } active && !active._closingWindow && Windows.Contains(active) && IsWindowVisible(active.Hwnd))
        {
            return active;
        }

        for (int i = Windows.Count - 1; i >= 0; i--)
        {
            var candidate = Windows[i];
            if (!candidate._closingWindow && IsWindowVisible(candidate.Hwnd))
            {
                return candidate;
            }
        }

        for (int i = 0; i < Windows.Count; i++)
        {
            if (!Windows[i]._closingWindow)
            {
                return Windows[i];
            }
        }

        return null;
    }

    private string GetSessionDirectory(MainWindow window)
    {
        if (window._startEmpty)
        {
            return Catalog.GetSessionDirectory(WorkspaceCatalogService.DefaultWorkspaceId, WorkspaceCatalogService.EmptyStartupWindowId);
        }

        return window.CurrentJournalDirectory ?? Catalog.GetSessionDirectory(window._workspaceId, window._windowId);
    }

    internal static string NormalizeDirectory(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
