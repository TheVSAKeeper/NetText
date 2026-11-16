using System.Buffers;
using System.IO;
using System.Text;
using System.Text.Unicode;

namespace NetText.Core;

public enum TextEncoding
{
    None = 0,
    Utf8 = 1,
    Utf8Bom = 2,
    Utf16Le = 3,
    Utf16Be = 4,
    Windows1251 = 5,
}

public enum LineEndings
{
    None = 0,
    Lf = 1,
    CrLf = 2,
    Cr = 3,
    Mixed = 4,
}

public readonly record struct TextFormat(TextEncoding Encoding, LineEndings Endings)
{
    private static readonly UTF8Encoding Utf8Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly UTF8Encoding Utf8Lossy = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
    private static readonly UnicodeEncoding Utf16LeStrict = new(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true);
    private static readonly UnicodeEncoding Utf16LeLossy = new(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: false);
    private static readonly UnicodeEncoding Utf16BeStrict = new(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true);
    private static readonly UnicodeEncoding Utf16BeLossy = new(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: false);
    private static readonly Lazy<Encoding> Win1251Strict = new(CreateWin1251Strict);
    private static readonly Lazy<Encoding> Win1251Lossy = new(CreateWin1251Lossy);

    public static TextFormat Default => new(TextEncoding.Utf8, LineEndings.Lf);

    public static int UnitSize(TextEncoding encoding) =>
        encoding is TextEncoding.Utf16Le or TextEncoding.Utf16Be ? 2 : 1;

    public static int MinimumFragmentBytes(TextEncoding encoding) =>
        encoding is TextEncoding.Utf8 or TextEncoding.Utf8Bom ? 4 : UnitSize(encoding);

    public static int BomLength(TextEncoding encoding) => encoding switch
    {
        TextEncoding.Utf8Bom => 3,
        TextEncoding.Utf16Le or TextEncoding.Utf16Be => 2,
        _ => 0,
    };

    public static ulong AlignOffset(ulong offset, TextEncoding encoding)
    {
        ulong unit = (ulong)UnitSize(encoding);
        return unit > 1 ? offset - (offset % unit) : offset;
    }

    public static string EncodingLabelOf(TextEncoding encoding) => encoding switch
    {
        TextEncoding.Utf8 => "UTF-8",
        TextEncoding.Utf8Bom => "UTF-8 BOM",
        TextEncoding.Utf16Le => "UTF-16 LE",
        TextEncoding.Utf16Be => "UTF-16 BE",
        TextEncoding.Windows1251 => "Windows-1251",
        _ => "UTF-8",
    };

    public static string DecodeLossy(ReadOnlySpan<byte> bytes, TextEncoding encoding, bool atFileStart)
    {
        bytes = StripOptionalBom(bytes, encoding, atFileStart);
        return GetLossyEncoding(encoding).GetString(bytes);
    }

    public static int DecodeLossyChars(ReadOnlySpan<byte> bytes, TextEncoding encoding, bool atFileStart, Span<char> destination)
    {
        bytes = StripOptionalBom(bytes, encoding, atFileStart);
        return GetLossyEncoding(encoding).GetChars(bytes, destination);
    }

    public static int GetMaxLossyCharCount(int byteCount, TextEncoding encoding) =>
        GetLossyEncoding(encoding).GetMaxCharCount(byteCount);

    private static ReadOnlySpan<byte> StripOptionalBom(ReadOnlySpan<byte> bytes, TextEncoding encoding, bool atFileStart)
    {
        if (!atFileStart) return bytes;
        int bom = BomLength(encoding);
        if (bom <= 0 || bytes.Length < bom) return bytes;
        bool matches = encoding switch
        {
            TextEncoding.Utf8Bom => bytes is [0xEF, 0xBB, 0xBF, ..],
            TextEncoding.Utf16Le => bytes is [0xFF, 0xFE, ..],
            TextEncoding.Utf16Be => bytes is [0xFE, 0xFF, ..],
            _ => false,
        };
        return matches ? bytes[bom..] : bytes;
    }

    private static Encoding GetLossyEncoding(TextEncoding encoding) => encoding switch
    {
        TextEncoding.Utf16Le => Utf16LeLossy,
        TextEncoding.Utf16Be => Utf16BeLossy,
        TextEncoding.Windows1251 => Win1251Lossy.Value,
        _ => Utf8Lossy,
    };

    public static TextEncoding DetectStreamEncoding(ReadOnlySpan<byte> prefix) => prefix switch
    {
        [0xEF, 0xBB, 0xBF, ..] => TextEncoding.Utf8Bom,
        [0xFF, 0xFE, ..] => TextEncoding.Utf16Le,
        [0xFE, 0xFF, ..] => TextEncoding.Utf16Be,
        _ => TextEncoding.Utf8,
    };

    public string EncodingLabel => Encoding switch
    {
        TextEncoding.Utf8 => "UTF-8",
        TextEncoding.Utf8Bom => "UTF-8 BOM",
        TextEncoding.Utf16Le => "UTF-16 LE",
        TextEncoding.Utf16Be => "UTF-16 BE",
        TextEncoding.Windows1251 => "Windows-1251",
        _ => "UTF-8",
    };

    public string LineEndingsLabel => Endings switch
    {
        LineEndings.Lf => "LF",
        LineEndings.CrLf => "CRLF",
        LineEndings.Cr => "CR",
        LineEndings.Mixed => "Смешанные",
        _ => "LF",
    };

    public static (byte[] Utf8Bytes, TextFormat Format) DecodeOwnedToUtf8(byte[] ownedBytes, bool windows1251)
    {
        if (!windows1251 &&
            ownedBytes is not [0xFF, 0xFE, ..] &&
            ownedBytes is not [0xFE, 0xFF, ..] &&
            ownedBytes is not [0xEF, 0xBB, 0xBF, ..])
        {
            if (!Utf8.IsValid(ownedBytes))
            {
                throw new InvalidDataException(
                    "Ожидается UTF-8 или UTF-16 с BOM. Для Windows-1251 выберите кодировку при открытии");
            }
            if (ownedBytes.AsSpan().IndexOf((byte)0) >= 0)
            {
                throw new InvalidDataException("Файл содержит нулевые символы");
            }
            var endings = DetectLineEndings(ownedBytes);
            if (endings == LineEndings.Cr)
            {
                ownedBytes.AsSpan().Replace((byte)'\r', (byte)'\n');
            }
            return (ownedBytes, new TextFormat(TextEncoding.Utf8, endings));
        }

        return DecodeToUtf8(ownedBytes, windows1251);
    }

    public static (byte[] Utf8Bytes, TextFormat Format) DecodeToUtf8(ReadOnlySpan<byte> bytes, bool windows1251)
    {
        TextEncoding encoding;
        byte[] utf8;

        if (windows1251)
        {
            encoding = TextEncoding.Windows1251;
            utf8 = TranscodeToUtf8(Win1251Strict.Value, bytes, "Некорректный текст Windows-1251");
        }
        else if (bytes is [0xFF, 0xFE, .. var leContent])
        {
            encoding = TextEncoding.Utf16Le;
            utf8 = TranscodeToUtf8(Utf16LeStrict, leContent, "Некорректный текст UTF-16");
        }
        else if (bytes is [0xFE, 0xFF, .. var beContent])
        {
            encoding = TextEncoding.Utf16Be;
            utf8 = TranscodeToUtf8(Utf16BeStrict, beContent, "Некорректный текст UTF-16");
        }
        else
        {
            ReadOnlySpan<byte> content;
            if (bytes is [0xEF, 0xBB, 0xBF, .. var bomContent])
            {
                encoding = TextEncoding.Utf8Bom;
                content = bomContent;
            }
            else
            {
                encoding = TextEncoding.Utf8;
                content = bytes;
            }

            if (!Utf8.IsValid(content))
            {
                throw new InvalidDataException(
                    "Ожидается UTF-8 или UTF-16 с BOM. Для Windows-1251 выберите кодировку при открытии");
            }

            utf8 = content.ToArray();
        }

        if (utf8.AsSpan().IndexOf((byte)0) >= 0)
        {
            throw new InvalidDataException("Файл содержит нулевые символы");
        }

        var endings = DetectLineEndings(utf8);
        if (endings == LineEndings.Cr)
        {
            utf8.AsSpan().Replace((byte)'\r', (byte)'\n');
        }

        return (utf8, new TextFormat(encoding, endings));
    }

    public static (string Text, TextFormat Format) Decode(ReadOnlySpan<byte> bytes, bool windows1251)
    {
        var (utf8, format) = DecodeToUtf8(bytes, windows1251);
        return (Utf8Strict.GetString(utf8), format);
    }

    public byte[] EncodeFromUtf8(ReadOnlySpan<byte> utf8Bytes)
    {
        var normalized = NormalizeEndings(utf8Bytes, Endings);

        switch (Encoding)
        {
            case TextEncoding.Utf8:
                return normalized;

            case TextEncoding.Utf8Bom:
            {
                var result = new byte[3 + normalized.Length];
                result[0] = 0xEF;
                result[1] = 0xBB;
                result[2] = 0xBF;
                normalized.CopyTo(result.AsSpan(3));
                return result;
            }

            case TextEncoding.Utf16Le:
                return EncodeUtf16(normalized, littleEndian: true);

            case TextEncoding.Utf16Be:
                return EncodeUtf16(normalized, littleEndian: false);

            case TextEncoding.Windows1251:
                try
                {
                    return System.Text.Encoding.Convert(Utf8Strict, Win1251Strict.Value, normalized);
                }
                catch (EncoderFallbackException ex)
                {
                    throw new InvalidDataException("Часть символов отсутствует в Windows-1251", ex);
                }

            default:
                return normalized;
        }
    }

    public byte[] Encode(string text)
    {
        var utf8 = Utf8Strict.GetBytes(text);
        return EncodeFromUtf8(utf8);
    }

    private static byte[] NormalizeEndings(ReadOnlySpan<byte> utf8, LineEndings endings)
    {
        if (endings is LineEndings.Lf or LineEndings.Mixed or LineEndings.None)
        {
            return utf8.ToArray();
        }

        if (endings == LineEndings.Cr)
        {
            var output = new byte[utf8.Length];
            int writeIndex = 0;
            for (int i = 0; i < utf8.Length; i++)
            {
                if (utf8[i] == (byte)'\r' && i + 1 < utf8.Length && utf8[i + 1] == (byte)'\n')
                {
                    output[writeIndex++] = (byte)'\r';
                    i++;
                }
                else if (utf8[i] == (byte)'\n')
                {
                    output[writeIndex++] = (byte)'\r';
                }
                else
                {
                    output[writeIndex++] = utf8[i];
                }
            }
            return writeIndex == output.Length ? output : output[..writeIndex];
        }

        int bareLfCount = 0;
        for (int i = 0; i < utf8.Length; i++)
        {
            if (utf8[i] == (byte)'\n' && (i == 0 || utf8[i - 1] != (byte)'\r'))
            {
                bareLfCount++;
            }
        }

        if (bareLfCount == 0)
        {
            return utf8.ToArray();
        }

        var crlf = new byte[utf8.Length + bareLfCount];
        int dst = 0;
        for (int i = 0; i < utf8.Length; i++)
        {
            if (utf8[i] == (byte)'\n' && (i == 0 || utf8[i - 1] != (byte)'\r'))
            {
                crlf[dst++] = (byte)'\r';
            }
            crlf[dst++] = utf8[i];
        }
        return crlf;
    }

    private static byte[] EncodeUtf16(byte[] utf8, bool littleEndian)
    {
        int charCount = Utf8Strict.GetCharCount(utf8);
        var chars = ArrayPool<char>.Shared.Rent(charCount);
        try
        {
            int actualChars = Utf8Strict.GetChars(utf8, chars);
            var target = littleEndian ? Utf16LeStrict : Utf16BeStrict;
            int byteCount = target.GetByteCount(chars, 0, actualChars);
            var result = new byte[2 + byteCount];
            if (littleEndian)
            {
                result[0] = 0xFF;
                result[1] = 0xFE;
            }
            else
            {
                result[0] = 0xFE;
                result[1] = 0xFF;
            }
            target.GetBytes(chars, 0, actualChars, result, 2);
            return result;
        }
        finally
        {
            ArrayPool<char>.Shared.Return(chars);
        }
    }

    private static byte[] TranscodeToUtf8(Encoding source, ReadOnlySpan<byte> bytes, string errorMessage)
    {
        try
        {
            int charCount = source.GetCharCount(bytes);
            var chars = ArrayPool<char>.Shared.Rent(charCount);
            try
            {
                int actualChars = source.GetChars(bytes, chars);
                int utf8Len = Utf8Strict.GetByteCount(chars.AsSpan(0, actualChars));
                var result = new byte[utf8Len];
                Utf8Strict.GetBytes(chars.AsSpan(0, actualChars), result);
                return result;
            }
            finally
            {
                ArrayPool<char>.Shared.Return(chars);
            }
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException(errorMessage, ex);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidDataException(errorMessage, ex);
        }
    }

    public static LineEndings DetectLineEndings(ReadOnlySpan<byte> bytes)
    {
        bool lf = false;
        bool crlf = false;
        bool cr = false;

        int i = 0;
        while (i < bytes.Length)
        {
            int nextIdx = bytes[i..].IndexOfAny((byte)'\n', (byte)'\r');
            if (nextIdx < 0)
            {
                break;
            }

            i += nextIdx;
            if (bytes[i] == (byte)'\n')
            {
                lf = true;
                i++;
            }
            else if (i + 1 < bytes.Length && bytes[i + 1] == (byte)'\n')
            {
                crlf = true;
                i += 2;
            }
            else
            {
                cr = true;
                i++;
            }

            if ((lf ? 1 : 0) + (crlf ? 1 : 0) + (cr ? 1 : 0) > 1)
            {
                return LineEndings.Mixed;
            }
        }

        if (crlf)
        {
            return LineEndings.CrLf;
        }
        if (cr)
        {
            return LineEndings.Cr;
        }
        return LineEndings.Lf;
    }

    private static Encoding CreateWin1251Strict()
    {
        System.Text.Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return System.Text.Encoding.GetEncoding(1251, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }

    private static Encoding CreateWin1251Lossy()
    {
        System.Text.Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return System.Text.Encoding.GetEncoding(1251);
    }
}
