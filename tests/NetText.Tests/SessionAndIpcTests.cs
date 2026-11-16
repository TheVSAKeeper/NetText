using System.Text;
using NetText.Core;
using Xunit;

namespace NetText.Tests;

public sealed class SessionAndIpcTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"nettext-sess-{Guid.NewGuid():N}");

    public SessionAndIpcTests()
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
    public void RestoresPreviousCommitIfLatestDraftOrManifestIsCorrupt()
    {
        var sessionDir = Path.Combine(_dir, "session");
        using (var j = SessionJournal.Open(sessionDir))
        {
            j.Commit(1, MakeCapture("first draft", 1));
            j.Commit(2, MakeCapture("second draft", 2));
        }

        Directory.CreateDirectory(Path.Combine(sessionDir, ".pending-incomplete"));
        var cp2 = Path.Combine(sessionDir, $"checkpoint-{2:D20}");
        File.WriteAllText(Path.Combine(cp2, "draft-0.txt"), "corrupt!");

        using var j2 = SessionJournal.Open(sessionDir);
        var recovered = j2.Restore();
        Assert.NotNull(recovered);
        Assert.NotNull(recovered!.Warning);
        Assert.Equal("first draft", recovered.DraftTexts[0]);
    }

    [Fact]
    public void DelayedPeriodicCommitCannotOverwriteNewerCommitAndRetainsTwo()
    {
        var sessionDir = Path.Combine(_dir, "retain");
        using var j = SessionJournal.Open(sessionDir);
        j.Commit(2, MakeCapture("v2", 2));
        j.Commit(1, MakeCapture("stale v1", 1));
        j.Commit(3, MakeCapture("v3", 3));
        j.Commit(4, MakeCapture("v4", 4));

        var dirs = Directory.GetDirectories(sessionDir, "checkpoint-*")
            .Select(Path.GetFileName)
            .OrderBy(x => x)
            .ToArray();
        Assert.Equal(new[] { $"checkpoint-{2:D20}", $"checkpoint-{3:D20}" }, dirs);

        Assert.Throws<IOException>(() => SessionJournal.Open(sessionDir));

        var rec = j.Restore();
        Assert.Equal("v4", rec!.DraftTexts[0]);
    }

    [Fact]
    public void SessionWriterQueuesMatchingStateWhenInFlight()
    {
        var writer = new SessionWriter();
        var cap = MakeCapture("hello", 1);
        var q1 = writer.Queue(cap);
        Assert.NotNull(q1);
        var q2 = writer.Queue(cap);
        Assert.NotNull(q2);
        Assert.True(q2!.Value.Revision > q1!.Value.Revision);
        writer.Complete(q2.Value.Revision, cap, true);
        Assert.Null(writer.Queue(cap));
    }

    [Fact]
    public void LogBookmarkRoundTripsWithoutDraftFile()
    {
        var sessionDir = Path.Combine(_dir, "log-sess");
        var bookmark = new LogBookmark
        {
            Identity = new FileIdentity(42, new byte[16]),
            Size = 10_000_000_000,
            Offset = 500_000,
            Hidden = 128_000,
            AutoRefresh = false,
            FollowEnd = true,
        };
        var tab = new SessionTab(
            1,
            new SessionFileTarget(@"C:\logs\big.log", null),
            new SessionFormat("Utf8", "Lf", false),
            new SessionSelection(0, 0),
            false,
            [0f, 0f],
            null,
            bookmark);
        var state = new SessionState([tab], 0, new SessionWindow(1080, 700, false), false, 15);
        var cap = new SessionCapture(state, [null]);

        using var j = SessionJournal.Open(sessionDir);
        j.Commit(1, cap);
        var cp1 = Path.Combine(sessionDir, $"checkpoint-{1:D20}");
        Assert.False(File.Exists(Path.Combine(cp1, "draft-0.txt")));

        var restored = j.Restore()!;
        Assert.Null(restored.DraftTexts[0]);
        Assert.Equal(10_000_000_000UL, restored.State.Tabs[0].Log!.Size);
        Assert.Equal(128_000UL, restored.State.Tabs[0].Log!.Hidden);
        Assert.True(restored.State.Tabs[0].Log!.FollowEnd);
        Assert.False(restored.State.Tabs[0].Log!.AutoRefresh);
    }

    [Fact]
    public void DiskStatusTracksExternalEditsAndRemoval()
    {
        var path = Path.Combine(_dir, "watched.txt");
        File.WriteAllText(path, "initial");
        var target = FileTarget.FromPicker(path);
        Assert.Equal(DiskStatusKind.Current, DiskStatus.Check(target));

        File.WriteAllText(path, "modified externally");
        Assert.Equal(DiskStatusKind.Changed, DiskStatus.Check(target));

        File.Delete(path);
        Assert.Equal(DiskStatusKind.Missing, DiskStatus.Check(target));
        Assert.Equal(DiskStatusKind.Missing, DiskStatus.FromException(new FileNotFoundException()));
        Assert.Equal(DiskStatusKind.Unavailable, DiskStatus.FromException(new UnauthorizedAccessException()));
    }

    [Fact]
    public void SingleInstanceResolvesPathsAndForwardsLaunchRequests()
    {
        var req = new LaunchRequest(@"C:\Work\Проект", ["notes.txt", @"D:\abs\Журнал.log"], true);
        var resolved = req.ResolveAbsolutePaths();
        Assert.Equal(@"C:\Work\Проект\notes.txt", resolved[0]);
        Assert.Equal(@"D:\abs\Журнал.log", resolved[1]);

        var tooMany = new LaunchRequest(_dir, Enumerable.Range(0, LaunchRequest.MaxPaths + 1).Select(i => $"{i}.txt").ToList(), true);
        Assert.Throws<ArgumentException>(() => tooMany.Validate());

        Assert.False(SingleInstance.ConstantTimeEqualsHex("abcd", "abce"));
        Assert.True(SingleInstance.ConstantTimeEqualsHex("a1b2", "a1b2"));

        var profile = Path.Combine(_dir, "profile");
        var (r1, primary) = SingleInstance.AcquireIn(profile, new LaunchRequest(_dir, [], true));
        Assert.Equal(InstanceRoleKind.Primary, r1);
        Assert.NotNull(primary);

        var (r2, secondary) = SingleInstance.AcquireIn(profile, new LaunchRequest(_dir, ["forwarded.txt"], true));
        Assert.Equal(InstanceRoleKind.Forwarded, r2);
        Assert.Null(secondary);

        LaunchRequest? received = null;
        for (int i = 0; i < 40 && !primary!.TryReceive(out received); i++)
        {
            Thread.Sleep(15);
        }
        Assert.NotNull(received);
        Assert.Equal("forwarded.txt", received!.Paths[0]);

        primary!.Dispose();
        var (r3, primary2) = SingleInstance.AcquireIn(profile, new LaunchRequest(_dir, [], true));
        Assert.Equal(InstanceRoleKind.Primary, r3);
        primary2?.Dispose();
    }

    [Fact]
    public void SingleInstanceWakeupCallbackFiresImmediatelyOnPipeForward()
    {
        var profile = Path.Combine(_dir, "ipc-wake");
        var (role, primary) = SingleInstance.AcquireIn(profile, new LaunchRequest(_dir, [], true));
        Assert.Equal(InstanceRoleKind.Primary, role);
        Assert.NotNull(primary);
        using (primary)
        {
            using var signaled = new ManualResetEventSlim(false);
            primary.SetWakeupCallback(() => signaled.Set());

            var (fwdRole, fwd) = SingleInstance.AcquireIn(profile, new LaunchRequest(_dir, ["woke.log"], true));
            Assert.Equal(InstanceRoleKind.Forwarded, fwdRole);
            Assert.Null(fwd);

            Assert.True(signaled.Wait(TimeSpan.FromSeconds(2)));
            Assert.True(primary.TryReceive(out var req));
            Assert.Equal("woke.log", req!.Paths[0]);
        }
    }

    [Fact]
    public void ClosedScratchAndLogTabsRoundTripAndCapAt100()
    {
        var sessionDir = Path.Combine(_dir, "closed-sess");
        var logBookmark = new LogBookmark
        {
            Identity = new FileIdentity(7, new byte[16]),
            Size = 1024,
            Offset = 12,
            Hidden = 6,
            AutoRefresh = false,
            FollowEnd = true,
        };

        var openTab = new SessionTab(1, null, new SessionFormat("Utf8", "Lf", false), new SessionSelection(0, 0), false, [0f, 0f], null, null);
        var closedTabs = new List<SessionTab>();
        var closedPayloads = new List<byte[]?>();
        for (int i = 0; i < SessionJournal.MaxClosedTabs + 5; i++)
        {
            bool isScratch = i == SessionJournal.MaxClosedTabs + 3;
            bool isLog = i == SessionJournal.MaxClosedTabs + 4;
            closedTabs.Add(new SessionTab(
                (ulong)(i + 10),
                isScratch ? null : new SessionFileTarget($@"C:\logs\file-{i}.log", null),
                new SessionFormat(isLog ? "Windows1251" : "Utf8", "Lf", false),
                new SessionSelection(isLog ? 8 : 1, isLog ? 12 : 4),
                isLog,
                [14f, 72f],
                isScratch ? (ulong)i : null,
                isLog ? logBookmark : null));
            closedPayloads.Add(isScratch ? Encoding.UTF8.GetBytes("Привет 🙂 черновик") : null);
        }

        var cap = new SessionCapture(
            new SessionState([openTab], 0, new SessionWindow(1080, 700, false), false, 15, closedTabs),
            [null],
            closedPayloads);

        using var j = SessionJournal.Open(sessionDir);
        j.Commit(1, cap);
        var restored = j.Restore()!;
        Assert.Equal(SessionJournal.MaxClosedTabs, restored.State.Closed!.Count);
        Assert.Equal(SessionJournal.MaxClosedTabs, restored.ClosedDraftTexts.Count);
        Assert.Equal("Привет 🙂 черновик", restored.ClosedDraftTexts[^2]);
        var restoredLog = restored.State.Closed[^1];
        Assert.Equal("Windows1251", restoredLog.Format.Encoding);
        Assert.Equal(8, restoredLog.Selection.Start);
        Assert.True(restoredLog.Reversed);
        Assert.Equal(12UL, restoredLog.Log!.Offset);

        // Old checkpoint without "closed" / "closed_drafts" fields still restores cleanly
        var legacyDir = Path.Combine(_dir, "legacy-sess");
        using var jLegacy = SessionJournal.Open(legacyDir);
        jLegacy.Commit(1, MakeCapture("legacy", 1));
        var legacyRestored = jLegacy.Restore()!;
        Assert.Empty(legacyRestored.State.Closed ?? []);
        Assert.Empty(legacyRestored.ClosedDraftTexts);
    }

    [Fact]
    public void ReopenRespectsDirtyAndEncodingDifferences()
    {
        var path = Path.Combine(_dir, "same.txt");
        File.WriteAllText(path, "Привет");
        using var utf8 = Files.OpenForEditor(path, windows1251: false);
        using var w1251 = Files.OpenForEditor(path, windows1251: true);

        Assert.Equal(utf8.Target, w1251.Target);
        Assert.True(Files.ShouldReplaceOpenedDocument(false, false, w1251.Target, utf8.Target, TextEncoding.Windows1251, TextEncoding.Utf8));
        Assert.False(Files.ShouldReplaceOpenedDocument(true, false, w1251.Target, utf8.Target, TextEncoding.Windows1251, TextEncoding.Utf8));
    }

    private static SessionCapture MakeCapture(string text, ulong rev)
    {
        var tab = new SessionTab(
            1,
            null,
            new SessionFormat("Utf8", "Lf", false),
            new SessionSelection(0, text.Length),
            false,
            [0f, 0f],
            rev,
            null);
        var state = new SessionState([tab], 0, new SessionWindow(1080, 700, false), false, 15);
        return new SessionCapture(state, [Encoding.UTF8.GetBytes(text)]);
    }
}
