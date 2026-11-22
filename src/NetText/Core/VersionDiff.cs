namespace NetText.Core;

public enum VersionDiffKind
{
    None = 0,
    Context = 1,
    Added = 2,
    Removed = 3,
    Info = 4,
}

public sealed record VersionDiffLine(VersionDiffKind Kind, string Text);

public static class VersionDiff
{
    private const long MaxMatrixCells = 1_500_000;
    private const int MaxOutputLines = 2_000;
    private const int MaxLineCharacters = 512;

    // Added means a line exists in currentText but not in snapshotText;
    // Removed means it exists in snapshotText but not in currentText.
    public static IReadOnlyList<VersionDiffLine> Compare(string currentText, string snapshotText)
    {
        ArgumentNullException.ThrowIfNull(currentText);
        ArgumentNullException.ThrowIfNull(snapshotText);

        var current = currentText.Split('\n');
        var snapshot = snapshotText.Split('\n');
        var prefix = 0;
        while (prefix < current.Length && prefix < snapshot.Length
            && string.Equals(current[prefix], snapshot[prefix], StringComparison.Ordinal))
        {
            prefix++;
        }

        var suffix = 0;
        while (suffix < current.Length - prefix && suffix < snapshot.Length - prefix
            && string.Equals(current[^(suffix + 1)], snapshot[^(suffix + 1)], StringComparison.Ordinal))
        {
            suffix++;
        }

        var output = new List<VersionDiffLine>(Math.Min(current.Length + snapshot.Length, MaxOutputLines));
        var currentLength = current.Length - prefix - suffix;
        var snapshotLength = snapshot.Length - prefix - suffix;
        if (currentLength == 0 && snapshotLength == 0)
        {
            Add(output, VersionDiffKind.Info, "Отличий нет.");
        }
        else
        {
            AddCommonContext(output, current, 0, prefix);
            if (currentLength == 0)
            {
                for (var index = prefix; index < prefix + snapshotLength; index++)
                {
                    Add(output, VersionDiffKind.Removed, snapshot[index]);
                }
            }
            else if (snapshotLength == 0)
            {
                for (var index = prefix; index < prefix + currentLength; index++)
                {
                    Add(output, VersionDiffKind.Added, current[index]);
                }
            }
            else if ((long)currentLength * snapshotLength <= MaxMatrixCells)
            {
                AppendLcsDiff(current, snapshot, prefix, currentLength, snapshotLength, output);
            }
            else
            {
                for (var index = prefix; index < prefix + snapshotLength; index++)
                {
                    Add(output, VersionDiffKind.Removed, snapshot[index]);
                }

                for (var index = prefix; index < prefix + currentLength; index++)
                {
                    Add(output, VersionDiffKind.Added, current[index]);
                }

                Add(output, VersionDiffKind.Info, "Большое изменение показано одним блоком.");
            }

            AddCommonContext(output, current, current.Length - suffix, suffix);
        }

        if (output.Count > MaxOutputLines)
        {
            output.RemoveRange(MaxOutputLines - 1, output.Count - MaxOutputLines + 1);
            output.Add(new(VersionDiffKind.Info, "Показана только начальная часть сравнения."));
        }

        return output;
    }

    private static void AddCommonContext(List<VersionDiffLine> output, string[] lines, int start, int count)
    {
        const int contextCount = 3;
        if (count <= contextCount * 2)
        {
            for (var index = start; index < start + count; index++)
            {
                Add(output, VersionDiffKind.Context, lines[index]);
            }

            return;
        }

        for (var index = start; index < start + contextCount; index++)
        {
            Add(output, VersionDiffKind.Context, lines[index]);
        }

        Add(output, VersionDiffKind.Info, $"… {count - contextCount * 2:N0} неизменённых строк скрыто …");
        for (var index = start + count - contextCount; index < start + count; index++)
        {
            Add(output, VersionDiffKind.Context, lines[index]);
        }
    }

    private static void AppendLcsDiff(string[] current, string[] snapshot, int start, int currentLength, int snapshotLength, List<VersionDiffLine> output)
    {
        var lcs = new int[currentLength + 1, snapshotLength + 1];
        for (var i = currentLength - 1; i >= 0; i--)
        {
            for (var j = snapshotLength - 1; j >= 0; j--)
            {
                lcs[i, j] = string.Equals(current[start + i], snapshot[start + j], StringComparison.Ordinal)
                    ? lcs[i + 1, j + 1] + 1
                    : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }

        var currentIndex = 0;
        var snapshotIndex = 0;
        while (currentIndex < currentLength && snapshotIndex < snapshotLength)
        {
            if (string.Equals(current[start + currentIndex], snapshot[start + snapshotIndex], StringComparison.Ordinal))
            {
                Add(output, VersionDiffKind.Context, current[start + currentIndex]);
                currentIndex++;
                snapshotIndex++;
            }
            else if (lcs[currentIndex + 1, snapshotIndex] >= lcs[currentIndex, snapshotIndex + 1])
            {
                Add(output, VersionDiffKind.Added, current[start + currentIndex++]);
            }
            else
            {
                Add(output, VersionDiffKind.Removed, snapshot[start + snapshotIndex++]);
            }
        }

        while (currentIndex < currentLength)
        {
            Add(output, VersionDiffKind.Added, current[start + currentIndex++]);
        }

        while (snapshotIndex < snapshotLength)
        {
            Add(output, VersionDiffKind.Removed, snapshot[start + snapshotIndex++]);
        }
    }

    private static void Add(List<VersionDiffLine> output, VersionDiffKind kind, string text)
    {
        if (output.Count < MaxOutputLines + 1)
        {
            if (text.Length > MaxLineCharacters)
            {
                text = string.Concat(text.AsSpan(0, MaxLineCharacters - 1), "…");
            }

            output.Add(new(kind, text));
        }
    }
}
