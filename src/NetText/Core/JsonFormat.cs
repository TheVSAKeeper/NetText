using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace NetText.Core;

public static unsafe class JsonFormat
{
    public const int MaxIndentDepth = 1024;
    internal const int ValidationLimit = 64 * 1024 * 1024;
    private const int MaxFastUtf16Chars = ValidationLimit / 3;
    private const int MaxCachedUtf16Chars = 2 * 1024 * 1024;
    private static readonly string CommaCrLfSpaces = ",\r\n" + new string(' ', 64);
    private static readonly string CommaLfSpaces = ",\n" + new string(' ', 64);
    private static nuint s_cachedUtf16Raw;
    private sealed class OutputLimitException : Exception;

    public static bool IsJsonLinesPath(string? path)
    {
        var ext = Path.GetExtension(path.AsSpan());
        return ext.Equals(".jsonl", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".ndjson", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsJsonPath(string? path) =>
        Path.GetExtension(path.AsSpan()).Equals(".json", StringComparison.OrdinalIgnoreCase)
        || IsJsonLinesPath(path);

    public static bool IsJsonContainerPrefix(ReadOnlySpan<char> record)
    {
        record = TrimLeadingUtf16BomAndWhitespace(record);
        if (record.IsEmpty) return false;
        char opener = record[0];
        if (opener is not ('{' or '[')) return false;

        var value = record[1..].TrimStart();
        if (value.IsEmpty) return true;
        return opener == '{'
            ? value[0] is '"' or '}'
            : value[0] is '"' or '{' or '[' or ']' or '-' or (>= '0' and <= '9') or 't' or 'f' or 'n';
    }

    public static bool IsJsonContainerPrefix(ReadOnlySpan<byte> record)
    {
        record = TrimLeadingUtf8BomAndWhitespace(record);
        if (record.IsEmpty) return false;
        byte opener = record[0];
        if (opener is not ((byte)'{' or (byte)'[')) return false;

        var value = TrimStartAsciiWhitespace(record[1..]);
        if (value.IsEmpty) return true;
        return opener == (byte)'{'
            ? value[0] is (byte)'"' or (byte)'}'
            : value[0] is (byte)'"' or (byte)'{' or (byte)'[' or (byte)']' or (byte)'-'
                or (>= (byte)'0' and <= (byte)'9') or (byte)'t' or (byte)'f' or (byte)'n';
    }

    public static bool UsesJsonLines(string? path, ReadOnlySpan<byte> utf8)
    {
        if (IsJsonLinesPath(path)) return true;
        if (Path.GetExtension(path.AsSpan()).Equals(".json", StringComparison.OrdinalIgnoreCase)) return false;

        var input = StripUtf8Bom(utf8);
        int consumed = ReadUtf8Line(input, out var first);
        var trimmed = TrimAsciiWhitespace(first);
        if (trimmed.Length < 2) return false;
        if (trimmed[0] is not ((byte)'{' or (byte)'[')) return false;
        if (trimmed[^1] is not ((byte)'}' or (byte)']')) return false;
        if (TrimStartAsciiWhitespace(input[consumed..]).IsEmpty) return false;
        if (first.Length > ValidationLimit) return false;
        return ValidateUtf8Json(first, out _, out _, out _);
    }

    public static bool UsesJsonLines(string? path, ReadOnlySpan<char> text)
    {
        if (IsJsonLinesPath(path)) return true;
        if (Path.GetExtension(path.AsSpan()).Equals(".json", StringComparison.OrdinalIgnoreCase)) return false;

        var input = text.TrimStart('\uFEFF');
        int consumed = ReadUtf16Line(input, out var first);
        var trimmed = first.Trim();
        if (trimmed.Length < 2) return false;
        if (trimmed[0] is not ('{' or '[')) return false;
        if (trimmed[^1] is not ('}' or ']')) return false;
        if (input[consumed..].IsWhiteSpace()) return false;
        int utf8Bytes = Encoding.UTF8.GetByteCount(first);
        if (utf8Bytes > ValidationLimit) return false;

        byte[]? rented = utf8Bytes > 1024 ? ArrayPool<byte>.Shared.Rent(utf8Bytes) : null;
        Span<byte> buf = rented is not null ? rented.AsSpan(0, utf8Bytes) : stackalloc byte[1024];
        try
        {
            int written = Encoding.UTF8.GetBytes(first, buf);
            return ValidateUtf8Json(buf[..written], out _, out _, out _);
        }
        finally
        {
            if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public static bool LooksLikeJson(string? path, ReadOnlySpan<byte> utf8)
    {
        if (IsJsonPath(path)) return true;
        if (!IsLogFilePath(path))
        {
            var trimmed = TrimLeadingUtf8BomAndWhitespace(utf8);
            return !trimmed.IsEmpty && trimmed[0] is (byte)'{' or (byte)'[';
        }

        var input = StripUtf8Bom(utf8);
        if (!UsesJsonLines(path, input)) return false;
        int consumed = ReadUtf8Line(input, out _);
        _ = ReadUtf8Line(input[consumed..], out var nextRecord);
        return IsJsonContainerPrefix(nextRecord);
    }

    internal static bool TryValidate(ReadOnlySpan<byte> source, out string error, out int position) =>
        ValidateUtf8Json(source, out error, out position, out _);

    public static bool TryValidate(
        ReadOnlySpan<byte> utf8,
        string? path,
        out string error,
        out int offset,
        out int lineNumber)
    {
        int bom = utf8.StartsWith("\uFEFF"u8) ? 3 : 0;
        var input = utf8[bom..];
        if (input.Length > ValidationLimit)
        {
            error = "Проверка JSON ограничена документами до 64 МиБ.";
            offset = 0;
            lineNumber = 1;
            return false;
        }

        if (!UsesJsonLines(path, input))
        {
            bool valid = ValidateUtf8Json(input, out error, out int pos, out int errLine);
            offset = pos >= 0 ? pos + bom : -1;
            lineNumber = errLine;
            return valid;
        }

        int start = 0;
        for (int line = 1; !input.IsEmpty; line++)
        {
            int consumed = ReadUtf8Line(input, out var record);
            if (!ValidateUtf8Json(record, out error, out int pos, out int relLine, line))
            {
                offset = bom + start + Math.Max(0, pos);
                lineNumber = relLine;
                return false;
            }
            input = input[consumed..];
            start += consumed;
        }

        error = "";
        offset = -1;
        lineNumber = 1;
        return true;
    }

    public static bool TryFormat(string source, string newline, out string formatted, out string error)
    {
        if (source.Length <= MaxFastUtf16Chars && IsFastUtf16Newline(newline)
            && TryFormatUtf16Fast(source, newline, out formatted))
        {
            error = "";
            return true;
        }

        int exactUtf8 = Encoding.UTF8.GetByteCount(source);
        if (exactUtf8 > ValidationLimit)
        {
            formatted = "";
            error = "Форматирование JSON ограничено входом до 64 МиБ.";
            return false;
        }

        Span<byte> nlStack = stackalloc byte[16];
        ReadOnlySpan<byte> nlSpan = newline switch
        {
            "\n" => "\n"u8,
            "\r\n" => "\r\n"u8,
            _ when Encoding.UTF8.GetMaxByteCount(newline.Length) <= 16 => nlStack[..Encoding.UTF8.GetBytes(newline, nlStack)],
            _ => Encoding.UTF8.GetBytes(newline),
        };

        byte* unmanagedUtf8 = exactUtf8 > 262_144 ? (byte*)NativeMemory.Alloc((nuint)exactUtf8) : null;
        byte[]? rentedUtf8 = unmanagedUtf8 == null ? ArrayPool<byte>.Shared.Rent(Math.Max(1, exactUtf8)) : null;
        byte* formattedPtr = null;
        try
        {
            Span<byte> utf8Span = unmanagedUtf8 != null
                ? new Span<byte>(unmanagedUtf8, exactUtf8)
                : rentedUtf8.AsSpan(0, exactUtf8);
            Encoding.UTF8.GetBytes(source.AsSpan(), utf8Span);

            if (!TryFormatUtf8Native(utf8Span, nlSpan, out formattedPtr, out int formattedLen, out error))
            {
                formatted = "";
                return false;
            }

            formatted = formattedLen == 0 ? "" : Encoding.UTF8.GetString(new ReadOnlySpan<byte>(formattedPtr, formattedLen));
            return true;
        }
        finally
        {
            if (formattedPtr != null) NativeMemory.Free(formattedPtr);
            if (unmanagedUtf8 != null) NativeMemory.Free(unmanagedUtf8);
            if (rentedUtf8 != null) ArrayPool<byte>.Shared.Return(rentedUtf8);
        }
    }

    public static bool TryFormat(
        string text,
        string? path,
        string? containingText,
        string newline,
        out string formatted,
        out string error)
    {
        bool jsonLines = UsesJsonLines(path, (containingText ?? text).AsSpan());
        if (!jsonLines)
        {
            var trimmed = text.TrimStart('\uFEFF');
            return TryFormat(trimmed, newline, out formatted, out error);
        }

        int utf8Len = Encoding.UTF8.GetByteCount(text);
        byte[] rented = ArrayPool<byte>.Shared.Rent(Math.Max(1, utf8Len));
        byte[] nlBytes = Encoding.UTF8.GetBytes(newline);
        try
        {
            int actual = Encoding.UTF8.GetBytes(text.AsSpan(), rented);
            if (!TryFormatUtf8Native(rented.AsSpan(0, actual), nlBytes, jsonLines: true, out byte* ptr, out int outLen, out error))
            {
                formatted = "";
                return false;
            }
            try
            {
                formatted = outLen == 0 ? "" : Encoding.UTF8.GetString(new ReadOnlySpan<byte>(ptr, outLen));
                return true;
            }
            finally
            {
                if (ptr is not null) NativeMemory.Free(ptr);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

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
        ReadOnlySpan<byte> source,
        byte[] newline,
        bool jsonLines,
        out byte* formattedPtr,
        out int formattedLen,
        out string error) =>
        TryFormatUtf8Native(source, newline.AsSpan(), jsonLines, out formattedPtr, out formattedLen, out error);

    internal static bool TryFormatUtf8Native(
        ReadOnlySpan<byte> source,
        ReadOnlySpan<byte> newline,
        bool jsonLines,
        out byte* formattedPtr,
        out int formattedLen,
        out string error)
    {
        if (!jsonLines)
        {
            return TryFormatUtf8Native(source, newline, out formattedPtr, out formattedLen, out error);
        }

        var src = StripUtf8Bom(source);
        if (src.Length > ValidationLimit)
        {
            formattedPtr = null;
            formattedLen = 0;
            error = "Форматирование JSON ограничено входом до 64 МиБ.";
            return false;
        }

        if (src.IsEmpty)
        {
            formattedPtr = null;
            formattedLen = 0;
            error = "";
            return true;
        }

        int cap = Math.Min(ValidationLimit, Math.Max(256, src.Length + 64));
        byte* buf = (byte*)NativeMemory.Alloc((nuint)cap);
        int len = 0;

        try
        {
            var remaining = src;
            int lineNum = 1;
            while (!remaining.IsEmpty)
            {
                int consumed = ReadUtf8Line(remaining, out var record);
                if (!WriteCompactRecord(record, lineNum, ref buf, ref cap, ref len, out error))
                {
                    NativeMemory.Free(buf);
                    formattedPtr = null;
                    formattedLen = 0;
                    return false;
                }

                var sep = remaining[record.Length..consumed];
                if (!sep.IsEmpty)
                {
                    AppendNativeSpan(ref buf, ref cap, ref len, newline.IsEmpty ? sep : newline);
                }

                remaining = remaining[consumed..];
                lineNum++;
            }

            formattedPtr = buf;
            formattedLen = len;
            error = "";
            return true;
        }
        catch (OutputLimitException)
        {
            NativeMemory.Free(buf);
            formattedPtr = null;
            formattedLen = 0;
            error = "Форматированный JSON превышает 64 МиБ.";
            return false;
        }
        catch
        {
            NativeMemory.Free(buf);
            throw;
        }
    }

    private const byte ContainerEmpty = 0;
    private const byte ContainerHasItems = 1;
    private const byte ContainerAfterProperty = 2;

    internal static bool TryFormatUtf8Native(
        ReadOnlySpan<byte> src,
        ReadOnlySpan<byte> newline,
        out byte* nativeOut,
        out int outLen,
        out string error)
    {
        if (src.Length > ValidationLimit)
        {
            nativeOut = null;
            outLen = 0;
            error = "Форматирование JSON ограничено входом до 64 МиБ.";
            return false;
        }

        int cap = (int)Math.Min((long)ValidationLimit, Math.Max(256L, (long)src.Length * 2 + 64));
        byte* buf = (byte*)NativeMemory.Alloc((nuint)cap);
        int len = 0;

        var reader = new Utf8JsonReader(src, new JsonReaderOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = MaxIndentDepth + 16,
        });

        try
        {
            Span<byte> state = stackalloc byte[MaxIndentDepth];
            int depth = 0;
            bool sawToken = false;

            while (reader.Read())
            {
                sawToken = true;
                JsonTokenType token = reader.TokenType;

                switch (token)
                {
                    case JsonTokenType.StartObject:
                    case JsonTokenType.StartArray:
                    {
                        if (depth > 0)
                        {
                            byte parentState = state[depth - 1];
                            if (parentState != ContainerAfterProperty)
                            {
                                AppendBeforeItem(ref buf, ref cap, ref len, parentState == ContainerHasItems, newline, depth * 2);
                            }
                            state[depth - 1] = ContainerHasItems;
                        }
                        if (depth >= MaxIndentDepth)
                        {
                            NativeMemory.Free(buf);
                            nativeOut = null;
                            outLen = 0;
                            error = $"Глубина вложенности JSON превышает {MaxIndentDepth}";
                            return false;
                        }
                        state[depth++] = ContainerEmpty;
                        AppendNativeByte(ref buf, ref cap, ref len, token == JsonTokenType.StartObject ? (byte)'{' : (byte)'[');
                        break;
                    }

                    case JsonTokenType.EndObject:
                    case JsonTokenType.EndArray:
                    {
                        byte childState = state[--depth];
                        if (childState != ContainerEmpty)
                        {
                            AppendBeforeItem(ref buf, ref cap, ref len, comma: false, newline, depth * 2);
                        }
                        AppendNativeByte(ref buf, ref cap, ref len, token == JsonTokenType.EndObject ? (byte)'}' : (byte)']');
                        break;
                    }

                    case JsonTokenType.PropertyName:
                    {
                        byte parentState = state[depth - 1];
                        AppendBeforeItem(ref buf, ref cap, ref len, parentState == ContainerHasItems, newline, depth * 2);
                        if (reader.ValueIsEscaped)
                        {
                            ValidateEscapedString(ref reader);
                        }
                        ReadOnlySpan<byte> prop = reader.ValueSpan;
                        EnsureNativeCapacity(ref buf, ref cap, len + prop.Length + 4);
                        buf[len++] = (byte)'"';
                        prop.CopyTo(new Span<byte>(buf + len, prop.Length));
                        len += prop.Length;
                        buf[len++] = (byte)'"';
                        buf[len++] = (byte)':';
                        buf[len++] = (byte)' ';
                        state[depth - 1] = ContainerAfterProperty;
                        break;
                    }

                    case JsonTokenType.String:
                    {
                        if (depth > 0)
                        {
                            byte parentState = state[depth - 1];
                            if (parentState != ContainerAfterProperty)
                            {
                                AppendBeforeItem(ref buf, ref cap, ref len, parentState == ContainerHasItems, newline, depth * 2);
                            }
                            state[depth - 1] = ContainerHasItems;
                        }
                        if (reader.ValueIsEscaped)
                        {
                            ValidateEscapedString(ref reader);
                        }
                        ReadOnlySpan<byte> val = reader.ValueSpan;
                        EnsureNativeCapacity(ref buf, ref cap, len + val.Length + 2);
                        buf[len++] = (byte)'"';
                        val.CopyTo(new Span<byte>(buf + len, val.Length));
                        len += val.Length;
                        buf[len++] = (byte)'"';
                        break;
                    }

                    case JsonTokenType.Number:
                    {
                        if (depth > 0)
                        {
                            byte parentState = state[depth - 1];
                            if (parentState != ContainerAfterProperty)
                            {
                                AppendBeforeItem(ref buf, ref cap, ref len, parentState == ContainerHasItems, newline, depth * 2);
                            }
                            state[depth - 1] = ContainerHasItems;
                        }
                        AppendNativeSpan(ref buf, ref cap, ref len, reader.ValueSpan);
                        break;
                    }

                    case JsonTokenType.True:
                    case JsonTokenType.False:
                    case JsonTokenType.Null:
                    {
                        if (depth > 0)
                        {
                            byte parentState = state[depth - 1];
                            if (parentState != ContainerAfterProperty)
                            {
                                AppendBeforeItem(ref buf, ref cap, ref len, parentState == ContainerHasItems, newline, depth * 2);
                            }
                            state[depth - 1] = ContainerHasItems;
                        }
                        ReadOnlySpan<byte> lit = token switch
                        {
                            JsonTokenType.True => "true"u8,
                            JsonTokenType.False => "false"u8,
                            _ => "null"u8,
                        };
                        AppendNativeSpan(ref buf, ref cap, ref len, lit);
                        break;
                    }
                }
            }

            if (!sawToken)
            {
                NativeMemory.Free(buf);
                nativeOut = null;
                outLen = 0;
                error = "Ошибка JSON, строка 1, столбец 1: пустой ввод";
                return false;
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
        catch (JsonException ex)
        {
            NativeMemory.Free(buf);
            nativeOut = null;
            outLen = 0;
            (_, _, error) = FormatJsonError(src, ex);
            return false;
        }
        catch (InvalidOperationException ex)
        {
            NativeMemory.Free(buf);
            nativeOut = null;
            outLen = 0;
            (_, _, error) = FormatJsonError(src, reader.TokenStartIndex, ex);
            return false;
        }
        catch (OutputLimitException)
        {
            NativeMemory.Free(buf);
            nativeOut = null;
            outLen = 0;
            error = "Форматированный JSON превышает 64 МиБ.";
            return false;
        }
        catch
        {
            NativeMemory.Free(buf);
            throw;
        }
    }

    private static bool WriteCompactRecord(
        ReadOnlySpan<byte> src,
        int lineNum,
        ref byte* buf,
        ref int cap,
        ref int len,
        out string error)
    {
        var reader = new Utf8JsonReader(src, new JsonReaderOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = MaxIndentDepth + 16,
        });

        try
        {
            Span<byte> state = stackalloc byte[MaxIndentDepth];
            int depth = 0;
            bool sawToken = false;

            while (reader.Read())
            {
                sawToken = true;
                JsonTokenType token = reader.TokenType;

                switch (token)
                {
                    case JsonTokenType.StartObject:
                    case JsonTokenType.StartArray:
                    {
                        if (depth > 0)
                        {
                            if (state[depth - 1] == ContainerHasItems)
                            {
                                AppendNativeByte(ref buf, ref cap, ref len, (byte)',');
                            }
                            state[depth - 1] = ContainerHasItems;
                        }
                        if (depth >= MaxIndentDepth)
                        {
                            error = $"Глубина вложенности JSON превышает {MaxIndentDepth}";
                            return false;
                        }
                        state[depth++] = ContainerEmpty;
                        AppendNativeByte(ref buf, ref cap, ref len, token == JsonTokenType.StartObject ? (byte)'{' : (byte)'[');
                        break;
                    }

                    case JsonTokenType.EndObject:
                    case JsonTokenType.EndArray:
                    {
                        depth--;
                        AppendNativeByte(ref buf, ref cap, ref len, token == JsonTokenType.EndObject ? (byte)'}' : (byte)']');
                        break;
                    }

                    case JsonTokenType.PropertyName:
                    {
                        bool needComma = state[depth - 1] == ContainerHasItems;
                        if (reader.ValueIsEscaped)
                        {
                            ValidateEscapedString(ref reader);
                        }
                        ReadOnlySpan<byte> prop = reader.ValueSpan;
                        EnsureNativeCapacity(ref buf, ref cap, len + prop.Length + (needComma ? 4 : 3));
                        if (needComma)
                        {
                            buf[len++] = (byte)',';
                        }
                        buf[len++] = (byte)'"';
                        prop.CopyTo(new Span<byte>(buf + len, prop.Length));
                        len += prop.Length;
                        buf[len++] = (byte)'"';
                        buf[len++] = (byte)':';
                        state[depth - 1] = ContainerAfterProperty;
                        break;
                    }

                    case JsonTokenType.String:
                    {
                        bool needComma = false;
                        if (depth > 0)
                        {
                            needComma = state[depth - 1] == ContainerHasItems;
                            state[depth - 1] = ContainerHasItems;
                        }
                        if (reader.ValueIsEscaped)
                        {
                            ValidateEscapedString(ref reader);
                        }
                        ReadOnlySpan<byte> val = reader.ValueSpan;
                        EnsureNativeCapacity(ref buf, ref cap, len + val.Length + (needComma ? 3 : 2));
                        if (needComma)
                        {
                            buf[len++] = (byte)',';
                        }
                        buf[len++] = (byte)'"';
                        val.CopyTo(new Span<byte>(buf + len, val.Length));
                        len += val.Length;
                        buf[len++] = (byte)'"';
                        break;
                    }

                    case JsonTokenType.Number:
                    {
                        bool needComma = false;
                        if (depth > 0)
                        {
                            needComma = state[depth - 1] == ContainerHasItems;
                            state[depth - 1] = ContainerHasItems;
                        }
                        ReadOnlySpan<byte> val = reader.ValueSpan;
                        EnsureNativeCapacity(ref buf, ref cap, len + val.Length + (needComma ? 1 : 0));
                        if (needComma)
                        {
                            buf[len++] = (byte)',';
                        }
                        val.CopyTo(new Span<byte>(buf + len, val.Length));
                        len += val.Length;
                        break;
                    }

                    case JsonTokenType.True:
                    case JsonTokenType.False:
                    case JsonTokenType.Null:
                    {
                        bool needComma = false;
                        if (depth > 0)
                        {
                            needComma = state[depth - 1] == ContainerHasItems;
                            state[depth - 1] = ContainerHasItems;
                        }
                        ReadOnlySpan<byte> lit = token switch
                        {
                            JsonTokenType.True => "true"u8,
                            JsonTokenType.False => "false"u8,
                            _ => "null"u8,
                        };
                        EnsureNativeCapacity(ref buf, ref cap, len + lit.Length + (needComma ? 1 : 0));
                        if (needComma)
                        {
                            buf[len++] = (byte)',';
                        }
                        lit.CopyTo(new Span<byte>(buf + len, lit.Length));
                        len += lit.Length;
                        break;
                    }
                }
            }

            if (!sawToken)
            {
                error = $"Ошибка JSON, строка {lineNum}, столбец 1: пустой ввод";
                return false;
            }

            error = "";
            return true;
        }
        catch (JsonException ex)
        {
            (_, _, error) = FormatJsonError(src, ex, lineNum);
            return false;
        }
        catch (InvalidOperationException ex)
        {
            (_, _, error) = FormatJsonError(src, reader.TokenStartIndex, ex, lineNum);
            return false;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void EnsureNativeCapacity(ref byte* buf, ref int cap, int needed)
    {
        if (needed <= cap) return;
        GrowNativeCapacity(ref buf, ref cap, needed);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void GrowNativeCapacity(ref byte* buf, ref int cap, int needed)
    {
        if (needed > ValidationLimit) throw new OutputLimitException();
        int nextCap = Math.Min(ValidationLimit, Math.Max(needed, cap * 2));
        buf = (byte*)NativeMemory.Realloc(buf, (nuint)nextCap);
        cap = nextCap;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AppendNativeByte(ref byte* buf, ref int cap, ref int len, byte b)
    {
        EnsureNativeCapacity(ref buf, ref cap, len + 1);
        buf[len++] = b;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AppendNativeSpan(ref byte* buf, ref int cap, ref int len, ReadOnlySpan<byte> span)
    {
        if (span.IsEmpty) return;
        EnsureNativeCapacity(ref buf, ref cap, len + span.Length);
        span.CopyTo(new Span<byte>(buf + len, span.Length));
        len += span.Length;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AppendBeforeItem(
        ref byte* buf,
        ref int cap,
        ref int len,
        bool comma,
        ReadOnlySpan<byte> newline,
        int spaces)
    {
        int extra = (comma ? 1 : 0) + newline.Length + spaces;
        EnsureNativeCapacity(ref buf, ref cap, len + extra);
        if (comma)
        {
            buf[len++] = (byte)',';
        }
        if (!newline.IsEmpty)
        {
            newline.CopyTo(new Span<byte>(buf + len, newline.Length));
            len += newline.Length;
        }
        if (spaces > 0)
        {
            new Span<byte>(buf + len, spaces).Fill((byte)' ');
            len += spaces;
        }
    }

    private const byte StackObject = 0;
    private const byte StackArray = 1;

    private const byte StateExpectValue = 0;
    private const byte StateExpectArrayValueOrEnd = 1;
    private const byte StateExpectPropertyOrEnd = 2;
    private const byte StateExpectProperty = 3;
    private const byte StateExpectColon = 4;
    private const byte StateExpectCommaOrEnd = 5;
    private const byte StateDone = 6;

    private static bool IsFastUtf16Newline(string newline)
    {
        if (newline is "\r\n" or "\n") return true;
        if (newline.Length > 8) return false;
        for (int i = 0; i < newline.Length; i++)
        {
            char c = newline[i];
            if (!char.IsSurrogate(c)) continue;
            if (char.IsHighSurrogate(c) && i + 1 < newline.Length && char.IsLowSurrogate(newline[i + 1]))
            {
                i++;
                continue;
            }
            return false;
        }
        return true;
    }

    private static bool TryFormatUtf16Fast(string source, string newline, out string formatted)
    {
        formatted = "";
        int srcLen = source.Length;
        if (srcLen == 0) return false;

        bool isCrLf = newline == "\r\n";
        bool isLf = !isCrLf && newline == "\n";

        int cap = Math.Min(MaxFastUtf16Chars, Math.Max(256, srcLen * 2 + 64));
        char* buf = RentUtf16Buffer(cap, out cap);
        int outLen = 0;

        try
        {
            byte* stack = stackalloc byte[MaxIndentDepth];
            int depth = 0;
            byte state = StateExpectValue;

            fixed (char* pSrc = source)
            {
                int pos = 0;
                while (pos < srcLen)
                {
                    while (pos < srcLen && IsJsonWhitespace(pSrc[pos])) pos++;
                    if (pos >= srcLen) break;

                    char ch = pSrc[pos];
                    switch (state)
                    {
                        case StateExpectValue:
                        case StateExpectArrayValueOrEnd:
                        {
                            if (state == StateExpectArrayValueOrEnd && ch == ']')
                            {
                                pos++;
                                depth--;
                                if (!EnsureUtf16Capacity(ref buf, ref cap, outLen + 1)) return false;
                                buf[outLen++] = ']';
                                state = depth == 0 ? StateDone : StateExpectCommaOrEnd;
                                continue;
                            }

                            if (ch is '{' or '[')
                            {
                                if (depth >= MaxIndentDepth) return false;
                                if (state == StateExpectArrayValueOrEnd)
                                {
                                    if (!TryAppendBeforeItem(ref buf, ref cap, ref outLen, comma: false, newline, isCrLf, isLf, depth * 2)) return false;
                                }
                                else if (depth > 0 && stack[depth - 1] == StackArray)
                                {
                                    if (!TryAppendBeforeItem(ref buf, ref cap, ref outLen, comma: true, newline, isCrLf, isLf, depth * 2)) return false;
                                }

                                bool isObj = ch == '{';
                                stack[depth++] = isObj ? StackObject : StackArray;
                                if (!EnsureUtf16Capacity(ref buf, ref cap, outLen + 1)) return false;
                                buf[outLen++] = ch;
                                pos++;
                                state = isObj ? StateExpectPropertyOrEnd : StateExpectArrayValueOrEnd;
                                continue;
                            }

                            int tokStart = pos;
                            int tokEnd;
                            if (ch == '"')
                            {
                                if (!TryScanJsonString(pSrc, srcLen, pos, out tokEnd)) return false;
                            }
                            else if (ch == '-' || (uint)(ch - '0') <= 9u)
                            {
                                if (!TryScanJsonNumber(pSrc, srcLen, pos, out tokEnd)) return false;
                            }
                            else if (ch == 't' && pos + 4 <= srcLen && pSrc[pos + 1] == 'r' && pSrc[pos + 2] == 'u' && pSrc[pos + 3] == 'e')
                            {
                                tokEnd = pos + 4;
                            }
                            else if (ch == 'f' && pos + 5 <= srcLen && pSrc[pos + 1] == 'a' && pSrc[pos + 2] == 'l' && pSrc[pos + 3] == 's' && pSrc[pos + 4] == 'e')
                            {
                                tokEnd = pos + 5;
                            }
                            else if (ch == 'n' && pos + 4 <= srcLen && pSrc[pos + 1] == 'u' && pSrc[pos + 2] == 'l' && pSrc[pos + 3] == 'l')
                            {
                                tokEnd = pos + 4;
                            }
                            else
                            {
                                return false;
                            }

                            if (state == StateExpectArrayValueOrEnd)
                            {
                                if (!TryAppendBeforeItem(ref buf, ref cap, ref outLen, comma: false, newline, isCrLf, isLf, depth * 2)) return false;
                            }
                            else if (depth > 0 && stack[depth - 1] == StackArray)
                            {
                                if (!TryAppendBeforeItem(ref buf, ref cap, ref outLen, comma: true, newline, isCrLf, isLf, depth * 2)) return false;
                            }

                            int tokLen = tokEnd - tokStart;
                            if (!EnsureUtf16Capacity(ref buf, ref cap, outLen + tokLen)) return false;
                            new ReadOnlySpan<char>(pSrc + tokStart, tokLen).CopyTo(new Span<char>(buf + outLen, tokLen));
                            outLen += tokLen;
                            pos = tokEnd;
                            state = depth == 0 ? StateDone : StateExpectCommaOrEnd;
                            continue;
                        }

                        case StateExpectPropertyOrEnd:
                        {
                            if (ch == '}')
                            {
                                pos++;
                                depth--;
                                if (!EnsureUtf16Capacity(ref buf, ref cap, outLen + 1)) return false;
                                buf[outLen++] = '}';
                                state = depth == 0 ? StateDone : StateExpectCommaOrEnd;
                                continue;
                            }
                            if (ch != '"' || !TryScanJsonString(pSrc, srcLen, pos, out int propEnd)) return false;
                            if (!TryAppendUtf16Property(ref buf, ref cap, ref outLen, pSrc, pos, propEnd - pos, comma: false, newline, isCrLf, isLf, depth * 2)) return false;
                            pos = propEnd;
                            if (pos < srcLen && pSrc[pos] == ':')
                            {
                                pos++;
                                state = StateExpectValue;
                            }
                            else
                            {
                                state = StateExpectColon;
                            }
                            continue;
                        }

                        case StateExpectProperty:
                        {
                            if (ch != '"' || !TryScanJsonString(pSrc, srcLen, pos, out int propEnd)) return false;
                            if (!TryAppendUtf16Property(ref buf, ref cap, ref outLen, pSrc, pos, propEnd - pos, comma: true, newline, isCrLf, isLf, depth * 2)) return false;
                            pos = propEnd;
                            if (pos < srcLen && pSrc[pos] == ':')
                            {
                                pos++;
                                state = StateExpectValue;
                            }
                            else
                            {
                                state = StateExpectColon;
                            }
                            continue;
                        }

                        case StateExpectColon:
                        {
                            if (ch != ':') return false;
                            pos++;
                            state = StateExpectValue;
                            continue;
                        }

                        case StateExpectCommaOrEnd:
                        {
                            if (ch == ',')
                            {
                                pos++;
                                state = stack[depth - 1] == StackObject ? StateExpectProperty : StateExpectValue;
                                continue;
                            }
                            if ((ch == '}' && stack[depth - 1] == StackObject)
                                || (ch == ']' && stack[depth - 1] == StackArray))
                            {
                                pos++;
                                depth--;
                                if (!TryAppendBeforeItem(ref buf, ref cap, ref outLen, comma: false, newline, isCrLf, isLf, depth * 2)) return false;
                                if (!EnsureUtf16Capacity(ref buf, ref cap, outLen + 1)) return false;
                                buf[outLen++] = ch;
                                state = depth == 0 ? StateDone : StateExpectCommaOrEnd;
                                continue;
                            }
                            return false;
                        }

                        default:
                            return false;
                    }
                }

                if (state != StateDone || depth != 0)
                {
                    return false;
                }

                if (pSrc[srcLen - 1] is '\r' or '\n')
                {
                    if (!EnsureUtf16Capacity(ref buf, ref cap, outLen + newline.Length)) return false;
                    if (newline.Length > 0)
                    {
                        newline.AsSpan().CopyTo(new Span<char>(buf + outLen, newline.Length));
                        outLen += newline.Length;
                    }
                }

                if (outLen > MaxFastUtf16Chars)
                {
                    return false;
                }

                formatted = new string(buf, 0, outLen);
                return true;
            }
        }
        finally
        {
            ReturnUtf16Buffer(buf, cap);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsJsonWhitespace(char c) => c is ' ' or '\t' or '\r' or '\n';

    private static char* RentUtf16Buffer(int minCap, out int actualCap)
    {
        nuint cached = Interlocked.Exchange(ref s_cachedUtf16Raw, 0);
        if (cached != 0)
        {
            byte* raw = (byte*)cached;
            int cap = *(int*)raw;
            if (cap >= minCap)
            {
                actualCap = cap;
                return (char*)(raw + 8);
            }
            NativeMemory.Free(raw);
        }

        byte* fresh = (byte*)NativeMemory.Alloc((nuint)minCap * sizeof(char) + 16);
        *(int*)fresh = minCap;
        actualCap = minCap;
        return (char*)(fresh + 8);
    }

    private static void ReturnUtf16Buffer(char* buf, int cap)
    {
        if (buf == null) return;
        byte* raw = (byte*)buf - 8;
        if (cap <= MaxCachedUtf16Chars && Interlocked.CompareExchange(ref s_cachedUtf16Raw, (nuint)raw, 0) == 0)
        {
            return;
        }
        NativeMemory.Free(raw);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool EnsureUtf16Capacity(ref char* buf, ref int cap, int needed) =>
        needed <= cap || TryGrowUtf16Buffer(ref buf, ref cap, needed);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool TryGrowUtf16Buffer(ref char* buf, ref int cap, int needed)
    {
        if (needed > MaxFastUtf16Chars) return false;
        int nextCap = Math.Min(MaxFastUtf16Chars, Math.Max(needed, cap * 2));
        byte* newRaw = (byte*)NativeMemory.Realloc((byte*)buf - 8, (nuint)nextCap * sizeof(char) + 16);
        *(int*)newRaw = nextCap;
        buf = (char*)(newRaw + 8);
        cap = nextCap;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryAppendBeforeItem(
        ref char* buf,
        ref int cap,
        ref int len,
        bool comma,
        string newline,
        bool isCrLf,
        bool isLf,
        int spaces)
    {
        int extra = (comma ? 1 : 0) + newline.Length + spaces;
        if (!EnsureUtf16Capacity(ref buf, ref cap, len + extra)) return false;

        if (spaces <= 64 && (isCrLf || isLf))
        {
            string template = isCrLf ? CommaCrLfSpaces : CommaLfSpaces;
            int offset = comma ? 0 : 1;
            template.AsSpan(offset, extra).CopyTo(new Span<char>(buf + len, extra));
            len += extra;
            return true;
        }

        int pos = len;
        if (comma)
        {
            buf[pos++] = ',';
        }
        if (newline.Length > 0)
        {
            newline.AsSpan().CopyTo(new Span<char>(buf + pos, newline.Length));
            pos += newline.Length;
        }
        if (spaces > 0)
        {
            new Span<char>(buf + pos, spaces).Fill(' ');
            pos += spaces;
        }
        len = pos;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryAppendUtf16Property(
        ref char* buf,
        ref int cap,
        ref int outLen,
        char* pSrc,
        int propStart,
        int propLen,
        bool comma,
        string newline,
        bool isCrLf,
        bool isLf,
        int spaces)
    {
        if (!TryAppendBeforeItem(ref buf, ref cap, ref outLen, comma, newline, isCrLf, isLf, spaces)) return false;
        if (!EnsureUtf16Capacity(ref buf, ref cap, outLen + propLen + 2)) return false;
        new ReadOnlySpan<char>(pSrc + propStart, propLen).CopyTo(new Span<char>(buf + outLen, propLen));
        outLen += propLen;
        buf[outLen++] = ':';
        buf[outLen++] = ' ';
        return true;
    }

    private static bool TryScanJsonString(char* pSrc, int srcLen, int startPos, out int endPos)
    {
        int i = startPos + 1;
        while (i < srcLen)
        {
            char c = pSrc[i];
            if ((uint)(c - 0x20) < 0xD7E0u)
            {
                if (c == '"')
                {
                    endPos = i + 1;
                    return true;
                }
                if (c == '\\')
                {
                    if (i + 1 >= srcLen)
                    {
                        endPos = 0;
                        return false;
                    }
                    char esc = pSrc[i + 1];
                    if (esc is '"' or '\\' or '/' or 'b' or 'f' or 'n' or 'r' or 't')
                    {
                        i += 2;
                        continue;
                    }
                    if (esc == 'u')
                    {
                        if (i + 6 > srcLen || !TryParseHex4(pSrc + i + 2, out ushort code1))
                        {
                            endPos = 0;
                            return false;
                        }
                        if (code1 is < 0xD800 or > 0xDFFF)
                        {
                            i += 6;
                            continue;
                        }
                        if (code1 <= 0xDBFF
                            && i + 12 <= srcLen
                            && pSrc[i + 6] == '\\'
                            && pSrc[i + 7] == 'u'
                            && TryParseHex4(pSrc + i + 8, out ushort code2)
                            && code2 is >= 0xDC00 and <= 0xDFFF)
                        {
                            i += 12;
                            continue;
                        }
                    }
                    endPos = 0;
                    return false;
                }
                i++;
                continue;
            }
            if (c > 0xDFFF)
            {
                i++;
                continue;
            }
            if (c < 0x20)
            {
                endPos = 0;
                return false;
            }
            if (c <= 0xDBFF && i + 1 < srcLen && (uint)(pSrc[i + 1] - 0xDC00) <= 0x03FFu)
            {
                i += 2;
                continue;
            }
            endPos = 0;
            return false;
        }

        endPos = 0;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryParseHex4(char* p, out ushort value)
    {
        uint acc = 0;
        for (int j = 0; j < 4; j++)
        {
            char h = p[j];
            uint d;
            if ((uint)(h - '0') <= 9u) d = (uint)(h - '0');
            else if ((uint)(h - 'a') <= 5u) d = (uint)(h - 'a' + 10);
            else if ((uint)(h - 'A') <= 5u) d = (uint)(h - 'A' + 10);
            else
            {
                value = 0;
                return false;
            }
            acc = (acc << 4) | d;
        }
        value = (ushort)acc;
        return true;
    }

    private static bool TryScanJsonNumber(char* pSrc, int srcLen, int startPos, out int endPos)
    {
        int i = startPos;
        if (pSrc[i] == '-')
        {
            i++;
            if (i >= srcLen)
            {
                endPos = 0;
                return false;
            }
        }

        char first = pSrc[i];
        if (first == '0')
        {
            i++;
            if (i < srcLen && (uint)(pSrc[i] - '0') <= 9u)
            {
                endPos = 0;
                return false;
            }
        }
        else if ((uint)(first - '1') <= 8u)
        {
            i++;
            while (i < srcLen && (uint)(pSrc[i] - '0') <= 9u) i++;
        }
        else
        {
            endPos = 0;
            return false;
        }

        if (i < srcLen && pSrc[i] == '.')
        {
            i++;
            if (i >= srcLen || (uint)(pSrc[i] - '0') > 9u)
            {
                endPos = 0;
                return false;
            }
            i++;
            while (i < srcLen && (uint)(pSrc[i] - '0') <= 9u) i++;
        }

        if (i < srcLen && (pSrc[i] is 'e' or 'E'))
        {
            i++;
            if (i < srcLen && (pSrc[i] is '+' or '-'))
            {
                i++;
            }
            if (i >= srcLen || (uint)(pSrc[i] - '0') > 9u)
            {
                endPos = 0;
                return false;
            }
            i++;
            while (i < srcLen && (uint)(pSrc[i] - '0') <= 9u) i++;
        }

        endPos = i;
        return true;
    }

    private static bool ValidateUtf8Json(
        ReadOnlySpan<byte> utf8,
        out string error,
        out int position,
        out int lineNumber,
        int baseLineNumber = 1)
    {
        position = -1;
        lineNumber = baseLineNumber;
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
                        position = (int)reader.TokenStartIndex;
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
                position = 0;
                lineNumber = baseLineNumber;
                error = $"Ошибка JSON, строка {baseLineNumber}, столбец 1: пустой ввод";
                return false;
            }

            error = "";
            return true;
        }
        catch (JsonException ex)
        {
            (position, lineNumber, error) = FormatJsonError(utf8, ex, baseLineNumber);
            return false;
        }
        catch (InvalidOperationException ex)
        {
            (position, lineNumber, error) = FormatJsonError(utf8, reader.TokenStartIndex, ex, baseLineNumber);
            return false;
        }
    }

    private static (int Offset, int LineNumber, string Error) FormatJsonError(
        ReadOnlySpan<byte> utf8,
        JsonException ex,
        int baseLineNumber = 1)
    {
        var (line, col, offset) = ComputeLineColumn(utf8, ex.BytePositionInLine, ex.LineNumber);
        long displayLine = baseLineNumber + Math.Max(1, line) - 1;
        return (offset, (int)displayLine, $"Ошибка JSON, строка {displayLine}, столбец {Math.Max(1, col)}: {ex.Message}");
    }

    private static (int Offset, int LineNumber, string Error) FormatJsonError(
        ReadOnlySpan<byte> utf8,
        long tokenStartIndex,
        InvalidOperationException ex,
        int baseLineNumber = 1)
    {
        var (line, col) = ComputeLineColumnFromOffset(utf8, tokenStartIndex);
        long displayLine = baseLineNumber + Math.Max(1, line) - 1;
        return ((int)tokenStartIndex, (int)displayLine, $"Ошибка JSON, строка {displayLine}, столбец {Math.Max(1, col)}: {ex.Message}");
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

    private static (long Line, long Column, int Offset) ComputeLineColumn(ReadOnlySpan<byte> utf8, long? bytePosInLine, long? lineZeroBased)
    {
        long line = (lineZeroBased ?? 0) + 1;
        if (lineZeroBased is null || bytePosInLine is null)
        {
            return (1, 1, 0);
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

        return (line, charCol, targetEnd);
    }

    private static bool IsLogFilePath(string? path) =>
        Path.GetExtension(path.AsSpan()).Equals(".log", StringComparison.OrdinalIgnoreCase);

    private static ReadOnlySpan<byte> StripUtf8Bom(ReadOnlySpan<byte> utf8) =>
        utf8.StartsWith("\uFEFF"u8) ? utf8[3..] : utf8;

    private static ReadOnlySpan<byte> TrimStartAsciiWhitespace(ReadOnlySpan<byte> utf8)
    {
        int i = 0;
        while (i < utf8.Length && utf8[i] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') i++;
        return utf8[i..];
    }

    private static ReadOnlySpan<byte> TrimAsciiWhitespace(ReadOnlySpan<byte> utf8)
    {
        int start = 0;
        while (start < utf8.Length && utf8[start] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') start++;
        int end = utf8.Length;
        while (end > start && utf8[end - 1] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') end--;
        return utf8[start..end];
    }

    private static ReadOnlySpan<byte> TrimLeadingUtf8BomAndWhitespace(ReadOnlySpan<byte> utf8) =>
        TrimStartAsciiWhitespace(StripUtf8Bom(TrimStartAsciiWhitespace(utf8)));

    private static ReadOnlySpan<char> TrimLeadingUtf16BomAndWhitespace(ReadOnlySpan<char> chars) =>
        chars.TrimStart().TrimStart('\uFEFF').TrimStart();

    private static int ReadUtf8Line(ReadOnlySpan<byte> input, out ReadOnlySpan<byte> record)
    {
        int sep = input.IndexOfAny((byte)'\r', (byte)'\n');
        if (sep < 0)
        {
            record = input;
            return input.Length;
        }

        record = input[..sep];
        int next = sep + 1;
        return sep + (input[sep] == (byte)'\r' && next < input.Length && input[next] == (byte)'\n' ? 2 : 1);
    }

    private static int ReadUtf16Line(ReadOnlySpan<char> input, out ReadOnlySpan<char> record)
    {
        int sep = input.IndexOfAny('\r', '\n');
        if (sep < 0)
        {
            record = input;
            return input.Length;
        }

        record = input[..sep];
        int next = sep + 1;
        return sep + (input[sep] == '\r' && next < input.Length && input[next] == '\n' ? 2 : 1);
    }
}
