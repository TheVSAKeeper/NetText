using NetText.Core;
using NetText.Native;
using NetText.UI;

namespace NetText;

internal static unsafe class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Measurements.EnableFromEnv();
        Win32.SetProcessDpiAwarenessContext(-4);

        var launch = LaunchRequest.FromProcess(args);
        SingleInstance? singleInstance = null;
        try
        {
            var (role, instance) = SingleInstance.Acquire(launch);
            if (role == InstanceRoleKind.Forwarded)
            {
                return;
            }
            singleInstance = instance;
        }
        catch
        {
        }

        using var ownedInstance = singleInstance;
        var window = new MainWindow(args, singleInstance);

        Win32.MSG msg;
        while (Win32.GetMessageW(&msg, 0, 0, 0) > 0)
        {
            if (msg.message is Win32.WM_KEYDOWN or Win32.WM_SYSKEYDOWN)
            {
                if (window.TryHandleKeyMessage(msg.hwnd, (int)msg.wParam))
                {
                    continue;
                }
            }

            Win32.TranslateMessage(&msg);
            Win32.DispatchMessageW(&msg);
        }
    }
}
