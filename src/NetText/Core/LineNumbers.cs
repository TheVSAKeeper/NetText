using System.Globalization;

namespace NetText.Core;

public static class LineNumbers
{
    public static NumberFormatInfo FormatInfo { get; } = new()
    {
        NumberGroupSeparator = ".",
        NumberDecimalDigits = 0,
    };

    public static string Format(long value) => value.ToString("N0", FormatInfo);

    public static string Format(int value) => ((long)value).ToString("N0", FormatInfo);

    public static int FormatUtf8(long value, Span<byte> destination)
    {
        if (value == 0)
        {
            destination[0] = (byte)'0';
            return 1;
        }

        bool negative = value < 0;
        ulong remaining = negative ? (ulong)(-(value + 1)) + 1UL : (ulong)value;

        Span<byte> rev = stackalloc byte[32];
        int count = 0;
        int group = 0;
        while (remaining > 0)
        {
            if (group == 3)
            {
                rev[count++] = (byte)'.';
                group = 0;
            }
            (remaining, ulong digit) = Math.DivRem(remaining, 10);
            rev[count++] = (byte)('0' + (int)digit);
            group++;
        }

        if (negative)
        {
            rev[count++] = (byte)'-';
        }

        for (int i = 0; i < count; i++)
        {
            destination[i] = rev[count - 1 - i];
        }
        return count;
    }
}
