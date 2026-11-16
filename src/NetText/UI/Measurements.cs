using System.Diagnostics;
using System.Globalization;

namespace NetText.UI;

internal static class Measurements
{
    private static bool _enabled;
    private static bool _e2eEnabled;
    private static string? _e2eNeedle;
    private static long _startTicks;
    private static int _windowVisibleLogged;
    private static int _firstFrameLogged;
    private static int _firstLogFrameLogged;
    private static int _editorReadyLogged;
    private static int _indexReadyLogged;

    public static void EnableFromEnv()
    {
        bool measure = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NETTEXT_MEASURE")) ||
                       !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("RUSTTEXT_MEASURE"));
        string? e2e = Environment.GetEnvironmentVariable("NETTEXT_E2E_PROBE");
        if (string.IsNullOrEmpty(e2e)) e2e = Environment.GetEnvironmentVariable("RUSTTEXT_E2E_PROBE");
        _e2eEnabled = !string.IsNullOrEmpty(e2e);
        _e2eNeedle = Environment.GetEnvironmentVariable("NETTEXT_E2E_NEEDLE");
        if (string.IsNullOrEmpty(_e2eNeedle)) _e2eNeedle = Environment.GetEnvironmentVariable("RUSTTEXT_E2E_NEEDLE");

        if (measure || _e2eEnabled)
        {
            _enabled = true;
            _startTicks = Stopwatch.GetTimestamp();
        }
    }

    public static bool IsEnabled => _enabled;
    public static bool IsE2EEnabled => _e2eEnabled;
    public static string? E2ENeedle => string.IsNullOrEmpty(_e2eNeedle) ? null : _e2eNeedle;
    public static long StartTicks => _startTicks;

    public static void RecordWindowVisible()
    {
        if (_enabled && Interlocked.Exchange(ref _windowVisibleLogged, 1) == 0)
        {
            long ms = (long)Stopwatch.GetElapsedTime(_startTicks).TotalMilliseconds;
            Console.WriteLine($"NETTEXT window_visible_ms={ms}");
            Console.Out.Flush();
        }
    }

    public static void RecordFirstFramePresented()
    {
        if (_enabled && Interlocked.Exchange(ref _firstFrameLogged, 1) == 0)
        {
            long ms = (long)Stopwatch.GetElapsedTime(_startTicks).TotalMilliseconds;
            Console.WriteLine($"NETTEXT first_frame_presented_ms={ms}");
            Console.Out.Flush();
        }
    }

    public static void RecordFirstLogFrame()
    {
        if (_enabled && Interlocked.Exchange(ref _firstLogFrameLogged, 1) == 0)
        {
            long ms = (long)Stopwatch.GetElapsedTime(_startTicks).TotalMilliseconds;
            Console.WriteLine($"NETTEXT first_log_frame_ms={ms}");
            if (_e2eEnabled) RecordMemory("first_frame");
            Console.Out.Flush();
        }
    }

    public static void RecordEditorReady()
    {
        if (_enabled && Interlocked.Exchange(ref _editorReadyLogged, 1) == 0)
        {
            long ms = (long)Stopwatch.GetElapsedTime(_startTicks).TotalMilliseconds;
            Console.WriteLine($"NETTEXT document_ready_ms={ms}");
            Console.WriteLine($"NETTEXT editor_ready_ms={ms}");
            Console.Out.Flush();
        }
    }

    public static void RecordIndexReady()
    {
        if (_enabled && Interlocked.Exchange(ref _indexReadyLogged, 1) == 0)
        {
            long ms = (long)Stopwatch.GetElapsedTime(_startTicks).TotalMilliseconds;
            Console.WriteLine($"NETTEXT index_ready_ms={ms}");
            if (_e2eEnabled) RecordMemory("index_ready");
            Console.Out.Flush();
        }
    }

    public static void RecordMetric(string name, long valueMs)
    {
        if (_enabled)
        {
            Console.WriteLine($"NETTEXT {name}={valueMs}");
            Console.Out.Flush();
        }
    }

    public static void RecordMemory(string stage)
    {
        if (!_enabled) return;
        using var proc = Process.GetCurrentProcess();
        proc.Refresh();
        var ci = CultureInfo.InvariantCulture;
        double ws = proc.WorkingSet64 / 1048576.0;
        double peakWs = proc.PeakWorkingSet64 / 1048576.0;
        double priv = proc.PrivateMemorySize64 / 1048576.0;
        Console.WriteLine(string.Create(ci, $"NETTEXT mem_{stage}_ws_mib={ws:F2} mem_{stage}_peak_ws_mib={peakWs:F2} mem_{stage}_priv_mib={priv:F2}"));
        Console.Out.Flush();
    }

    public static void RecordCancelSearch(long cancelStartTicks)
    {
        if (_enabled)
        {
            long ms = (long)Stopwatch.GetElapsedTime(cancelStartTicks).TotalMilliseconds;
            Console.WriteLine($"NETTEXT cancel_search_ms={ms}");
            Console.Out.Flush();
        }
    }

    public static void DumpMemoryProfile(string tag)
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NETTEXT_PROFILE_MEM")))
        {
            return;
        }

        using var proc = Process.GetCurrentProcess();
        proc.Refresh();
        var gcInfoBefore = GC.GetGCMemoryInfo();
        long liveBefore = GC.GetTotalMemory(false);
        long liveAfter = GC.GetTotalMemory(true);
        var gcInfoAfter = GC.GetGCMemoryInfo();
        proc.Refresh();

        Console.WriteLine(
            $"MEM[{tag}] WS={proc.WorkingSet64 / 1048576.0:F1}MiB PeakWS={proc.PeakWorkingSet64 / 1048576.0:F1}MiB Priv={proc.PrivateMemorySize64 / 1048576.0:F1}MiB | " +
            $"GC_LiveBefore={liveBefore / 1048576.0:F1}MiB GC_LiveAfter={liveAfter / 1048576.0:F1}MiB " +
            $"GC_HeapBefore={gcInfoBefore.HeapSizeBytes / 1048576.0:F1}MiB GC_HeapAfter={gcInfoAfter.HeapSizeBytes / 1048576.0:F1}MiB " +
            $"GC_Committed={gcInfoAfter.TotalCommittedBytes / 1048576.0:F1}MiB Gen0={GC.CollectionCount(0)} Gen1={GC.CollectionCount(1)} Gen2={GC.CollectionCount(2)}");
        Console.Out.Flush();
    }
}
