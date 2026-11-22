using System.IO;

namespace NetText.Core;

internal sealed class EditorFileWatch : IDisposable
{
    public const int DefaultDebounceMs = 250;
    public const int FallbackPollIntervalMs = 5000;
    public const int DefaultMaxRetries = 3;

    private readonly CancellationTokenSource _stop = new();
    private readonly Func<EditorFileWatch, int, Task<bool>> _refresh;
    private readonly Action<Action>? _marshal;
    private readonly FileSystemWatcher? _watcher;
    private readonly Timer _timer;
    private readonly object _gate = new();
    private int _attempt;
    private int _disposed;
    private bool _refreshing;
    private bool _forceProbe;
    private (bool Exists, long Length, DateTime ModifiedUtc)? _fallbackObservation;

    public EditorFileWatch(string path, Action onChanged, Action<Action>? marshal = null)
        : this(path, (_, _) =>
        {
            onChanged();
            return Task.FromResult(false);
        }, marshal)
    {
    }

    public EditorFileWatch(string path, Func<EditorFileWatch, int, Task<bool>> refresh, Action<Action>? marshal = null)
    {
        Path = System.IO.Path.GetFullPath(path);
        _refresh = refresh ?? throw new ArgumentNullException(nameof(refresh));
        _marshal = marshal;
        Token = _stop.Token;
        _timer = new Timer(_ => OnTimerTick(), null, Timeout.Infinite, Timeout.Infinite);

        string? directory = System.IO.Path.GetDirectoryName(Path);
        string fileName = System.IO.Path.GetFileName(Path);
        if (!string.IsNullOrEmpty(directory) && !string.IsNullOrEmpty(fileName))
        {
            try
            {
                var watcher = new FileSystemWatcher(directory, fileName)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime
                };
                watcher.Changed += HandleFsEvent;
                watcher.Created += HandleFsEvent;
                watcher.Deleted += HandleFsEvent;
                watcher.Renamed += HandleFsEvent;
                watcher.Error += HandleError;
                watcher.EnableRaisingEvents = true;
                _watcher = watcher;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            {
                _watcher?.Dispose();
                _watcher = null;
            }
        }

        if (_watcher is null)
        {
            _timer.Change(FallbackPollIntervalMs, Timeout.Infinite);
        }
    }

    public string Path { get; }
    public CancellationToken Token { get; }
    public bool UsesFallbackPolling => _watcher is null;

    public void Schedule(bool force)
    {
        if (Volatile.Read(ref _disposed) != 0 || Token.IsCancellationRequested) return;
        lock (_gate)
        {
            if (force)
            {
                _attempt = 0;
                _forceProbe = true;
            }
            _timer.Change(DefaultDebounceMs, Timeout.Infinite);
        }
    }

    public static async Task<T> ReadWithRetryAsync<T>(
        Func<CancellationToken, Task<T>> read,
        CancellationToken token = default,
        int maxRetries = DefaultMaxRetries,
        int delayMs = 75)
    {
        ArgumentNullException.ThrowIfNull(read);
        int retries = Math.Max(0, maxRetries);
        for (int attempt = 0; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                return await read(token).ConfigureAwait(false);
            }
            catch (IOException) when (attempt < retries && !token.IsCancellationRequested)
            {
                await Task.Delay(delayMs, token).ConfigureAwait(false);
            }
        }
    }

    private void HandleFsEvent(object sender, FileSystemEventArgs args) => Schedule(force: true);

    private void HandleError(object sender, ErrorEventArgs args) => Schedule(force: true);

    private void OnTimerTick()
    {
        if (Volatile.Read(ref _disposed) != 0 || Token.IsCancellationRequested) return;
        if (_marshal is not null)
        {
            _marshal(() => _ = RunTickAsync());
        }
        else
        {
            _ = RunTickAsync();
        }
    }

    private async Task RunTickAsync()
    {
        if (Volatile.Read(ref _disposed) != 0 || Token.IsCancellationRequested) return;
        bool forceProbe;
        int attempt;
        lock (_gate)
        {
            if (_refreshing) return;
            _refreshing = true;
            forceProbe = _forceProbe;
            _forceProbe = false;
            attempt = _attempt;
        }

        try
        {
            if (_watcher is null && !forceProbe && attempt == 0)
            {
                var info = new FileInfo(Path);
                var observation = (info.Exists, info.Exists ? info.Length : 0L, info.Exists ? info.LastWriteTimeUtc : DateTime.MinValue);
                if (observation == _fallbackObservation)
                {
                    return;
                }
                _fallbackObservation = observation;
            }

            bool retry = await _refresh(this, attempt).ConfigureAwait(false);
            lock (_gate)
            {
                _attempt = retry ? attempt + 1 : 0;
            }
            if (retry && Volatile.Read(ref _disposed) == 0 && !Token.IsCancellationRequested)
            {
                _timer.Change(DefaultDebounceMs, Timeout.Infinite);
            }
        }
        catch (OperationCanceledException) when (Token.IsCancellationRequested)
        {
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
        finally
        {
            lock (_gate)
            {
                _refreshing = false;
                if (_watcher is null && Volatile.Read(ref _disposed) == 0 && !Token.IsCancellationRequested && _attempt == 0)
                {
                    _timer.Change(FallbackPollIntervalMs, Timeout.Infinite);
                }
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        _timer.Dispose();
        _watcher?.Dispose();
        _stop.Dispose();
    }
}
