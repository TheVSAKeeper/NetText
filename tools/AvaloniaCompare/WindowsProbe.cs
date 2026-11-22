using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

public static class ComparisonWindowsProbe
{
    private delegate bool WindowCallback(IntPtr hwnd, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(WindowCallback callback, IntPtr data);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint process);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint command);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder title, int size);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wparam, IntPtr lparam,
        uint flags, uint timeout, out IntPtr result);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wparam, IntPtr lparam);

    public static IntPtr VisibleWindow(int pid)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out uint process);
            if (process != (uint)pid || GetWindow(window, 4) != IntPtr.Zero || !IsWindowVisible(window)) return true;
            if (!GetWindowRect(window, out var rect) || rect.Right - rect.Left < 200 || rect.Bottom - rect.Top < 150) return true;
            if (Title(window).Length == 0) return true;
            found = window;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    public static string Title(IntPtr hwnd)
    {
        var title = new StringBuilder(512);
        GetWindowText(hwnd, title, title.Capacity);
        return title.ToString();
    }

    public static bool Responds(IntPtr hwnd) =>
        SendMessageTimeout(hwnd, 0, IntPtr.Zero, IntPtr.Zero, 2, 25, out _) != IntPtr.Zero;

    public static void Close(IntPtr hwnd) => PostMessage(hwnd, 0x0010, IntPtr.Zero, IntPtr.Zero);
}

public sealed class ComparisonStreamCapture
{
    private readonly object _gate = new object();
    private readonly StringBuilder _buffer = new StringBuilder();
    private readonly Task _readerTask;
    private readonly Regex _readyRegex;
    private readonly Stopwatch _clock;
    private double? _readyMs;

    public ComparisonStreamCapture(StreamReader reader, Stopwatch clock, string readyPattern)
    {
        _clock = clock;
        _readyRegex = string.IsNullOrEmpty(readyPattern)
            ? null
            : new Regex(readyPattern, RegexOptions.CultureInvariant);
        _readerTask = Task.Run(() =>
        {
            try
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    lock (_gate)
                    {
                        if (_buffer.Length > 0) _buffer.Append('\n');
                        _buffer.Append(line);
                        if (!_readyMs.HasValue && _readyRegex != null && _readyRegex.IsMatch(line))
                        {
                            _readyMs = _clock.Elapsed.TotalMilliseconds;
                        }
                    }
                }
            }
            catch (ObjectDisposedException) { }
            catch (IOException) { }
        });
    }

    public double? ReadyMs
    {
        get { lock (_gate) return _readyMs; }
    }

    public bool WaitForCompletion(int timeoutMs) => _readerTask.Wait(timeoutMs);

    public string GetText()
    {
        lock (_gate) return _buffer.ToString();
    }
}
