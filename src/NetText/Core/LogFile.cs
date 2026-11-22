using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;

namespace NetText.Core;

public enum LogChangeKind
{
    None = 0,
    Same = 1,
    Appended = 2,
    Truncated = 3,
    Rewritten = 4,
    Replaced = 5,
}

public sealed record FileIdentity(
    [property: JsonPropertyName("volume")] ulong Volume,
    [property: JsonPropertyName("id")] byte[] Id) : IEquatable<FileIdentity>
{
    public bool Equals(FileIdentity? other) =>
        other is not null && Volume == other.Volume && Id.AsSpan().SequenceEqual(other.Id);

    public override int GetHashCode()
    {
        var hc = new HashCode();
        hc.Add(Volume);
        hc.AddBytes(Id);
        return hc.ToHashCode();
    }
}

public sealed record FileStamp(FileIdentity Identity, ulong Length, DateTime? ModifiedUtc);

public sealed record LogObservation(FileStamp Stamp, byte[] Head, byte[] Tail);

public sealed record LogFingerprint(DateTime? ModifiedUtc, string Hash);

public sealed class LogBookmark
{
    [JsonPropertyName("identity")]
    public FileIdentity? Identity { get; set; }

    [JsonPropertyName("size")]
    public ulong Size { get; set; }

    [JsonPropertyName("offset")]
    public ulong Offset { get; set; }

    [JsonPropertyName("hidden")]
    public ulong Hidden { get; set; }

    [JsonPropertyName("auto_refresh")]
    public bool AutoRefresh { get; set; } = true;

    [JsonPropertyName("follow_end")]
    public bool FollowEnd { get; set; }

    [JsonPropertyName("index_opt_in"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IndexOptIn { get; set; }

    [JsonPropertyName("edit_pages"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool EditPages { get; set; }

    [JsonPropertyName("fingerprint"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LogFingerprint? Fingerprint { get; set; }

    public bool CanRestore(FileStamp stamp) =>
        Identity is not null && Identity.Equals(stamp.Identity) && stamp.Length >= Size;

    internal void Record(LogObservation observation)
    {
        Identity = observation.Stamp.Identity;
        Size = observation.Stamp.Length;
        Fingerprint = new LogFingerprint(observation.Stamp.ModifiedUtc, HashBoundary(observation.Head, observation.Tail));
    }

    internal bool CanRestore(SafeFileHandle file, LogObservation observation)
    {
        bool atStart = Offset == 0 && Hidden == 0;
        if (Identity is null) return atStart;
        if (!CanRestore(observation.Stamp)) return false;
        if (Fingerprint is not { } saved) return atStart;

        if (observation.Stamp.Length == Size)
        {
            if (saved.ModifiedUtc is { } before && observation.Stamp.ModifiedUtc is { } after && before != after) return false;
            return saved.Hash == HashBoundary(observation.Head, observation.Tail);
        }

        int count = (int)Math.Min(Size, LogFile.WitnessBytes);
        if (observation.Head.Length < count) return false;
        Span<byte> tail = stackalloc byte[LogFile.WitnessBytes];
        if (LogIo.ReadAt(file, Size - (ulong)count, tail[..count]) != count) return false;
        return saved.Hash == HashBoundary(observation.Head.AsSpan(0, count), tail[..count]);
    }

    private static string HashBoundary(ReadOnlySpan<byte> head, ReadOnlySpan<byte> tail)
    {
        Span<byte> combined = stackalloc byte[LogFile.WitnessBytes * 2];
        head.CopyTo(combined);
        tail.CopyTo(combined[head.Length..]);
        return Convert.ToHexString(SHA256.HashData(combined[..(head.Length + tail.Length)]));
    }

    public LogBookmark Copy() => (LogBookmark)MemberwiseClone();
}

public static unsafe partial class LogFile
{
    public const int WitnessBytes = 256;
    private const int FileBasicInfoClass = 0;
    private const int FileIdInfoClass = 18;
    private const uint FileAttributeDirectory = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    private struct FILE_BASIC_INFO
    {
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public long ChangeTime;
        public uint FileAttributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILE_ID_INFO
    {
        public ulong VolumeSerialNumber;
        public fixed byte FileId[16];
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandleEx(
        SafeFileHandle hFile,
        int FileInformationClass,
        void* lpFileInformation,
        uint dwBufferSize);

    public static FileStamp StampOf(string path, SafeFileHandle handle)
    {
        FILE_BASIC_INFO basic;
        if (GetFileInformationByHandleEx(handle, FileBasicInfoClass, &basic, (uint)sizeof(FILE_BASIC_INFO)))
        {
            if ((basic.FileAttributes & FileAttributeDirectory) != 0)
            {
                throw new InvalidDataException("Выберите обычный файл.");
            }
        }
        else if (Directory.Exists(path))
        {
            throw new InvalidDataException("Выберите обычный файл.");
        }

        FILE_ID_INFO idInfo;
        if (!GetFileInformationByHandleEx(handle, FileIdInfoClass, &idInfo, (uint)sizeof(FILE_ID_INFO)))
        {
            int err = Marshal.GetLastPInvokeError();
            throw new IOException($"Не удалось прочитать идентификатор файла (Win32 {err})");
        }

        var idBytes = new byte[16];
        new ReadOnlySpan<byte>(idInfo.FileId, 16).CopyTo(idBytes);
        ulong len = (ulong)RandomAccess.GetLength(handle);
        DateTime? modified = basic.LastWriteTime > 0 ? DateTime.FromFileTimeUtc(basic.LastWriteTime) : null;
        return new FileStamp(new FileIdentity(idInfo.VolumeSerialNumber, idBytes), len, modified);
    }

    public static (LogObservation Observation, LogChangeKind Change) Observe(string path, LogObservation? previous)
    {
        if (Directory.Exists(path))
        {
            throw new InvalidDataException("Выберите обычный файл.");
        }

        using var handle = File.OpenHandle(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        return ObserveHandle(path, handle, previous);
    }

    public static (LogObservation Observation, LogChangeKind Change) ObserveHandle(
        string path,
        SafeFileHandle handle,
        LogObservation? previous)
    {
        var stamp = StampOf(path, handle);

        int count = (int)Math.Min(stamp.Length, WitnessBytes);
        Span<byte> headBuf = stackalloc byte[WitnessBytes];
        Span<byte> tailBuf = stackalloc byte[WitnessBytes];
        int headLen = count == 0 ? 0 : ReadExactSpan(handle, 0, headBuf[..count]);
        int tailLen;
        if (count == 0)
        {
            tailLen = 0;
        }
        else if (stamp.Length <= (ulong)count)
        {
            headBuf[..headLen].CopyTo(tailBuf);
            tailLen = headLen;
        }
        else
        {
            tailLen = ReadExactSpan(handle, stamp.Length - (ulong)count, tailBuf[..count]);
        }

        ReadOnlySpan<byte> headSpan = headBuf[..headLen];
        ReadOnlySpan<byte> tailSpan = tailBuf[..tailLen];

        if (previous is not null && previous.Stamp.Equals(stamp))
        {
            bool same = headSpan.SequenceEqual(previous.Head) && tailSpan.SequenceEqual(previous.Tail);
            return same
                ? (previous, LogChangeKind.Same)
                : (new LogObservation(stamp, headSpan.ToArray(), tailSpan.ToArray()), LogChangeKind.Rewritten);
        }

        var current = new LogObservation(stamp, headSpan.ToArray(), tailSpan.ToArray());

        if (previous is null)
        {
            return (current, LogChangeKind.Replaced);
        }

        if (!previous.Stamp.Identity.Equals(stamp.Identity))
        {
            return (current, LogChangeKind.Replaced);
        }

        if (stamp.Length < previous.Stamp.Length)
        {
            return (current, LogChangeKind.Truncated);
        }

        if (stamp.Length > previous.Stamp.Length)
        {
            Span<byte> prevWitness = stackalloc byte[WitnessBytes];
            int prevHeadRead = ReadExactSpan(handle, 0, prevWitness[..previous.Head.Length]);
            bool headMatch = prevHeadRead == previous.Head.Length && prevWitness[..prevHeadRead].SequenceEqual(previous.Head);
            ulong tailOffset = previous.Stamp.Length - (ulong)previous.Tail.Length;
            int prevTailRead = ReadExactSpan(handle, tailOffset, prevWitness[..previous.Tail.Length]);
            bool tailMatch = prevTailRead == previous.Tail.Length && prevWitness[..prevTailRead].SequenceEqual(previous.Tail);
            return (current, headMatch && tailMatch ? LogChangeKind.Appended : LogChangeKind.Rewritten);
        }

        return (current, LogChangeKind.Rewritten);
    }

    private static int ReadExactSpan(SafeFileHandle handle, ulong offset, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = RandomAccess.Read(handle, buffer[total..], (long)offset + total);
            if (read == 0)
            {
                break;
            }
            total += read;
        }
        return total;
    }
}
