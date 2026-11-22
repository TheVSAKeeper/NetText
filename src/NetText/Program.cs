using NetText.Core;
using NetText.Native;
using NetText.UI;

namespace NetText;

internal static unsafe class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (ConsoleCapture.TryRunWorker(args) is { } workerExit)
        {
            Environment.ExitCode = workerExit;
            return;
        }

        if (args is ["--register-file-associations"])
        {
            try { WindowsIntegration.RegisterFileAssociations(); }
            catch { Environment.ExitCode = 1; }
            return;
        }

        if (args is ["--install-update", var installation, var pid, var started])
        {
            Environment.ExitCode = Updates.Install(installation, pid, started);
            return;
        }
        string? updateStage = null;
        string? updateFailure = null;
        if (args is ["--update-ready", var stage])
        {
            updateStage = stage;
            args = [];
        }
        else if (args is ["--update-failed", var failedStage])
        {
            updateFailure = Updates.ReadFailure(failedStage);
            args = [];
        }

        Measurements.EnableFromEnv();
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 15063)) Win32.SetProcessDpiAwarenessContext(-4);

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
        WorkspaceSession.Current.SingleInstance = singleInstance;
        var window = new MainWindow(args, singleInstance);
        if (updateStage is not null) Updates.Acknowledge(updateStage);
        if (updateFailure is not null) window.ReportUpdateFailure(updateFailure);

        Win32.MSG msg;
        while (Win32.GetMessageW(&msg, 0, 0, 0) > 0)
        {
            if (msg.message is Win32.WM_KEYDOWN or Win32.WM_SYSKEYDOWN)
            {
                if (WorkspaceSession.Current.TryHandleKeyMessage(&msg))
                {
                    continue;
                }
            }

            Win32.TranslateMessage(&msg);
            Win32.DispatchMessageW(&msg);
        }
    }
}
