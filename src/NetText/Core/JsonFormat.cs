using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace NetText.Core;

public static unsafe class JsonFormat
{
    public const int MaxIndentDepth = 1024;
    private static readonly SearchValues<char> StringSpecials = SearchValues.Create("\"\\");
    private static readonly SearchValues<char> NonStringDelims = SearchValues.Create(" \t\r\n,]}:\"");
    private static readonly char[] SpaceIndent256 = Enumerable.Repeat(' ', 256).ToArray();

    public static bool TryFormat(string source, string newline, out string formatted, out string error)
    {
        int exactUtf8 = Encoding.UTF8.GetByteCount(source);
        byte* unmanagedUtf8 = exactUtf8 > 262_144 ? (byte*)NativeMemory.Alloc((nuint)Math.Max(1, exactUtf8)) : null;
        byte[]? rentedUtf8 = unmanagedUtf8 == null ? ArrayPool<byte>.Shared.Rent(Math.Max(1, exactUtf8)) : null;
        try
        {
            Span<byte> utf8Span = unmanagedUtf8 != null
                ? new Span<byte>(unmanagedUtf8, exactUtf8)
                : rentedUtf8.AsSpan(0, exactUtf8);
            Encoding.UTF8.GetBytes(source.AsSpan(), utf8Span);
            if (!ValidateUtf8Json(utf8Span, out error))
            {
                formatted = "";
                return false;
            }
        }
        finally
        {
            if (unmanagedUtf8 != null) NativeMemory.Free(unmanagedUtf8);
            if (rentedUtf8 != null) ArrayPool<byte>.Shared.Return(rentedUtf8);
        }

        ReadOnlySpan<char> src = source.AsSpan();
        int outCap = Math.Max(256, source.Length + source.Length / 4 + 64);
        var sb = new StringBuilder(Math.Min(outCap, 64 * 1024));
        int depth = 0;
        byte previous = 0;
        int i = 0;

        while (i < src.Length)
        {
            char ch = src[i];
            if (IsAsciiWhitespace(ch))
            {
                i++;
                continue;
            }

            if (ch == '"')
            {
                int start = i++;
                while (i < src.Length)
                {
                    int rel = src[i..].IndexOfAny(StringSpecials);
                    if (rel < 0)
                    {
                        i = src.Length;
                        break;
                    }
                    i += rel;
                    char hit = src[i++];
                    if (hit == '"')
                    {
                        break;
                    }
                    if (i < src.Length)
                    {
                        i++;
                    }
                }
                sb.Append(src[start..i]);
                previous = (byte)'"';
                continue;
            }

            switch (ch)
            {
                case '{':
                case '[':
                    depth++;
                    if (depth > MaxIndentDepth)
                    {
                        formatted = "";
                        error = $"Глубина вложенности JSON превышает {MaxIndentDepth}";
                        return false;
                    }
                    sb.Append(ch);
                    i++;
                    while (i < src.Length && IsAsciiWhitespace(src[i]))
                    {
                        i++;
                    }
                    if (i < src.Length && src[i] is not ('}' or ']'))
                    {
                        sb.Append(newline);
                        AppendIndent(sb, depth * 2);
                    }
                    previous = (byte)ch;
                    break;

                case '}':
                case ']':
                    depth = Math.Max(0, depth - 1);
                    if (previous is not ((byte)'{' or (byte)'['))
                    {
                        sb.Append(newline);
                        AppendIndent(sb, depth * 2);
                    }
                    sb.Append(ch);
                    previous = (byte)ch;
                    i++;
                    break;

                case ',':
                    sb.Append(',');
                    sb.Append(newline);
                    AppendIndent(sb, depth * 2);
                    previous = (byte)',';
                    i++;
                    break;

                case ':':
                    sb.Append(": ");
                    previous = (byte)':';
                    i++;
                    break;

                default:
                {
                    int rel = src[i..].IndexOfAny(NonStringDelims);
                    int runLen = rel < 0 ? src.Length - i : Math.Max(1, rel);
                    sb.Append(src.Slice(i, runLen));
                    char last = src[i + runLen - 1];
                    previous = last <= 0x7F ? (byte)last : (byte)'_';
                    i += runLen;
                    break;
                }
            }
        }

        if (source.EndsWith('\r') || source.EndsWith('\n'))
        {
            sb.Append(newline);
        }

        formatted = sb.ToString();
        error = "";
        return true;
    }

    private static readonly SearchValues<byte> Utf8StringSpecials = SearchValues.Create("\"\\"u8);
    private static readonly SearchValues<byte> Utf8NonStringDelims = SearchValues.Create(" \t\r\n,]}:\""u8);
    private static readonly byte[] SpaceIndentBytes256 = Enumerable.Repeat((byte)' ', 256).ToArray();

    public static bool TryFormatUtf8(ReadOnlySpan<byte> utf8, ReadOnlySpan<byte> newline, out byte[] formatted, out string error)
    {
        if (!TryFormatUtf8Native(utf8, newline, out byte* buf, out int len, out error))
        {
            formatted = [];
            return false;
        }
        try
        {
            formatted = new ReadOnlySpan<byte>(buf, len).ToArray();
            return true;
        }
        finally
        {
            NativeMemory.Free(buf);
        }
    }

    internal static bool TryFormatUtf8Native(
        ReadOnlySpan<byte> src,
        ReadOnlySpan<byte> newline,
        out byte* nativeOut,
        out int outLen,
        out string error)
    {
        if (!ValidateUtf8Json(src, out error))
        {
            nativeOut = null;
            outLen = 0;
            return false;
        }

        int cap = Math.Max(256, src.Length + src.Length / 4 + 64);
        byte* buf = (byte*)NativeMemory.Alloc((nuint)cap);
        int len = 0;

        try
        {
            int depth = 0;
            byte previous = 0;
            int i = 0;

            while (i < src.Length)
            {
                byte b = src[i];
                if (b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
                {
                    i++;
                    continue;
                }

                if (b == (byte)'"')
                {
                    int start = i++;
                    while (i < src.Length)
                    {
                        int rel = src[i..].IndexOfAny(Utf8StringSpecials);
                        if (rel < 0)
                        {
                            i = src.Length;
                            break;
                        }
                        i += rel;
                        byte hit = src[i++];
                        if (hit == (byte)'"') break;
                        if (i < src.Length) i++;
                    }
                    AppendNativeSpan(ref buf, ref cap, ref len, src[start..i]);
                    previous = (byte)'"';
                    continue;
                }

                switch (b)
                {
                    case (byte)'{':
                    case (byte)'[':
                        depth++;
                        if (depth > MaxIndentDepth)
                        {
                            NativeMemory.Free(buf);
                            nativeOut = null;
                            outLen = 0;
                            error = $"Глубина вложенности JSON превышает {MaxIndentDepth}";
                            return false;
                        }
                        AppendNativeByte(ref buf, ref cap, ref len, b);
                        i++;
                        while (i < src.Length && src[i] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') i++;
                        if (i < src.Length && src[i] is not ((byte)'}' or (byte)']'))
                        {
                            AppendNativeSpan(ref buf, ref cap, ref len, newline);
                            AppendNativeIndent(ref buf, ref cap, ref len, depth * 2);
                        }
                        previous = b;
                        break;

                    case (byte)'}':
                    case (byte)']':
                        depth = Math.Max(0, depth - 1);
                        if (previous is not ((byte)'{' or (byte)'['))
                        {
                            AppendNativeSpan(ref buf, ref cap, ref len, newline);
                            AppendNativeIndent(ref buf, ref cap, ref len, depth * 2);
                        }
                        AppendNativeByte(ref buf, ref cap, ref len, b);
                        previous = b;
                        i++;
                        break;

                    case (byte)',':
                        AppendNativeByte(ref buf, ref cap, ref len, (byte)',');
                        AppendNativeSpan(ref buf, ref cap, ref len, newline);
                        AppendNativeIndent(ref buf, ref cap, ref len, depth * 2);
                        previous = (byte)',';
                        i++;
                        break;

                    case (byte)':':
                        AppendNativeSpan(ref buf, ref cap, ref len, ": "u8);
                        previous = (byte)':';
                        i++;
                        break;

                    default:
                    {
                        int rel = src[i..].IndexOfAny(Utf8NonStringDelims);
                        int runLen = rel < 0 ? src.Length - i : Math.Max(1, rel);
                        AppendNativeSpan(ref buf, ref cap, ref len, src.Slice(i, runLen));
                        previous = src[i + runLen - 1];
                        i += runLen;
                        break;
                    }
                }
            }

            if (!src.IsEmpty && src[^1] is (byte)'\r' or (byte)'\n')
            {
                AppendNativeSpan(ref buf, ref cap, ref len, newline);
            }

            nativeOut = buf;
            outLen = len;
            error = "";
            return true;
        }
        catch
        {
            NativeMemory.Free(buf);
            throw;
        }
    }

    private static void EnsureNativeCapacity(ref byte* buf, ref int cap, int needed)
    {
        if (needed <= cap) return;
        int nextCap = Math.Max(needed, cap * 2);
        buf = (byte*)NativeMemory.Realloc(buf, (nuint)nextCap);
        cap = nextCap;
    }

    private static void AppendNativeByte(ref byte* buf, ref int cap, ref int len, byte b)
    {
        EnsureNativeCapacity(ref buf, ref cap, len + 1);
        buf[len++] = b;
    }

    private static void AppendNativeSpan(ref byte* buf, ref int cap, ref int len, ReadOnlySpan<byte> span)
    {
        if (span.IsEmpty) return;
        EnsureNativeCapacity(ref buf, ref cap, len + span.Length);
        span.CopyTo(new Span<byte>(buf + len, span.Length));
        len += span.Length;
    }

    private static void AppendNativeIndent(ref byte* buf, ref int cap, ref int len, int spaces)
    {
        if (spaces <= 0) return;
        EnsureNativeCapacity(ref buf, ref cap, len + spaces);
        new Span<byte>(buf + len, spaces).Fill((byte)' ');
        len += spaces;
    }

    private static void AppendIndent(StringBuilder sb, int spaces)
    {
        while (spaces > 0)
        {
            int chunk = Math.Min(spaces, SpaceIndent256.Length);
            sb.Append(SpaceIndent256.AsSpan(0, chunk));
            spaces -= chunk;
        }
    }

    private static bool ValidateUtf8Json(ReadOnlySpan<byte> utf8, out string error)
    {
        var reader = new Utf8JsonReader(utf8, new JsonReaderOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = MaxIndentDepth + 16,
        });

        try
        {
            bool sawToken = false;
            int depth = 0;
            while (reader.Read())
            {
                sawToken = true;
                if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                {
                    depth++;
                    if (depth > MaxIndentDepth)
                    {
                        error = $"Глубина вложенности JSON превышает {MaxIndentDepth}";
                        return false;
                    }
                }
                else if (reader.TokenType is JsonTokenType.EndObject or JsonTokenType.EndArray)
                {
                    depth--;
                }
                else if (reader.TokenType is JsonTokenType.String or JsonTokenType.PropertyName && reader.ValueIsEscaped)
                {
                    ValidateEscapedString(ref reader);
                }
            }

            if (!sawToken)
            {
                error = "Ошибка JSON, строка 1, столбец 1: пустой ввод";
                return false;
            }

            error = "";
            return true;
        }
        catch (JsonException ex)
        {
            var (line, col) = ComputeLineColumn(utf8, ex.BytePositionInLine, ex.LineNumber);
            error = $"Ошибка JSON, строка {Math.Max(1, line)}, столбец {Math.Max(1, col)}: {ex.Message}";
            return false;
        }
        catch (InvalidOperationException ex)
        {
            var (line, col) = ComputeLineColumnFromOffset(utf8, reader.TokenStartIndex);
            error = $"Ошибка JSON, строка {Math.Max(1, line)}, столбец {Math.Max(1, col)}: {ex.Message}";
            return false;
        }
    }

    private static void ValidateEscapedString(ref Utf8JsonReader reader)
    {
        int len = reader.HasValueSequence ? (int)reader.ValueSequence.Length : reader.ValueSpan.Length;
        byte[]? rented = len > 512 ? ArrayPool<byte>.Shared.Rent(len) : null;
        Span<byte> buf = rented is not null ? rented.AsSpan(0, len) : stackalloc byte[512];
        try
        {
            _ = reader.CopyString(buf);
        }
        finally
        {
            if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static (long Line, long Column) ComputeLineColumnFromOffset(ReadOnlySpan<byte> utf8, long offset)
    {
        long line = 1;
        long col = 1;
        long limit = Math.Clamp(offset, 0, utf8.Length);
        for (int i = 0; i < limit; i++)
        {
            if (utf8[i] == (byte)'\n')
            {
                line++;
                col = 1;
            }
            else if ((utf8[i] & 0xC0) != 0x80)
            {
                col++;
            }
        }
        return (line, col);
    }

    private static (long Line, long Column) ComputeLineColumn(ReadOnlySpan<byte> utf8, long? bytePosInLine, long? lineZeroBased)
    {
        long line = (lineZeroBased ?? 0) + 1;
        long col = (bytePosInLine ?? 0) + 1;
        if (lineZeroBased is null || bytePosInLine is null)
        {
            return (1, 1);
        }

        long currentLine = 0;
        int lineStart = 0;
        for (int i = 0; i < utf8.Length && currentLine < lineZeroBased.Value; i++)
        {
            if (utf8[i] == (byte)'\n')
            {
                currentLine++;
                lineStart = i + 1;
            }
        }

        int targetEnd = (int)Math.Min(utf8.Length, lineStart + Math.Max(0, bytePosInLine.Value));
        int charCol = 1;
        for (int i = lineStart; i < targetEnd; i++)
        {
            if ((utf8[i] & 0xC0) != 0x80)
            {
                charCol++;
            }
        }

        return (line, Math.Max(col, charCol));
    }

    private static bool IsAsciiWhitespace(char ch) => ch is ' ' or '\t' or '\r' or '\n';
}
