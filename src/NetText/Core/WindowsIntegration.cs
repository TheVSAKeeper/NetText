using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace NetText.Core;

internal static unsafe partial class WindowsIntegration
{
    private static readonly (string Extension, string Kind)[] FileTypes =
        [(".txt", "Text"), (".json", "Json"), (".xml", "Xml"), (".log", "Log")];

    internal readonly record struct Window(nint Handle, uint ProcessId, string Title);

    public static void RegisterFileAssociations()
    {
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("Путь к NetText недоступен.");
        string command = $"\"{executable}\" \"%1\"";
        using var currentUser = Registry.CurrentUser;
        using var classes = currentUser.CreateSubKey(@"Software\Classes");
        using var capabilities = currentUser.CreateSubKey(@"Software\NetText\Capabilities");
        using var associations = capabilities.CreateSubKey("FileAssociations");
        capabilities.SetValue("ApplicationName", "NetText");
        capabilities.SetValue("ApplicationDescription", "Редактор текста и просмотрщик логов");
        capabilities.SetValue("ApplicationIcon", $"\"{executable}\",0");
        foreach (var (extension, kind) in FileTypes)
        {
            string id = "NetText." + kind;
            using var type = classes.CreateSubKey(id);
            type.SetValue("", "NetText " + kind);
            using var open = type.CreateSubKey(@"shell\open\command");
            open.SetValue("", command);
            using var openWith = classes.CreateSubKey(extension + @"\OpenWithProgids");
            openWith.SetValue(id, Array.Empty<byte>(), RegistryValueKind.None);
            using var context = classes.CreateSubKey($@"SystemFileAssociations\{extension}\shell\NetText");
            context.SetValue("", "Открыть в NetText");
            context.SetValue("Icon", $"\"{executable}\",0");
            using var contextCommand = context.CreateSubKey("command");
            contextCommand.SetValue("", command);
            associations.SetValue(extension, id);
        }
        using var apps = currentUser.CreateSubKey(@"Software\RegisteredApplications");
        apps.SetValue("NetText", @"Software\NetText\Capabilities");
        SHChangeNotify(0x08000000, 0, 0, 0);
    }

    public static void OpenDefaultApps() =>
        Process.Start(new ProcessStartInfo("ms-settings:defaultapps") { UseShellExecute = true });

    public static void RevealFile(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Файл не найден.", path);
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });
    }

    public static IReadOnlyList<Window> FindWindows(string query, bool byPid)
    {
        query = query.Trim();
        if (query.Length == 0) throw new ArgumentException("Введите PID или часть заголовка.");
        uint pid = 0;
        if (byPid && (!uint.TryParse(query, NumberStyles.None, CultureInfo.InvariantCulture, out pid) || pid == 0))
            throw new ArgumentException("Введите PID процесса целым положительным числом.");

        var result = new List<Window>();
        var handle = GCHandle.Alloc(result);
        try { EnumWindows(&CollectWindow, GCHandle.ToIntPtr(handle)); }
        finally { handle.Free(); }
        return result.Where(window => byPid ? window.ProcessId == pid :
            window.Title.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    public static bool BringToFront(Window window)
    {
        if (GetWindowThreadProcessId(window.Handle, out uint pid) == 0 || pid != window.ProcessId || !IsWindowVisible(window.Handle))
            return false;
        if (IsIconic(window.Handle)) ShowWindowAsync(window.Handle, 9);
        return GetForegroundWindow() == window.Handle || Native.Win32.SetForegroundWindow(window.Handle);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int CollectWindow(nint window, nint parameter)
    {
        if (!IsWindowVisible(window)) return 1;
        GetWindowThreadProcessId(window, out uint pid);
        if (pid == 0) return 1;
        char* title = stackalloc char[1024];
        int length = Native.Win32.GetWindowTextW(window, title, 1024);
        if (GCHandle.FromIntPtr(parameter).Target is List<Window> result)
            result.Add(new Window(window, pid, length > 0 ? new string(title, 0, length) : ""));
        return 1;
    }

    [LibraryImport("shell32.dll")]
    private static partial void SHChangeNotify(int eventId, uint flags, nint first, nint second);

    [LibraryImport("user32.dll")]
    private static partial int EnumWindows(delegate* unmanaged[Stdcall]<nint, nint, int> callback, nint parameter);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(nint hwnd);

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindowAsync(nint hwnd, int command);
}
