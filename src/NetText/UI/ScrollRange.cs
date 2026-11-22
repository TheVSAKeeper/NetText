namespace NetText.UI;

internal readonly record struct ScrollRange(ulong Start, ulong End, ulong Extent)
{
    public ulong Span => End - Start;
    public ulong LastStart => Extent - Span;
    public bool CanScroll => LastStart > 0;

    public static ScrollRange FromViewport(ulong start, ulong length, ulong extent)
    {
        length = Math.Min(length, extent);
        start = Math.Min(start, extent - length);
        return new(start, start + length, extent);
    }

    public (int Offset, int Size) Thumb(int length, int minimum)
    {
        if (!CanScroll || length <= 0) return (0, length);
        int size = Math.Clamp((int)Math.Round(length * (double)Span / Extent), Math.Min(minimum, length), length);
        int offset = (int)Math.Round((length - size) * (double)Start / LastStart);
        return (offset, size);
    }

    public ulong Seek(int position, int length) => ClampPosition(position * (double)Extent / Math.Max(1, length) - Span / 2.0);

    public ulong Drag(int delta, int offset, int travel)
    {
        if (delta == 0 || travel <= 0) return Start;
        if (offset + delta <= 0) return 0;
        if (offset + delta >= travel) return LastStart;
        return ClampPosition(Start + delta * (double)LastStart / travel);
    }

    private ulong ClampPosition(double position) => (ulong)Math.Round(Math.Clamp(position, 0, (double)LastStart));
}
