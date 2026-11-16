using System.Text;
using NetText.Core;
using NetText.UI;
using Xunit;

namespace NetText.Tests;

public sealed class TextFormatTests
{
    [Fact]
    public void SavesCyrillicWithoutChangingOriginalFormat()
    {
        byte[] utf16 = [0xFF, 0xFE, 0x2F, 0x04, 0x0D, 0x00, 0x0A, 0x00];
        var (text, format) = TextFormat.Decode(utf16, false);
        Assert.Equal("Я\r\n", text);
        Assert.Equal(utf16, format.Encode(text));
        Assert.Equal(
            [0xFF, 0xFE, 0x2F, 0x04, 0x0D, 0x00, 0x0A, 0x00, 0x11, 0x04, 0x0D, 0x00, 0x0A, 0x00],
            format.Encode("Я\r\nБ\n"));

        byte[] cp1251 = [0xCF, 0xF0, 0xE8, 0xE2, 0xE5, 0xF2];
        var (cpText, cpFormat) = TextFormat.Decode(cp1251, true);
        Assert.Equal("Привет", cpText);
        Assert.Equal(cp1251, cpFormat.Encode(cpText));
        Assert.Throws<InvalidDataException>(() => cpFormat.Encode("Привет 🦀"));
    }

    [Fact]
    public void RejectsNullBytesAndInvalidUtf8()
    {
        Assert.Throws<InvalidDataException>(() => TextFormat.Decode([0x41, 0x00, 0x42], false));
        Assert.Throws<InvalidDataException>(() => TextFormat.Decode([0xFF, 0xFE, 0x00], false));
        Assert.Throws<InvalidDataException>(() => TextFormat.Decode([0xCF, 0xF0], false));
    }

    [Fact]
    public void PreservesCrOnlyAndBomFormats()
    {
        byte[] crBytes = Encoding.UTF8.GetBytes("a\rb\r");
        var (utf8, format) = TextFormat.DecodeToUtf8(crBytes, false);
        Assert.Equal("a\nb\n", Encoding.UTF8.GetString(utf8));
        Assert.Equal(LineEndings.Cr, format.Endings);
        Assert.Equal(crBytes, format.EncodeFromUtf8(utf8));

        byte[] utf8Bom = [0xEF, 0xBB, 0xBF, (byte)'h', (byte)'i'];
        var (bomUtf8, bomFormat) = TextFormat.DecodeToUtf8(utf8Bom, false);
        Assert.Equal("hi", Encoding.UTF8.GetString(bomUtf8));
        Assert.Equal(TextEncoding.Utf8Bom, bomFormat.Encoding);
        Assert.Equal(utf8Bom, bomFormat.EncodeFromUtf8(bomUtf8));
    }
}

public sealed class FilesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"nettext-test-{Guid.NewGuid():N}");

    public FilesTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void DetectsExternalChangesBeforeReplacingFile()
    {
        var path = Path.Combine(_dir, "заметка.txt");
        File.WriteAllText(path, "Черновик");
        var old = Files.Open(path, false);
        File.WriteAllText(path, "Записано другой программой");

        Assert.Throws<IOException>(() => Files.SaveText(old.Target, old.Format, "Правки"));
        Assert.Equal("Записано другой программой", File.ReadAllText(path));
    }

    [Fact]
    public void SavesNewFileAndAllowsRepeatedSaves()
    {
        var path = Path.Combine(_dir, "заметка.txt");
        var target = FileTarget.FromPicker(path);
        target = Files.SaveText(target, TextFormat.Default, "Привет");
        Files.SaveText(target, TextFormat.Default, "Привет снова");

        Assert.Equal("Привет снова", File.ReadAllText(path));
    }
}

public sealed class RecentFilesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"nettext-recent-{Guid.NewGuid():N}");

    public RecentFilesTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void PersistsOrderAndEncodingAndMovesReopenedFilesToFront()
    {
        var historyPath = Path.Combine(_dir, "recent-files.json");
        var recent = new RecentFiles(historyPath);
        var first = Path.Combine(_dir, "Журнал.txt");
        var second = Path.Combine(_dir, "Черновик.txt");

        recent.Record([
            RecentFile.From(first, TextEncoding.Windows1251),
            RecentFile.From(second, TextEncoding.Utf8),
            RecentFile.From(first, TextEncoding.Windows1251),
        ]);

        var reloaded = new RecentFiles(historyPath);
        reloaded.Reload();
        Assert.Equal(2, reloaded.Entries.Count);
        Assert.Equal(first, reloaded.Entries[0].Path);
        Assert.True(reloaded.Entries[0].Windows1251);
        Assert.Equal(second, reloaded.Entries[1].Path);
        Assert.False(reloaded.Entries[1].Windows1251);
    }

    [Fact]
    public void RepairsTruncatedHistoryWhenFileIsOpened()
    {
        var historyPath = Path.Combine(_dir, "recent-files.json");
        File.WriteAllText(historyPath, "[");
        var recent = new RecentFiles(historyPath);
        Assert.Throws<InvalidDataException>(() => recent.Reload());

        var file = Path.Combine(_dir, "example.txt");
        recent.Record([RecentFile.From(file, TextEncoding.Utf8)]);
        recent.Reload();
        Assert.Equal(file, recent.Entries[0].Path);
    }

    [Fact]
    public void MergesUpdatesFromTwoWindowsAndKeepsMostRecentFiles()
    {
        var historyPath = Path.Combine(_dir, "recent-files.json");
        var firstWindow = new RecentFiles(historyPath);
        var secondWindow = firstWindow.Clone();

        var files = Enumerable.Range(0, RecentFiles.MaxFiles)
            .Select(i => RecentFile.From(Path.Combine(_dir, $"{i}.txt"), TextEncoding.Utf8));
        firstWindow.Record(files);

        var newFile = Path.Combine(_dir, "new.txt");
        secondWindow.Record([RecentFile.From(newFile, TextEncoding.Utf8)]);

        firstWindow.Reload();
        Assert.Equal(RecentFiles.MaxFiles, firstWindow.Entries.Count);
        Assert.Equal(newFile, firstWindow.Entries[0].Path);
        Assert.Equal(Path.Combine(_dir, "1.txt"), firstWindow.Entries[RecentFiles.MaxFiles - 1].Path);
    }
}

public sealed class GoToLineTests
{
    [Theory]
    [InlineData("1", 0)]
    [InlineData(" 80 ", 79)]
    public void AcceptsExistingOneBasedLines(string input, int expected)
    {
        Assert.Equal(expected, GoToLineDialog.LineIndex(input, 80));
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("81")]
    [InlineData("1.5")]
    [InlineData("4294967296")]
    public void RejectsInvalidLines(string input)
    {
        Assert.Null(GoToLineDialog.LineIndex(input, 80));
    }
}
