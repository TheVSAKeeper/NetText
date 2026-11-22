using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using NetText.Native;

namespace NetText.Core;

internal sealed record TargetProcessMatch(int ProcessId, string ProcessName);

internal static class ConsoleCapture
{
    public const string WorkerArgument = "--read-console-buffer";
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(150);
    private static readonly Encoding Utf8 = new UTF8Encoding(false);

    public static int? TryRunWorker(string[] args)
    {
        if (args.Length == 0 || args[0] != WorkerArgument) return null;
        if (args.Length != 2 ||
            !int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out int pid))
        {
            return 1;
        }
        return RunHelperProcess(pid);
    }

    public static int RunHelperProcess(int pid)
    {
        if (!OperatingSystem.IsWindows() || pid <= 0 || pid == Environment.ProcessId)
        {
            return 1;
        }

        try
        {
            using var parent = new StreamReader(Console.OpenStandardInput());
            using var output = new StreamWriter(Console.OpenStandardOutput(), Utf8);
            var disconnected = parent.ReadLineAsync();

            try
            {
                using var target = Process.GetProcessById(pid);
                using var buffer = new ConsoleScreenBuffer(pid);
                bool initial = true;

                while (!disconnected.IsCompleted)
                {
                    bool sourceExited = target.HasExited;
                    string[] rows = buffer.ReadNewRows(CancellationToken.None);
                    if (initial || rows.Length > 0)
                    {
                        output.WriteLine(rows.Length.ToString(CultureInfo.InvariantCulture));
                        foreach (string row in rows)
                        {
                            output.WriteLine(row);
                        }
                        output.Flush();
                        initial = false;
                    }

                    if (sourceExited || disconnected.Wait(PollInterval))
                    {
                        break;
                    }
                }

                return 0;
            }
            catch (Exception error) when (error is Win32Exception or ArgumentException)
            {
                output.WriteLine(error.Message);
                output.Flush();
                return 1;
            }
        }
        catch (IOException)
        {
            return 1;
        }
    }

    public static IReadOnlyList<TargetProcessMatch> FindTargetProcesses(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];

        string trimmed = query.Trim();
        if (int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out int pid))
        {
            if (pid <= 0 || pid == Environment.ProcessId) return [];
            try
            {
                using var process = Process.GetProcessById(pid);
                return [new TargetProcessMatch(process.Id, process.ProcessName)];
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException)
            {
                return [];
            }
        }

        string name = trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? trimmed[..^4]
            : trimmed;
        if (string.IsNullOrEmpty(name)) return [];

        var matches = new List<TargetProcessMatch>();
        var processes = Process.GetProcessesByName(name);
        try
        {
            foreach (var process in processes)
            {
                if (process.Id != Environment.ProcessId)
                {
                    matches.Add(new TargetProcessMatch(process.Id, process.ProcessName));
                }
            }
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }

        matches.Sort((a, b) => a.ProcessId.CompareTo(b.ProcessId));
        return matches;
    }
}

internal sealed class ConsoleCaptureSession : IDisposable
{
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(10);
    private static readonly Encoding Utf8 = new UTF8Encoding(false);

    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly FileStream _file;
    private volatile bool _connected;
    private volatile bool _disconnected;
    private volatile Exception? _disconnectError;
    private bool _disposed;

    private ConsoleCaptureSession(int processId, string title)
    {
        ProcessId = processId;
        Title = title;
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"nettext-console-{Guid.NewGuid():N}.log");
        _file = new FileStream(
            Path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete,
            16 * 1024,
            FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        Completion = PumpAsync(processId, _stop.Token);
    }

    public int ProcessId { get; }
    public string Path { get; }
    public string Title { get; }
    public Task<Exception?> Completion { get; }
    public bool IsConnected => _connected && !_disconnected && !_disposed;
    public bool IsDisconnected => _disconnected;
    public Exception? DisconnectError => _disconnectError;
    public bool IsDisposed => _disposed;
    public event Action? StateChanged;

    public static async Task<ConsoleCaptureSession> ConnectAsync(int processId, string title, CancellationToken token = default)
    {
        var session = new ConsoleCaptureSession(processId, title);
        try
        {
            await session._ready.Task.WaitAsync(ConnectionTimeout, token).ConfigureAwait(false);
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    private async Task<Exception?> PumpAsync(int processId, CancellationToken token)
    {
        Process? worker = null;
        try
        {
            string executable = Environment.ProcessPath ?? throw new IOException("Путь NetText недоступен.");
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                StandardOutputEncoding = Utf8,
            };
            if (System.IO.Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            {
                start.ArgumentList.Add(Environment.GetCommandLineArgs()[0]);
            }
            start.ArgumentList.Add(ConsoleCapture.WorkerArgument);
            start.ArgumentList.Add(processId.ToString(CultureInfo.InvariantCulture));

            worker = Process.Start(start) ?? throw new IOException("Не удалось запустить чтение консоли.");
            using var disconnect = token.Register(worker.StandardInput.BaseStream.Dispose);
            using var output = worker.StandardOutput;
            using var writer = new StreamWriter(_file, Utf8, leaveOpen: true);

            while (await output.ReadLineAsync(token).ConfigureAwait(false) is { } header)
            {
                if (!int.TryParse(header, NumberStyles.None, CultureInfo.InvariantCulture, out int count))
                {
                    throw new IOException(header);
                }

                for (int i = 0; i < count; i++)
                {
                    string row = await output.ReadLineAsync(token).ConfigureAwait(false)
                        ?? throw new IOException("Поток вывода консоли прервался.");
                    await writer.WriteLineAsync(row.AsMemory(), token).ConfigureAwait(false);
                }

                await writer.FlushAsync(token).ConfigureAwait(false);
                if (!_connected)
                {
                    _connected = true;
                    _ready.TrySetResult();
                    StateChanged?.Invoke();
                }
            }

            await worker.WaitForExitAsync(token).ConfigureAwait(false);
            if (!_ready.Task.IsCompletedSuccessfully)
            {
                throw new IOException("Чтение вывода консоли завершено до получения первого кадра.");
            }
            if (worker.ExitCode != 0)
            {
                throw new IOException("Чтение вывода консоли завершено с ошибкой.");
            }
            return null;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            _ready.TrySetCanceled(token);
            return null;
        }
        catch (Exception error) when (error is IOException or Win32Exception or InvalidOperationException or UnauthorizedAccessException)
        {
            _disconnectError = error;
            _ready.TrySetException(error);
            return error;
        }
        finally
        {
            _connected = false;
            _disconnected = true;

            if (worker is not null)
            {
                using (worker)
                {
                    try
                    {
                        worker.StandardInput.BaseStream.Dispose();
                        await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                    }
                    catch (Exception error) when (error is IOException or InvalidOperationException or TimeoutException)
                    {
                        try { if (!worker.HasExited) worker.Kill(); }
                        catch (Exception stopError) when (stopError is InvalidOperationException or Win32Exception) { }
                    }
                }
            }

            StateChanged?.Invoke();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stop.Cancel();
        _ = DisposeAfterPumpAsync();
    }

    private async Task DisposeAfterPumpAsync()
    {
        try
        {
            await Completion.ConfigureAwait(false);
        }
        finally
        {
            _file.Dispose();
            _stop.Dispose();
        }
    }
}
