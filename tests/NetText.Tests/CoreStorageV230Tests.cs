using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;
using NetText.Core;

namespace NetText.Tests;

public sealed class CoreStorageV230Tests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("nettext-v230-").FullName;

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void EditorSettingsDefaultsMigrationAndNormalizationWork()
    {
        var defaults = new EditorSettings();
        Assert.Equal("focus", defaults.Theme);
        Assert.Equal(12f, defaults.FontSize);
        Assert.False(defaults.WordWrap);
        Assert.Null(defaults.OpenLargeFilesAsLog);
        Assert.Equal(WorkspaceStartupMode.LastUsed, defaults.WorkspaceStartupMode);
        Assert.Null(defaults.StartupWorkspaceId);
        Assert.Equal(10, defaults.WorkspaceHistoryLimit);
        Assert.True(defaults.ShowDraftLineCount);
        Assert.Equal(300, defaults.ArchiveClosedDraftAfterMinutes);
        Assert.Equal(30, defaults.ArchivedDraftRetentionDays);
        Assert.True(defaults.AutoCheckUpdates);
        Assert.False(defaults.AutoCheckUpdatesChoiceMade);

        string legacyPath = Path.Combine(_root, "legacy-settings.json");
        File.WriteAllText(legacyPath, """{"UntitledLimit":15,"AutoCheckUpdates":false}""");
        var migrated = EditorSettings.Load(legacyPath);
        Assert.Equal(15, migrated.UntitledLimit);
        Assert.True(migrated.AutoCheckUpdates);
        Assert.True(migrated.AutoCheckUpdatesChoiceMade);
        Assert.Equal(300, migrated.ArchiveClosedDraftAfterMinutes);

        string explicitOffPath = Path.Combine(_root, "explicit-off-settings.json");
        File.WriteAllText(explicitOffPath, """{"AutoCheckUpdates":false,"AutoCheckUpdatesChoiceMade":true}""");
        var explicitOff = EditorSettings.Load(explicitOffPath);
        Assert.False(explicitOff.AutoCheckUpdates);
        Assert.True(explicitOff.AutoCheckUpdatesChoiceMade);

        string clampedPath = Path.Combine(_root, "clamped-settings.json");
        var extreme = new EditorSettings(UntitledLimit: 999)
        {
            Theme = "   ",
            FontSize = 96f,
            WorkspaceStartupMode = (WorkspaceStartupMode)999,
            StartupWorkspaceId = "  ",
            WorkspaceHistoryLimit = 500,
            ArchiveClosedDraftAfterMinutes = 900_000,
            ArchivedDraftRetentionDays = -5,
        };
        extreme.Save(clampedPath);
        var loaded = EditorSettings.Load(clampedPath);
        Assert.Equal(200, loaded.UntitledLimit);
        Assert.Equal("focus", loaded.Theme);
        Assert.Equal(48f, loaded.FontSize);
        Assert.Equal(WorkspaceStartupMode.LastUsed, loaded.WorkspaceStartupMode);
        Assert.Null(loaded.StartupWorkspaceId);
        Assert.Equal(200, loaded.WorkspaceHistoryLimit);
        Assert.Equal(525_600, loaded.ArchiveClosedDraftAfterMinutes);
        Assert.Equal(0, loaded.ArchivedDraftRetentionDays);

        var unlimitedHistory = (new EditorSettings { WorkspaceHistoryLimit = -1 }).Normalize();
        Assert.Equal(-1, unlimitedHistory.WorkspaceHistoryLimit);
    }

    [Fact]
    public async Task WorkspaceCatalogSessionPathsAndCrudWork()
    {
        var profile = Path.Combine(_root, "profile-ws");
        var service = new WorkspaceCatalogService(profile);

        Assert.Equal(
            Path.Combine(profile, "session"),
            service.GetSessionDirectory(WorkspaceCatalogService.DefaultWorkspaceId, WorkspaceCatalogService.PrimaryWindowId));
        Assert.Equal(
            Path.Combine(profile, "workspaces", WorkspaceCatalogService.DefaultWorkspaceId, "windows", "fffffffffffffffffffffffffffffff0", "session"),
            service.GetSessionDirectory(WorkspaceCatalogService.DefaultWorkspaceId, WorkspaceCatalogService.EmptyStartupWindowId));

        var catalog = await service.LoadAsync();
        Assert.Single(catalog.Workspaces);
        Assert.Equal(WorkspaceCatalogService.DefaultWorkspaceId, catalog.Workspaces[0].Id);
        Assert.Equal("По умолчанию", catalog.Workspaces[0].Name);

        var created = await service.CreateAsync("Работа", symbol: "◆", color: "#75a9ff");
        Assert.Equal("#75A9FF", created.Color);
        Assert.Equal("◆", created.Symbol);

        Assert.Equal(
            Path.Combine(profile, "workspaces", created.Id, "windows", "primary", "session"),
            service.GetSessionDirectory(created.Id, WorkspaceCatalogService.PrimaryWindowId));
        string winGuid = Guid.NewGuid().ToString("N");
        Assert.Equal(
            Path.Combine(profile, "workspaces", created.Id, "windows", winGuid, "session"),
            service.GetSessionDirectory(created.Id, "window:" + winGuid));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAsync("работа"));
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync("Плохой цвет", color: "75a9ff"));
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync("Два символа", symbol: "AB"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RenameAsync(WorkspaceCatalogService.DefaultWorkspaceId, "Другое"));

        await service.RenameAsync(created.Id, "Работа 2");
        await service.SetThemeAsync(created.Id, "light");
        await service.SetWindowWorkspaceAsync("window:" + winGuid, created.Id);
        var updated = await service.LoadAsync();
        var item = updated.Workspaces.Single(w => w.Id == created.Id);
        Assert.Equal("Работа 2", item.Name);
        Assert.Equal("light", item.Theme);
        Assert.Equal(created.Id, updated.WindowAssignments["window:" + winGuid]);
    }

    [Fact]
    public async Task WorkspaceCatalogDeleteRestoreCollisionCorruptAndMoveRecoveryWork()
    {
        var profile = Path.Combine(_root, "profile-ws-recovery");
        var service = new WorkspaceCatalogService(profile);
        var ws1 = await service.CreateAsync("Проект");
        var wsDir = service.GetWorkspaceDirectory(ws1.Id);
        Directory.CreateDirectory(Path.Combine(wsDir, "windows", "primary", "session"));
        File.WriteAllText(Path.Combine(wsDir, "windows", "primary", "session", "marker.txt"), "kept");

        var afterDelete = await service.DeleteAsync(ws1.Id, retentionLimit: 10);
        Assert.DoesNotContain(afterDelete.Workspaces, w => w.Id == ws1.Id);
        var deletedEntry = Assert.Single(afterDelete.DeletedWorkspaces!);
        Assert.False(Directory.Exists(wsDir));
        Assert.True(Directory.Exists(Path.Combine(service.DirectoryPath, "deleted", deletedEntry.DirectoryName)));

        // Case-insensitive name collision on restore appends " (восстановлено)"
        _ = await service.CreateAsync("проект");
        var restored = await service.RestoreDeletedAsync(ws1.Id);
        var restoredWs = restored.Workspaces.Single(w => w.Id == ws1.Id);
        Assert.Equal("Проект (восстановлено)", restoredWs.Name);
        Assert.Equal("kept", File.ReadAllText(Path.Combine(wsDir, "windows", "primary", "session", "marker.txt")));

        // Second collision increments suffix to " (восстановлено 2)"
        var ws2 = await service.CreateAsync("Дубль");
        _ = await service.DeleteAsync(ws2.Id, retentionLimit: -1);
        _ = await service.CreateAsync("Дубль");
        _ = await service.CreateAsync("Дубль (восстановлено)");
        var restored2 = await service.RestoreDeletedAsync(ws2.Id);
        Assert.Equal("Дубль (восстановлено 2)", restored2.Workspaces.Single(w => w.Id == ws2.Id).Name);

        // Simulated interrupted move recovery (index already committed deletion, folder still in live path)
        var ws3 = await service.CreateAsync("Сбой");
        var ws3Dir = service.GetWorkspaceDirectory(ws3.Id);
        Directory.CreateDirectory(ws3Dir);
        File.WriteAllText(Path.Combine(ws3Dir, "data.txt"), "v1");
        var afterDel3 = await service.DeleteAsync(ws3.Id, retentionLimit: 10);
        var del3 = afterDel3.DeletedWorkspaces!.Single(d => d.Workspace.Id == ws3.Id);
        var deletedDir3 = Path.Combine(service.DirectoryPath, "deleted", del3.DirectoryName);
        Directory.Move(deletedDir3, ws3Dir);
        File.WriteAllText(Path.Combine(service.DirectoryPath, "pending-directory-move"), del3.DirectoryName);

        _ = await service.LoadAsync();
        Assert.False(File.Exists(Path.Combine(service.DirectoryPath, "pending-directory-move")));
        Assert.False(Directory.Exists(ws3Dir));
        Assert.True(Directory.Exists(deletedDir3));

        // Corrupt index.json throws InvalidDataException without overwriting file
        var indexPath = Path.Combine(service.DirectoryPath, "index.json");
        File.WriteAllText(indexPath, "{broken");
        await Assert.ThrowsAsync<InvalidDataException>(() => service.LoadAsync());
        Assert.Equal("{broken", File.ReadAllText(indexPath));
    }

    [Fact]
    public async Task DraftArchiveServiceRoundTripTombstoneRecoveryAndCorruptIndexWork()
    {
        var profile = Path.Combine(_root, "profile-archive");
        var service = new DraftArchiveService(profile);
        var tab = new SessionTab(
            42,
            null,
            new SessionFormat("Windows1251", "CrLf", false),
            new SessionSelection(2, 7),
            true,
            [12f, 34f],
            1,
            null)
        {
            ClosedAtMs = DateTimeOffset.UtcNow.AddHours(-6).ToUnixTimeMilliseconds(),
            CustomTitle = "Заметка",
            DraftLogMode = true,
            DraftOffset = 100,
        };

        string content = string.Concat(Enumerable.Repeat("Повторяемая строка кириллицы 💡\n", 200));
        byte[] bytes = Encoding.UTF8.GetBytes(content);
        var entry = await service.ArchiveAsync(tab, bytes);

        string zipPath = Path.Combine(service.ArchiveDirectory, entry.Id + ".zip");
        Assert.True(File.Exists(zipPath));
        Assert.Equal(bytes.LongLength, entry.Bytes);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), entry.Sha256);

        using (var zip = ZipFile.OpenRead(zipPath))
        {
            Assert.NotNull(zip.GetEntry("draft.txt"));
        }

        var list = await service.ListAsync();
        Assert.Single(list);

        var (restoredTab, restoredBytes) = await service.RestoreAsync(entry);
        Assert.Equal(bytes, restoredBytes);
        Assert.Equal("Windows1251", restoredTab.Format.Encoding);
        Assert.Equal("CrLf", restoredTab.Format.LineEndings);
        Assert.Equal("Заметка", restoredTab.CustomTitle);
        Assert.True(restoredTab.DraftLogMode);
        Assert.Equal(100, restoredTab.DraftOffset);

        // Simulate unfinished .delete tombstone while entry is still in index.json
        string tombstonePath = zipPath + ".delete";
        File.Move(zipPath, tombstonePath);
        _ = await service.ListAsync();
        Assert.True(File.Exists(zipPath));
        Assert.False(File.Exists(tombstonePath));

        // Corrupt index.json throws InvalidDataException WITHOUT deleting .zip files
        string indexPath = Path.Combine(service.ArchiveDirectory, "index.json");
        string savedIndex = File.ReadAllText(indexPath);
        File.WriteAllText(indexPath, "{corrupt");
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ListAsync());
        Assert.True(File.Exists(zipPath));

        // Restore valid index and verify DeleteAsync + PruneExpiredAsync
        File.WriteAllText(indexPath, savedIndex);
        await service.DeleteAsync(entry);
        Assert.Empty(await service.ListAsync());
        Assert.False(File.Exists(zipPath));

        var oldTab = tab with { Id = 43, ClosedAtMs = DateTimeOffset.UtcNow.AddDays(-40).ToUnixTimeMilliseconds() };
        var oldEntry = await service.ArchiveAsync(oldTab, "old draft"u8.ToArray());
        // Rewrite index with old ArchivedAtMs to test PruneExpiredAsync
        var indexObj = JsonSerializer.Deserialize(File.ReadAllText(indexPath), DraftArchiveJsonContext.Default.DraftArchiveIndex)!;
        var aged = indexObj.Entries.Select(e => e with { ArchivedAtMs = DateTimeOffset.UtcNow.AddDays(-35).ToUnixTimeMilliseconds() }).ToList();
        File.WriteAllBytes(indexPath, JsonSerializer.SerializeToUtf8Bytes(new DraftArchiveIndex(aged), DraftArchiveJsonContext.Default.DraftArchiveIndex));

        Assert.Equal(0, await service.PruneExpiredAsync(0));
        Assert.Equal(1, await service.PruneExpiredAsync(30));
        Assert.Empty(await service.ListAsync());
        Assert.False(File.Exists(Path.Combine(service.ArchiveDirectory, oldEntry.Id + ".zip")));
    }

    [Fact]
    public void VersionDiffHandlesContextLcsAndLargeFallback()
    {
        var same = VersionDiff.Compare("a\nb", "a\nb");
        var sameLine = Assert.Single(same);
        Assert.Equal(VersionDiffKind.Info, sameLine.Kind);
        Assert.Equal("Отличий нет.", sameLine.Text);

        var prefixLines = Enumerable.Range(1, 10).Select(i => $"line {i}").ToArray();
        string current = string.Join('\n', prefixLines.Append("new line"));
        string snapshot = string.Join('\n', prefixLines.Append("old line"));
        var diff = VersionDiff.Compare(current, snapshot);
        Assert.Contains(diff, l => l.Kind == VersionDiffKind.Info && l.Text.Contains("неизменённых строк скрыто", StringComparison.Ordinal));
        Assert.Contains(diff, l => l.Kind == VersionDiffKind.Added && l.Text == "new line");
        Assert.Contains(diff, l => l.Kind == VersionDiffKind.Removed && l.Text == "old line");

        // Exceed 1.5M LCS cells (1300 * 1300 = 1_690_000) -> single block fallback + 2000 line cap
        string largeCurrent = string.Join('\n', Enumerable.Range(0, 1300).Select(i => $"c-{i}"));
        string largeSnapshot = string.Join('\n', Enumerable.Range(0, 1300).Select(i => $"s-{i}"));
        var largeDiff = VersionDiff.Compare(largeCurrent, largeSnapshot);
        Assert.Equal(2_000, largeDiff.Count);
        Assert.Equal(1_300, largeDiff.Count(l => l.Kind == VersionDiffKind.Removed));
        Assert.Equal(699, largeDiff.Count(l => l.Kind == VersionDiffKind.Added));
        Assert.Equal(VersionDiffKind.Info, largeDiff[^1].Kind);
        Assert.Equal("Показана только начальная часть сравнения.", largeDiff[^1].Text);

        string longLine = new('x', 600);
        var truncatedLineDiff = VersionDiff.Compare(longLine, "short");
        Assert.Contains(truncatedLineDiff, l => l.Kind == VersionDiffKind.Added && l.Text.Length == 512 && l.Text.EndsWith('…'));
    }

    [Fact]
    public void SessionEmptyUntitledFilterDraftEncodingAndRecentFilesRemoveWork()
    {
        var emptyDraft = new SessionTab(1, null, new SessionFormat("Utf8", "Lf", false), new SessionSelection(0, 0), false, [0f, 0f], null, null);
        var emptyBytesDraft = new SessionTab(2, null, new SessionFormat("Utf8", "Lf", false), new SessionSelection(0, 0), false, [0f, 0f], 1, null);
        var nonEmptyDraft = new SessionTab(3, null, new SessionFormat("Windows1251", "CrLf", false), new SessionSelection(0, 3), false, [0f, 0f], 2, null)
        {
            ClosedAtMs = 123456,
            AutoRefresh = false,
            DraftLogMode = true,
            DraftOffset = 42,
            CustomTitle = "Черновик 1251",
            EditPages = true,
        };
        var emptySavedFile = new SessionTab(4, new SessionFileTarget(@"C:\temp\empty.txt", null), new SessionFormat("Utf8Bom", "Lf", true), new SessionSelection(0, 0), false, [0f, 0f], null, null);

        Assert.True(SessionJournal.IsKnownEmptyUntitledDraft(emptyDraft, (byte[]?)null));
        Assert.True(SessionJournal.IsKnownEmptyUntitledDraft(emptyBytesDraft, Array.Empty<byte>()));
        Assert.False(SessionJournal.IsKnownEmptyUntitledDraft(nonEmptyDraft, "abc"u8.ToArray()));
        Assert.False(SessionJournal.IsKnownEmptyUntitledDraft(emptySavedFile, (byte[]?)null));

        var (filteredTabs, filteredPayloads) = SessionJournal.FilterClosedHistory(
            [emptyDraft, emptyBytesDraft, nonEmptyDraft, emptySavedFile],
            [null, Array.Empty<byte>(), "abc"u8.ToArray(), null]);
        Assert.Equal(2, filteredTabs.Count);
        Assert.Equal(3UL, filteredTabs[0].Id);
        Assert.Equal(4UL, filteredTabs[1].Id);
        Assert.Equal("abc"u8.ToArray(), filteredPayloads[0]);
        Assert.Null(filteredPayloads[1]);

        var isolatedDir = Path.Combine(_root, "isolated-session");
        using (var journal = SessionJournal.Open(isolatedDir))
        {
            var state = new SessionState([nonEmptyDraft], 0, new SessionWindow(900, 600, false), true, 14f, filteredTabs);
            journal.Commit(1, new SessionCapture(state, ["abc"u8.ToArray()], filteredPayloads));
        }
        using (var reopened = SessionJournal.Open(isolatedDir))
        {
            var recovered = reopened.Restore()!;
            var restoredTab = Assert.Single(recovered.State.Tabs);
            Assert.Equal("Windows1251", restoredTab.Format.Encoding);
            Assert.Equal("CrLf", restoredTab.Format.LineEndings);
            Assert.Equal(123456, restoredTab.ClosedAtMs);
            Assert.False(restoredTab.AutoRefresh);
            Assert.True(restoredTab.DraftLogMode);
            Assert.Equal(42, restoredTab.DraftOffset);
            Assert.Equal("Черновик 1251", restoredTab.CustomTitle);
            Assert.True(restoredTab.EditPages);
        }

        var recentPath = Path.Combine(_root, "recent.json");
        var recent = new RecentFiles(recentPath);
        var f1 = RecentFile.From(@"C:\logs\one.log", TextEncoding.Windows1251, viewAsLog: true, editPages: true);
        var f2 = RecentFile.From(@"C:\logs\two.txt", TextEncoding.Utf8, viewAsLog: false);
        recent.Record([f1, f2]);
        recent.RecordPositions([f1 with { Lines = 55, Position = nonEmptyDraft }]);

        var reloaded = new RecentFiles(recentPath);
        reloaded.Reload();
        var one = reloaded.History.Single(e => e.Path == f1.Path);
        Assert.True(one.ViewAsLog);
        Assert.True(one.EditPages);
        Assert.Equal(55UL, one.Lines);
        Assert.True(one.Position!.EditPages);

        Assert.True(reloaded.Remove(f1.Path));
        var afterRemove = new RecentFiles(recentPath);
        afterRemove.Reload();
        Assert.Single(afterRemove.History);
        Assert.Equal(f2.Path, afterRemove.History[0].Path);
    }

    [Fact]
    public void StreamedOpenForEditor_HandlesBomSplitUtf8SplitCrlfAndLineCount()
    {
        string path = Path.Combine(_root, "streamed-utf8-bom.txt");
        const int chunk = 1024 * 1024;
        var ms = new MemoryStream(chunk * 2 + 4096);
        ms.Write("\uFEFF"u8);

        // Place a 2-byte UTF-8 Cyrillic character so it straddles the 1 MiB chunk boundary (payload offset 1048575..1048576).
        byte[] filler = new byte[chunk - 1];
        Array.Fill(filler, (byte)'a');
        ms.Write(filler);
        byte[] cyrillic = Encoding.UTF8.GetBytes("Я");
        ms.Write(cyrillic);

        // Place a \r\n sequence so \r is at the end of chunk 2 (payload offset 2*chunk - 1) and \n starts chunk 3.
        int padToSecondEdge = (chunk * 2 - 1) - (chunk - 1 + cyrillic.Length);
        byte[] filler2 = new byte[padToSecondEdge];
        Array.Fill(filler2, (byte)'b');
        ms.Write(filler2);
        ms.Write("\r\n"u8);
        ms.Write("tail line\r\nlast"u8);

        byte[] fullOnDisk = ms.ToArray();
        byte[] expectedContent = fullOnDisk[3..];
        File.WriteAllBytes(path, fullOnDisk);

        using var opened = Files.OpenForEditor(path, windows1251: false);
        Assert.True(opened.IsStreamed);
        Assert.Equal(TextEncoding.Utf8Bom, opened.Format.Encoding);
        Assert.Equal(LineEndings.CrLf, opened.Format.Endings);
        Assert.Equal(expectedContent.Length, opened.Length);
        Assert.Equal(3UL, opened.LineCount);

        using var streamed = new MemoryStream(expectedContent.Length);
        opened.StreamUtf8Chunks(span => streamed.Write(span));
        Assert.Equal(expectedContent, streamed.ToArray());
        Assert.Equal(expectedContent, opened.Span.ToArray());
    }

    [Fact]
    public void StreamedOpenForEditor_NormalizesCrOnlyInStreamUtf8Chunks()
    {
        string path = Path.Combine(_root, "streamed-cr.txt");
        byte[] payload = new byte[2 * 1024 * 1024 + 128];
        Array.Fill(payload, (byte)'x');
        payload[1024 * 1024 - 1] = (byte)'\r';
        payload[2 * 1024 * 1024] = (byte)'\r';
        File.WriteAllBytes(path, payload);

        using var opened = Files.OpenForEditor(path, windows1251: false);
        Assert.True(opened.IsStreamed);
        Assert.Equal(LineEndings.Cr, opened.Format.Endings);
        Assert.Equal(3UL, opened.LineCount);

        using var streamed = new MemoryStream(payload.Length);
        opened.StreamUtf8Chunks(span => streamed.Write(span));
        byte[] result = streamed.ToArray();
        Assert.DoesNotContain((byte)'\r', result);
        Assert.Equal((byte)'\n', result[1024 * 1024 - 1]);
        Assert.Equal((byte)'\n', result[2 * 1024 * 1024]);
    }

    [Fact]
    public void LineNumbers_FormatUtf8_MatchesManagedFormat()
    {
        long[] values = [0, 1, 9, 999, 1_000, 12_345, 1_234_567, -1, -42, -1_000_000, long.MaxValue, long.MinValue];
        Span<byte> buf = stackalloc byte[48];
        foreach (long v in values)
        {
            int len = LineNumbers.FormatUtf8(v, buf);
            Assert.Equal(LineNumbers.Format(v), Encoding.UTF8.GetString(buf[..len]));
        }
    }

    [Fact]
    public void LogReader_BufferedReadPageAndPageBefore_PreservesCrlfAndUtf8()
    {
        string path = Path.Combine(_root, "buffered-pages.log");
        var sb = new StringBuilder();
        for (int i = 0; i < 3000; i++)
        {
            sb.Append($"2026-11-21T00:00:00Z INFO строка={i:D4} сообщение=привет_мир_тест\r\n");
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));

        using var reader = LogReader.Open(path, null);
        reader.Index.Advance(reader.Handle, reader.Encoding, reader.Size, CancellationToken.None);
        Assert.Equal(3001UL, reader.Index.LineCount);

        var opts = new PageOptions(80, 16 * 1024);
        ulong mid = reader.NearbyLineStart(reader.Size / 2) ?? 0;
        var pMid = reader.RenderPage(mid, opts);
        Assert.Equal(80, pMid.Rows.Count);
        Assert.All(pMid.Rows, r => Assert.StartsWith("2026-11-21T00:00:00Z INFO строка=", r.Text));

        var pEnd = reader.PageBefore(reader.Size, opts);
        Assert.Equal(80, pEnd.Rows.Count);
        Assert.Contains("строка=2999", pEnd.Rows[^1].Text);
        Assert.Contains("строка=2920", pEnd.Rows[0].Text);

        var pPrev = reader.PageBefore(pEnd.Rows[0].Start, opts);
        Assert.Equal(80, pPrev.Rows.Count);
        Assert.Contains("строка=2919", pPrev.Rows[^1].Text);
        Assert.Equal(pEnd.Rows[0].Start, pPrev.NextOffset);
    }
}
