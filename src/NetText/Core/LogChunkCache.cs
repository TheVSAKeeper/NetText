using System.Buffers;
using Microsoft.Win32.SafeHandles;

namespace NetText.Core;

public sealed class LogChunkCache
{
    public const int ChunkSize = 64 * 1024;
    public const int DefaultCapacity = 64;

    private sealed class Entry
    {
        public ulong ChunkOffset;
        public ulong FileSize;
        public int ValidBytes;
        public long LastUsed;
        public readonly byte[] Buffer = GC.AllocateUninitializedArray<byte>(ChunkSize);
    }

    private readonly int _capacity;
    private readonly List<Entry> _entries;
    private readonly Dictionary<ulong, Entry> _byOffset;
    private readonly object _gate = new();
    private long _clock;

    public LogChunkCache(int capacity = DefaultCapacity)
    {
        _capacity = Math.Max(1, capacity);
        _entries = new List<Entry>(_capacity);
        _byOffset = new Dictionary<ulong, Entry>(_capacity);
    }

    public int Count
    {
        get { lock (_gate) return _entries.Count; }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _byOffset.Clear();
        }
    }

    public void InvalidateFrom(ulong offset)
    {
        ulong minChunk = offset - (offset % ChunkSize);
        lock (_gate)
        {
            if (_entries.Count == 0) return;
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                if (_entries[i].ChunkOffset >= minChunk)
                {
                    _byOffset.Remove(_entries[i].ChunkOffset);
                    _entries.RemoveAt(i);
                }
            }
        }
    }

    public int Read(SafeFileHandle handle, ulong fileSize, ulong offset, Span<byte> destination)
    {
        if (destination.IsEmpty || offset >= fileSize)
        {
            return 0;
        }

        int maxCopy = (int)Math.Min((ulong)destination.Length, fileSize - offset);
        int copied = 0;

        while (copied < maxCopy)
        {
            ulong cur = offset + (ulong)copied;
            ulong chunkOff = cur - (cur % ChunkSize);
            int inChunk = (int)(cur - chunkOff);
            int want = Math.Min(ChunkSize - inChunk, maxCopy - copied);

            if (!TryCopyFromCache(chunkOff, fileSize, inChunk, want, destination.Slice(copied, want), out int avail))
            {
                avail = LoadAndCopyChunk(handle, fileSize, chunkOff, inChunk, want, destination.Slice(copied, want));
            }

            if (avail <= 0)
            {
                break;
            }

            copied += avail;
            if (avail < want)
            {
                break;
            }
        }

        return copied;
    }

    private bool TryCopyFromCache(ulong chunkOff, ulong fileSize, int inChunk, int want, Span<byte> dest, out int avail)
    {
        lock (_gate)
        {
            if (_byOffset.TryGetValue(chunkOff, out var entry) && entry.FileSize == fileSize && entry.ValidBytes > 0)
            {
                entry.LastUsed = ++_clock;
                avail = Math.Max(0, Math.Min(want, entry.ValidBytes - inChunk));
                if (avail > 0)
                {
                    entry.Buffer.AsSpan(inChunk, avail).CopyTo(dest);
                }
                return true;
            }
        }

        avail = 0;
        return false;
    }

    private int LoadAndCopyChunk(SafeFileHandle handle, ulong fileSize, ulong chunkOff, int inChunk, int want, Span<byte> dest)
    {
        int chunkWant = (int)Math.Min((ulong)ChunkSize, fileSize - chunkOff);
        byte[] tmp = ArrayPool<byte>.Shared.Rent(ChunkSize);
        try
        {
            int read = LogIo.ReadAt(handle, chunkOff, tmp.AsSpan(0, chunkWant));
            if (read <= 0)
            {
                return 0;
            }

            lock (_gate)
            {
                if (_byOffset.TryGetValue(chunkOff, out var existing) && existing.FileSize == fileSize && existing.ValidBytes > 0)
                {
                    existing.LastUsed = ++_clock;
                    int eAvail = Math.Max(0, Math.Min(want, existing.ValidBytes - inChunk));
                    if (eAvail > 0)
                    {
                        existing.Buffer.AsSpan(inChunk, eAvail).CopyTo(dest);
                    }
                    return eAvail;
                }

                Entry target;
                if (_byOffset.TryGetValue(chunkOff, out var stale))
                {
                    target = stale;
                }
                else if (_entries.Count < _capacity)
                {
                    target = new Entry();
                    _entries.Add(target);
                }
                else
                {
                    target = _entries[0];
                    for (int i = 1; i < _entries.Count; i++)
                    {
                        if (_entries[i].LastUsed < target.LastUsed)
                        {
                            target = _entries[i];
                        }
                    }
                    _byOffset.Remove(target.ChunkOffset);
                }

                target.ChunkOffset = chunkOff;
                target.FileSize = fileSize;
                target.ValidBytes = read;
                target.LastUsed = ++_clock;
                Buffer.BlockCopy(tmp, 0, target.Buffer, 0, read);
                _byOffset[chunkOff] = target;
            }

            int avail = Math.Max(0, Math.Min(want, read - inChunk));
            if (avail > 0)
            {
                tmp.AsSpan(inChunk, avail).CopyTo(dest);
            }
            return avail;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(tmp);
        }
    }
}
