using System.Globalization;
using System.IO;
using System.Text;

namespace NetText.Core;

internal sealed class LineTemplate
{
    public const ulong TabLimit = 8 * 1024 * 1024;
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    private abstract record Part
    {
        public abstract ulong EstimatedBytes(long rows);
        public abstract void Append(StringBuilder line, long row);
    }

    private sealed record Literal(string Value) : Part
    {
        public override ulong EstimatedBytes(long rows) => (ulong)Encoding.UTF8.GetByteCount(Value);
        public override void Append(StringBuilder line, long row) => line.Append(Value);
    }

    private sealed record Number(long First) : Part
    {
        public override ulong EstimatedBytes(long rows) => (ulong)Math.Max(
            First.ToString(CultureInfo.InvariantCulture).Length,
            checked(First + (rows - 1)).ToString(CultureInfo.InvariantCulture).Length);

        public override void Append(StringBuilder line, long row) =>
            line.Append(checked(First + row).ToString(CultureInfo.InvariantCulture));
    }

    private sealed record Identifier : Part
    {
        public override ulong EstimatedBytes(long rows) => 36;
        public override void Append(StringBuilder line, long row) => line.Append(Guid.NewGuid());
    }

    private sealed record RandomText(int Length) : Part
    {
        public override ulong EstimatedBytes(long rows) => (ulong)Length;
        public override void Append(StringBuilder line, long row)
        {
            for (int i = 0; i < Length; i++)
                line.Append(Alphabet[Random.Shared.Next(Alphabet.Length)]);
        }
    }

    private readonly List<Part> _parts = [];
    public long Rows { get; }
    public ulong EstimatedBytes { get; }

    public LineTemplate(string template, long rows)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rows);
        Rows = rows;
        int literalStart = 0;
        int start = -1;
        for (int end = 0; end < template.Length; end++)
        {
            if (template[end] == '{')
            {
                start = end;
                continue;
            }
            if (template[end] != '}' || start < 0) continue;
            var token = template.AsSpan(start + 1, end - start - 1);
            Part? part = token switch
            {
                "guid" => new Identifier(),
                "rand_string" => new RandomText(12),
                _ when token.StartsWith("int_from_", StringComparison.Ordinal) =>
                    new Number(long.Parse(token[9..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture)),
                _ when token.StartsWith("rand_string_", StringComparison.Ordinal) =>
                    new RandomText(int.Parse(token[12..], NumberStyles.None, CultureInfo.InvariantCulture)),
                _ => null,
            };
            if (part is not null)
            {
                if (start > literalStart) _parts.Add(new Literal(template[literalStart..start]));
                _parts.Add(part);
                literalStart = end + 1;
            }
            start = -1;
        }
        if (literalStart < template.Length) _parts.Add(new Literal(template[literalStart..]));

        ulong rowBytes = 0;
        foreach (var part in _parts)
            rowBytes = checked(rowBytes + part.EstimatedBytes(rows));
        EstimatedBytes = checked(rowBytes * (ulong)rows + (ulong)(rows - 1));
    }

    public void Write(TextWriter writer, CancellationToken cancellationToken, Action<long>? progress = null)
    {
        var buffer = new StringBuilder();
        for (long row = 0; row < Rows; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (row != 0) writer.Write('\n');
            foreach (var part in _parts)
            {
                if (part is RandomText random && random.Length > 16384)
                {
                    writer.Write(buffer);
                    buffer.Clear();
                    int remaining = random.Length;
                    while (remaining > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        int length = Math.Min(16384, remaining);
                        new RandomText(length).Append(buffer, row);
                        writer.Write(buffer);
                        buffer.Clear();
                        remaining -= length;
                    }
                }
                else
                {
                    part.Append(buffer, row);
                }
            }
            writer.Write(buffer);
            buffer.Clear();
            if ((row & 1023) == 0 || row == Rows - 1) progress?.Invoke(row + 1);
        }
    }

    public void WriteFile(FileTarget target, CancellationToken cancellationToken, Action<long>? progress = null) =>
        Files.WriteAtomically(target, stream =>
        {
            using var writer = new StreamWriter(stream, new UTF8Encoding(false), 16384, leaveOpen: true);
            Write(writer, cancellationToken, progress);
        }, cancellationToken);
}

internal sealed record ListComparison(string[] Common, string[] OnlyLeft, string[] OnlyRight)
{
    public static ListComparison Compare(string left, string right)
    {
        static string[] Unique(string input)
        {
            if (input.Length == 0) return [];
            var normalized = input.Replace("\r\n", "\n", StringComparison.Ordinal);
            if (normalized.EndsWith('\n') || normalized.EndsWith('\r')) normalized = normalized[..^1];
            return normalized.Replace('\r', '\n').Split('\n').Distinct(StringComparer.Ordinal).ToArray();
        }

        var leftLines = Unique(left);
        var rightLines = Unique(right);
        var leftSet = leftLines.ToHashSet(StringComparer.Ordinal);
        var rightSet = rightLines.ToHashSet(StringComparer.Ordinal);
        return new ListComparison(
            leftLines.Where(rightSet.Contains).ToArray(),
            leftLines.Where(line => !rightSet.Contains(line)).ToArray(),
            rightLines.Where(line => !leftSet.Contains(line)).ToArray());
    }

    public string Render() => string.Join('\n',
        "Общие", string.Join('\n', Common), "\nТолько слева", string.Join('\n', OnlyLeft),
        "\nТолько справа", string.Join('\n', OnlyRight));
}
