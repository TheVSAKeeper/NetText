using System.IO;
using System.Runtime.InteropServices;
using NetText.Native;

namespace NetText.Core;

internal static unsafe partial class DesktopIcon
{
    private static readonly Guid ShellWindows = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    private static readonly Guid IShellWindows = new("85CB6900-4D95-11CF-960C-0080C7F4EE85");
    private static readonly Guid IServiceProvider = new("6D5140C1-7436-11CE-8034-00AA006009FA");
    private static readonly Guid TopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    private static readonly Guid IShellBrowser = new("000214E2-0000-0000-C000-000000000046");
    private static readonly Guid IFolderView2 = new("1AF3A467-214F-4298-908E-06B03E0B39F9");
    private static readonly Guid IShellFolder = new("000214E6-0000-0000-C000-000000000046");
    private static readonly Guid IEnumIdList = new("000214F2-0000-0000-C000-000000000046");
    private static readonly Guid IShellItem = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");
    private const uint AutoArrange = 1;
    private const uint PositionWithoutFocus = 0x40000080;
    private static int _worker;

    public static Win32.POINT? Capture(FileTarget target)
    {
        if (target.Version is null) return null;
        return Run<Win32.POINT>(250, deadline =>
        {
            if (!IsDesktop(target.Path, deadline)) return null;
            using var view = DesktopView();
            if (!IsManual(view.Value)) return null;
            using var item = FindItem(view.Value, target.Path, deadline);
            return item.Value == 0 ? null : GetPosition(view.Value, item.Value);
        });
    }

    public static void Restore(FileTarget saved, Win32.POINT position)
    {
        if (saved.Version is not { } expected) return;
        _ = Run<bool>(3000, deadline =>
        {
            using var view = DesktopView();
            while (Environment.TickCount64 < deadline && IsManual(view.Value) && FileVersion.At(saved.Path) == expected)
            {
                using var item = FindItem(view.Value, saved.Path, deadline);
                if (item.Value != 0 && item.Size == expected.Bytes && item.Modified == expected.ModifiedUtc.ToFileTimeUtc())
                {
                    if (GetPosition(view.Value, item.Value) is not { } current) return null;
                    if (current.X == position.X && current.Y == position.Y) return true;
                    if (Environment.TickCount64 >= deadline || !IsManual(view.Value) || FileVersion.At(saved.Path) != expected) return null;
                    var selection = (delegate* unmanaged[Stdcall]<nint, nint, uint*, int>)Slot(view.Value, 33);
                    uint flags;
                    if (selection(view.Value, item.Value, &flags) < 0) return null;
                    var move = (delegate* unmanaged[Stdcall]<nint, uint, nint*, Win32.POINT*, uint, int>)Slot(view.Value, 16);
                    nint pidl = item.Value;
                    Win32.POINT desired = position;
                    return move(view.Value, 1, &pidl, &desired, PositionWithoutFocus | (flags & 1)) >= 0;
                }
                Thread.Sleep(125);
            }
            return null;
        });
    }

    private static T? Run<T>(int milliseconds, Func<long, T?> action) where T : struct
    {
        if (Interlocked.Exchange(ref _worker, 1) != 0) return null;
        long deadline = Environment.TickCount64 + milliseconds;
        var completion = new TaskCompletionSource<T?>();
        var thread = new Thread(() =>
        {
            T? result = null;
            try
            {
                if (CoInitializeEx(0, 2) >= 0)
                {
                    try { result = action(deadline); }
                    finally { CoUninitialize(); }
                }
            }
            catch (Exception) { }
            finally
            {
                Interlocked.Exchange(ref _worker, 0);
                completion.TrySetResult(result);
            }
        }) { IsBackground = true };
        try
        {
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }
        catch (Exception)
        {
            Interlocked.Exchange(ref _worker, 0);
            return null;
        }
        return completion.Task.Wait(milliseconds) ? completion.Task.Result : null;
    }

    private static bool IsDesktop(string path, long deadline)
    {
        string parent = Path.GetDirectoryName(path) ?? "";
        foreach (var folder in new[] { Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.CommonDesktopDirectory })
        {
            string configured = Environment.GetFolderPath(folder, Environment.SpecialFolderOption.DoNotVerify);
            if (configured.Length == 0) continue;
            if (SamePath(configured, parent)) return true;
            if (Environment.TickCount64 >= deadline || configured.StartsWith(@"\\", StringComparison.Ordinal)) continue;
            var target = Directory.ResolveLinkTarget(configured, returnFinalTarget: true);
            if (target is not null && SamePath(target.FullName, parent)) return true;
        }
        return false;
    }

    private static bool SamePath(string first, string second)
    {
        static string Normalize(string path)
        {
            if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + path[8..];
            return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
        }
        return string.Equals(Normalize(first), Normalize(second), StringComparison.OrdinalIgnoreCase);
    }

    private static ComObject DesktopView()
    {
        if (CoCreateInstance(ShellWindows, 0, 4, IShellWindows, out nint shellWindows) < 0) return default;
        using var windows = new ComObject(shellWindows);
        var find = (delegate* unmanaged[Stdcall]<nint, Variant*, Variant*, int, int*, int, nint*, int>)Slot(windows.Value, 15);
        Variant desktop = new() { Type = 3 };
        Variant root = default;
        int hwnd;
        nint dispatch;
        if (find(windows.Value, &desktop, &root, 8, &hwnd, 1, &dispatch) != 0 || dispatch == 0) return default;
        using var window = new ComObject(dispatch);
        using var provider = Query(window.Value, IServiceProvider);
        if (provider.Value == 0) return default;
        var query = (delegate* unmanaged[Stdcall]<nint, Guid*, Guid*, nint*, int>)Slot(provider.Value, 3);
        Guid service = TopLevelBrowser;
        Guid browserType = IShellBrowser;
        nint shellBrowser;
        if (query(provider.Value, &service, &browserType, &shellBrowser) < 0) return default;
        using var browser = new ComObject(shellBrowser);
        var active = (delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(browser.Value, 15);
        nint shellView;
        if (active(browser.Value, &shellView) < 0) return default;
        using var view = new ComObject(shellView);
        return Query(view.Value, IFolderView2);
    }

    private static bool IsManual(nint view)
    {
        if (view == 0) return false;
        var flags = (delegate* unmanaged[Stdcall]<nint, uint*, int>)Slot(view, 25);
        uint mode;
        return flags(view, &mode) >= 0 && (mode & AutoArrange) == 0;
    }

    private static Win32.POINT? GetPosition(nint view, nint item)
    {
        var get = (delegate* unmanaged[Stdcall]<nint, nint, Win32.POINT*, int>)Slot(view, 11);
        Win32.POINT location;
        return get(view, item, &location) < 0 ? null : location;
    }

    private static Pidl FindItem(nint view, string path, long deadline)
    {
        var folderMethod = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(view, 5);
        var itemsMethod = (delegate* unmanaged[Stdcall]<nint, uint, Guid*, nint*, int>)Slot(view, 8);
        Guid folderType = IShellFolder;
        nint shellFolder;
        if (folderMethod(view, &folderType, &shellFolder) < 0) return default;
        using var folder = new ComObject(shellFolder);
        Guid itemsType = IEnumIdList;
        nint shellItems;
        if (itemsMethod(view, 2, &itemsType, &shellItems) < 0) return default;
        using var items = new ComObject(shellItems);
        var next = (delegate* unmanaged[Stdcall]<nint, uint, nint*, uint*, int>)Slot(items.Value, 3);
        string name = Path.GetFileName(path);
        while (Environment.TickCount64 < deadline)
        {
            nint pidl;
            if (next(items.Value, 1, &pidl, null) != 0 || pidl == 0) break;
            bool matched = false;
            try
            {
                FindData data;
                if (SHGetDataFromIDListW(folder.Value, pidl, 1, &data, sizeof(FindData)) < 0 ||
                    !name.Equals(new string(data.Name), StringComparison.OrdinalIgnoreCase)) continue;
                if (SHCreateItemWithParent(0, folder.Value, pidl, IShellItem, out nint shellItem) < 0) continue;
                using var item = new ComObject(shellItem);
                var display = (delegate* unmanaged[Stdcall]<nint, uint, char**, int>)Slot(item.Value, 5);
                char* fullPath;
                if (display(item.Value, 0x80058000, &fullPath) < 0 || fullPath is null) continue;
                try { matched = SamePath(new string(fullPath), path); }
                finally { CoTaskMemFree((nint)fullPath); }
                if (matched) return new Pidl(pidl, ((long)data.SizeHigh << 32) | data.SizeLow,
                    ((long)data.ModifiedHigh << 32) | data.ModifiedLow);
            }
            finally { if (!matched) CoTaskMemFree(pidl); }
        }
        return default;
    }

    private static nint Slot(nint pointer, int index) => (*(nint**)pointer)[index];

    private static ComObject Query(nint pointer, Guid type)
    {
        var query = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(pointer, 0);
        nint result;
        return query(pointer, &type, &result) >= 0 ? new ComObject(result) : default;
    }

    private readonly record struct Pidl(nint Value, long Size, long Modified) : IDisposable
    {
        public void Dispose() { if (Value != 0) CoTaskMemFree(Value); }
    }

    private readonly record struct ComObject(nint Value) : IDisposable
    {
        public void Dispose()
        {
            if (Value != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(Value, 2))(Value);
        }
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct Variant
    {
        [FieldOffset(0)] public ushort Type;
    }

    [StructLayout(LayoutKind.Explicit, Size = 592)]
    private struct FindData
    {
        [FieldOffset(20)] public uint ModifiedLow;
        [FieldOffset(24)] public uint ModifiedHigh;
        [FieldOffset(28)] public uint SizeHigh;
        [FieldOffset(32)] public uint SizeLow;
        [FieldOffset(44)] public fixed char Name[260];
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(nint reserved, uint model);

    [LibraryImport("ole32.dll")]
    private static partial void CoUninitialize();

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid classId, nint outer, uint context, in Guid interfaceId, out nint result);

    [LibraryImport("ole32.dll")]
    private static partial void CoTaskMemFree(nint address);

    [LibraryImport("shell32.dll")]
    private static partial int SHGetDataFromIDListW(nint folder, nint item, int format, FindData* data, int size);

    [LibraryImport("shell32.dll")]
    private static partial int SHCreateItemWithParent(nint parent, nint folder, nint child, in Guid type, out nint result);
}
