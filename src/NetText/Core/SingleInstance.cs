using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetText.Core;

public sealed record LaunchRequest(
    [property: JsonPropertyName("cwd")] string Cwd,
    [property: JsonPropertyName("paths")] List<string> Paths,
    [property: JsonPropertyName("activate")] bool Activate)
{
    public const int MaxPaths = 512;
    public const int MaxPathBytes = 32 * 1024;

    public static LaunchRequest FromProcess(string[] args) =>
        new(Environment.CurrentDirectory, [.. args], true);

    public string[] ResolveAbsolutePaths()
    {
        var result = new string[Paths.Count];
        for (int i = 0; i < Paths.Count; i++)
        {
            var p = Paths[i];
            result[i] = Path.IsPathRooted(p) ? p : Path.Combine(Cwd, p);
        }
        return result;
    }

    public void Validate()
    {
        if (Paths.Count > MaxPaths)
        {
            throw new ArgumentException($"Too many paths: {Paths.Count} > {MaxPaths}");
        }
        foreach (var p in Paths)
        {
            if (Encoding.UTF8.GetByteCount(p) > MaxPathBytes)
            {
                throw new ArgumentException("Path exceeds 32 KiB limit");
            }
        }
    }
}

internal sealed record InstanceMetadata(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("pipe")] string PipeName,
    [property: JsonPropertyName("capability")] string Capability,
    [property: JsonPropertyName("pid")] int Pid);

internal sealed record WireEnvelope(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("capability")] string Capability,
    [property: JsonPropertyName("request")] LaunchRequest Request);

[JsonSerializable(typeof(InstanceMetadata))]
[JsonSerializable(typeof(WireEnvelope))]
internal partial class SingleInstanceJsonContext : JsonSerializerContext;

public enum InstanceRoleKind
{
    None = 0,
    Primary = 1,
    Forwarded = 2,
}

public sealed partial class SingleInstance : IDisposable
{
    public const int ProtocolVersion = 2;
    public const int MaxMessageBytes = 256 * 1024;
    private const byte AckOk = 1;
    private const byte AckRejected = 0;
    private static readonly TimeSpan StartupRetry = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ConnectRetry = TimeSpan.FromMilliseconds(40);
    private const int IoTimeoutMs = 700;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(uint dwProcessId);

    private readonly string _profileDir;
    private readonly FileStream _lockStream;
    private readonly string _pipeName;
    private readonly string _capability;
    private readonly ConcurrentQueue<LaunchRequest> _incoming = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _acceptThread;
    private readonly string _metaPath;
    private Action? _onIncoming;
    private bool _disposed;

    private SingleInstance(string profileDir, FileStream lockStream, string pipeName, string capability)
    {
        _profileDir = profileDir;
        _lockStream = lockStream;
        _pipeName = pipeName;
        _capability = capability;
        _metaPath = Path.Combine(profileDir, "instance.json");

        _acceptThread = new Thread(AcceptLoop)
        {
            Name = "nettext-instance-ipc",
            IsBackground = true,
        };
        _acceptThread.Start();
    }

    public static string DefaultProfileDirectory() =>
        Path.Combine(AppPaths.RequireLocalAppData(), "NetText");

    public static (InstanceRoleKind Role, SingleInstance? Instance) Acquire(LaunchRequest initialRequest) =>
        AcquireIn(DefaultProfileDirectory(), initialRequest);

    public static (InstanceRoleKind Role, SingleInstance? Instance) AcquireIn(string profileDir, LaunchRequest initialRequest)
    {
        initialRequest.Validate();
        Directory.CreateDirectory(profileDir);
        var lockPath = Path.Combine(profileDir, "instance.lock");

        var deadline = DateTime.UtcNow + StartupRetry;
        while (true)
        {
            FileStream? lockStream = TryOpenLock(lockPath);
            if (lockStream is not null)
            {
                try
                {
                    var instance = BecomePrimary(profileDir, lockStream);
                    return (InstanceRoleKind.Primary, instance);
                }
                catch
                {
                    lockStream.Dispose();
                    throw;
                }
            }

            if (TryForwardToPrimary(profileDir, initialRequest))
            {
                return (InstanceRoleKind.Forwarded, null);
            }

            if (DateTime.UtcNow >= deadline)
            {
                var retryLock = TryOpenLock(lockPath);
                if (retryLock is not null)
                {
                    return (InstanceRoleKind.Primary, BecomePrimary(profileDir, retryLock));
                }
                throw new TimeoutException("Не удалось подключиться к основному окну NetText");
            }

            Thread.Sleep(ConnectRetry);
        }
    }

    public void SetWakeupCallback(Action callback)
    {
        Volatile.Write(ref _onIncoming, callback);
        if (!_incoming.IsEmpty)
        {
            callback();
        }
    }

    public bool TryReceive(out LaunchRequest? request) => _incoming.TryDequeue(out request);

    public static bool ConstantTimeEqualsHex(string a, string b)
    {
        var ab = Encoding.ASCII.GetBytes(a);
        var bb = Encoding.ASCII.GetBytes(b);
        return ab.Length == bb.Length && CryptographicOperations.FixedTimeEquals(ab, bb);
    }

    private static FileStream? TryOpenLock(string lockPath)
    {
        try
        {
            return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static SingleInstance BecomePrimary(string profileDir, FileStream lockStream)
    {
        Span<byte> random = stackalloc byte[32];
        RandomNumberGenerator.Fill(random);
        string capability = Convert.ToHexStringLower(random);
        string pipeName = $"NetText.{Environment.ProcessId}.{Guid.NewGuid():N}";

        var instance = new SingleInstance(profileDir, lockStream, pipeName, capability);
        var meta = new InstanceMetadata(ProtocolVersion, pipeName, capability, Environment.ProcessId);
        var metaBytes = JsonSerializer.SerializeToUtf8Bytes(meta, SingleInstanceJsonContext.Default.InstanceMetadata);

        var tmpPath = Path.Combine(profileDir, $"instance.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
        var finalPath = Path.Combine(profileDir, "instance.json");
        try
        {
            using (var fs = new FileStream(tmpPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.Write(metaBytes);
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmpPath, finalPath, overwrite: true);
            return instance;
        }
        catch
        {
            instance.Dispose();
            throw;
        }
        finally
        {
            if (File.Exists(tmpPath))
            {
                try { File.Delete(tmpPath); } catch { }
            }
        }
    }

    private static bool TryForwardToPrimary(string profileDir, LaunchRequest request)
    {
        var metaPath = Path.Combine(profileDir, "instance.json");
        if (!File.Exists(metaPath))
        {
            return false;
        }

        InstanceMetadata? meta;
        try
        {
            var bytes = File.ReadAllBytes(metaPath);
            if (bytes.Length == 0 || bytes.Length > MaxMessageBytes)
            {
                return false;
            }
            meta = JsonSerializer.Deserialize(bytes, SingleInstanceJsonContext.Default.InstanceMetadata);
        }
        catch
        {
            return false;
        }

        if (meta is null ||
            meta.Version != ProtocolVersion ||
            !IsValidPipeName(meta.PipeName) ||
            !IsValidCapabilityHex(meta.Capability))
        {
            return false;
        }

        if (request.Activate && meta.Pid > 0)
        {
            try { AllowSetForegroundWindow((uint)meta.Pid); } catch { }
        }

        var envelope = new WireEnvelope(ProtocolVersion, meta.Capability, request);
        var payload = JsonSerializer.SerializeToUtf8Bytes(envelope, SingleInstanceJsonContext.Default.WireEnvelope);
        if (payload.Length == 0 || payload.Length > MaxMessageBytes)
        {
            throw new ArgumentException("IPC payload exceeds 256 KiB");
        }

        try
        {
            using var client = new NamedPipeClientStream(".", meta.PipeName, PipeDirection.InOut, PipeOptions.None);
            client.Connect(IoTimeoutMs);

            Span<byte> lenPrefix = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(lenPrefix, (uint)payload.Length);
            client.Write(lenPrefix);
            client.Write(payload);
            client.Flush();

            int ack = client.ReadByte();
            return ack == AckOk;
        }
        catch
        {
            return false;
        }
    }

    private void AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                server.WaitForConnectionAsync(_cts.Token).GetAwaiter().GetResult();
                HandleClient(server);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch
            {
            }
        }
    }

    private void HandleClient(NamedPipeServerStream stream)
    {
        byte ack = AckRejected;
        try
        {
            using var ioCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            ioCts.CancelAfter(IoTimeoutMs);

            byte[] prefix = new byte[4];
            ReadExactlyAsync(stream, prefix, ioCts.Token).GetAwaiter().GetResult();
            uint len = BinaryPrimitives.ReadUInt32BigEndian(prefix);
            if (len == 0 || len > MaxMessageBytes)
            {
                stream.WriteByte(AckRejected);
                return;
            }

            var payload = new byte[len];
            ReadExactlyAsync(stream, payload, ioCts.Token).GetAwaiter().GetResult();

            var envelope = JsonSerializer.Deserialize(payload, SingleInstanceJsonContext.Default.WireEnvelope);
            if (envelope is not null &&
                envelope.Version == ProtocolVersion &&
                ConstantTimeEqualsHex(envelope.Capability, _capability))
            {
                envelope.Request.Validate();
                _incoming.Enqueue(envelope.Request);
                ack = AckOk;
            }
        }
        catch
        {
            ack = AckRejected;
        }

        try
        {
            stream.WriteByte(ack);
            stream.Flush();
        }
        catch
        {
        }

        if (ack == AckOk)
        {
            Volatile.Read(ref _onIncoming)?.Invoke();
        }
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset), ct).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException();
            }
            offset += read;
        }
    }

    private static bool IsValidPipeName(string pipeName) =>
        pipeName.StartsWith("NetText.", StringComparison.Ordinal) &&
        pipeName.IndexOfAny(['\\', '/', ':']) < 0 &&
        pipeName.Length <= 128;

    private static bool IsValidCapabilityHex(string cap)
    {
        if (cap.Length != 64)
        {
            return false;
        }
        foreach (char c in cap)
        {
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
            {
                return false;
            }
        }
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _cts.Cancel();
        try { _acceptThread.Join(500); } catch { }
        try
        {
            if (File.Exists(_metaPath))
            {
                File.Delete(_metaPath);
            }
        }
        catch
        {
        }
        _lockStream.Dispose();
        _cts.Dispose();
    }
}
