using System.IO.Compression;
using System.Security.Cryptography;
using MailArchiver.Data;
using MailArchiver.Models;
using MailArchiver.Services.Shared;
using MailArchiver.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;
using Xunit;
using Opt = Microsoft.Extensions.Options.Options;

namespace MailArchiver.Tests.Services;

public class OriginalBinOptionsTests
{
    [Fact]
    public void ItIsOnByDefault_WithAThirtyDayBin()
    {
        var o = new RawMessageOptions().OriginalCleanup;
        Assert.True(o.Enabled);
        Assert.Equal(30, o.BinRetentionDays);
        Assert.Equal(1, o.IntervalHours);
        Assert.Equal(1000, o.MaxFilesPerRun);
    }

    [Fact]
    public void WhereItDoesNotApply_ItIsNotAStartupError_ButItIsNotApplicable()
    {
        var raw = new RawMessageOptions(); // defaults: originals off
        Assert.Empty(OriginalCleanupOptions.Validate(raw));
        Assert.False(OriginalCleanupOptions.IsApplicable(raw, out var why));
        Assert.NotEmpty(why);

        raw.Enabled = true; raw.Storage = RawMessageStorage.FileSystem; raw.FileSystem.HashNamed = true;
        Assert.True(OriginalCleanupOptions.IsApplicable(raw, out _));
        raw.FileSystem.HashNamed = false;
        Assert.False(OriginalCleanupOptions.IsApplicable(raw, out _));
    }

    [Fact]
    public void BadNumbers_AreStartupProblems_OnlyWhileEnabled()
    {
        var raw = new RawMessageOptions();
        raw.OriginalCleanup.BinRetentionDays = 0; raw.OriginalCleanup.IntervalHours = 0; raw.OriginalCleanup.MaxFilesPerRun = 0;
        Assert.Equal(3, OriginalCleanupOptions.Validate(raw).Count);
        raw.OriginalCleanup.Enabled = false;
        Assert.Empty(OriginalCleanupOptions.Validate(raw));
    }

    [Fact]
    public void OnlyWellFormedHashNamedPathsAreOurs()
    {
        var sha = "abcd" + new string('0', 60);
        Assert.True(OriginalBin.TryParseLive($"ab/cd/{sha}.eml.gz", out var parsed));
        Assert.Equal(sha, parsed);
        Assert.False(OriginalBin.TryParseLive("12345.eml.gz", out _));
        Assert.False(OriginalBin.TryParseLive("ab/cd/xyz.eml.gz", out _));
        Assert.False(OriginalBin.TryParseLive($"ff/ff/{sha}.eml.gz", out _));          // shard directories must match the hash
        Assert.False(OriginalBin.TryParseLive($"../{sha}.eml.gz", out _));
        Assert.False(OriginalBin.TryParseLive($".bin/ab/cd/{sha}.eml.gz", out _));     // the bin is not a live path
        Assert.False(OriginalBin.TryParseLive($"ab/cd/{sha.ToUpperInvariant()}.eml.gz", out _));
    }
}

/// <summary>The original bin: hash-named originals with no row move to .bin, wait out the retention, come back when referenced.</summary>
[Collection(TestDbFixture.CollectionName)]
public class OriginalBinTests : IAsyncLifetime
{
    private readonly TestDbFixture _fixture;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bin-" + Guid.NewGuid().ToString("N"));
    private readonly List<int> _accounts = new();

    public OriginalBinTests(TestDbFixture fixture) => _fixture = fixture;

    private MailArchiverDbContext NewContext()
        => new(new DbContextOptionsBuilder<MailArchiverDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning))
            .Options);

    private async Task ExecAsync(string sql)
    {
        await using var c = new NpgsqlConnection(_fixture.ConnectionString);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        await cmd.ExecuteNonQueryAsync();
    }

    private Task ResetAsync() => ExecAsync(@"
        ALTER TABLE mail_archiver.""AuditChain"" DISABLE TRIGGER USER;
        DELETE FROM mail_archiver.""AuditChain"";
        ALTER TABLE mail_archiver.""AuditChain"" ENABLE TRIGGER USER;
        DELETE FROM mail_archiver.""OriginalBinEntries"";
        DELETE FROM mail_archiver.""OriginalCleanupRuns"";");

    public async Task InitializeAsync() { Directory.CreateDirectory(_root); await ResetAsync(); }

    public async Task DisposeAsync()
    {
        await using (var c = NewContext())
        {
            foreach (var id in _accounts)
            {
                await c.Database.ExecuteSqlInterpolatedAsync($"UPDATE mail_archiver.\"ArchivedEmails\" SET \"IsLocked\" = false WHERE \"MailAccountId\" = {id}");
                await c.ArchivedEmails.Where(e => e.MailAccountId == id).ExecuteDeleteAsync();
                await c.MailAccounts.Where(a => a.Id == id).ExecuteDeleteAsync();
            }
        }
        await ResetAsync();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private RawMessageOptions Raw(Action<OriginalCleanupOptions>? tweak = null)
    {
        var raw = new RawMessageOptions
        {
            Enabled = true, Storage = RawMessageStorage.FileSystem, MaxMessageSizeMb = 0,
            FileSystem = new RawMessageFileSystemOptions { BasePath = _root, HashNamed = true },
            OriginalCleanup = new OriginalCleanupOptions { Enabled = true, BinRetentionDays = 30, MaxFilesPerRun = 1000 }
        };
        tweak?.Invoke(raw.OriginalCleanup);
        return raw;
    }

    private RawMessagePathResolver Paths() => new(new RawMessageFileSystemOptions { BasePath = _root, HashNamed = true }, _root);

    private OriginalBin NewBin(RawMessageOptions raw, bool chainOn = true, IAuditChain? chainOverride = null)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => NewContext());
        services.AddSingleton(Paths());
        var integrity = Opt.Create(new IntegrityOptions { AuditChainEnabled = chainOn });
        services.AddSingleton<IAuditChain>(sp => chainOverride ?? new AuditChain(sp.GetRequiredService<IServiceScopeFactory>(), integrity, NullLogger<AuditChain>.Instance));
        var lifetime = new Mock<IHostApplicationLifetime>();
        lifetime.SetupGet(l => l.ApplicationStopping).Returns(CancellationToken.None);
        var sp = services.BuildServiceProvider();
        return new OriginalBin(sp.GetRequiredService<IServiceScopeFactory>(), lifetime.Object, new MutableOptionsMonitor<RawMessageOptions>(raw), new MutableOptionsMonitor<IntegrityOptions>(integrity.Value), NullLogger<OriginalBin>.Instance);
    }

    /// <summary>Writes a hash-named original (gzip of fresh synthetic bytes) and returns (sha, relative path). Aged by default so it is not "recent".</summary>
    private (string Sha, string Rel) WriteOriginal(bool aged = true)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("Subject: synthetic " + Guid.NewGuid().ToString("N") + "\r\n\r\nbody\r\n");
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var rel = Paths().GetRelativePathForHash(sha);
        var abs = Paths().GetAbsolutePath(rel);
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        using (var fs = File.Create(abs)) using (var gz = new GZipStream(fs, CompressionLevel.Fastest)) gz.Write(bytes);
        if (aged) File.SetLastWriteTimeUtc(abs, DateTime.UtcNow.AddDays(-2));
        return (sha, rel);
    }

    private string Abs(string rel) => Paths().GetAbsolutePath(rel);

    private async Task<int> AddEmailWithRowAsync(string sha, string rel)
    {
        await using var ctx = NewContext();
        var account = new MailAccount
        {
            Name = $"ob-{Guid.NewGuid():N}".Substring(0, 20), EmailAddress = $"{Guid.NewGuid():N}@test.local",
            Provider = ProviderType.IMAP, IsEnabled = true, LastSync = DateTime.UtcNow
        };
        ctx.MailAccounts.Add(account);
        await ctx.SaveChangesAsync();
        _accounts.Add(account.Id);
        var email = new ArchivedEmail
        {
            MailAccountId = account.Id, MessageId = Guid.NewGuid().ToString("N"), Subject = "s", From = "a@b.test", To = "c@d.test",
            Body = "b", HtmlBody = "", Cc = "", Bcc = "", FolderName = "INBOX", SentDate = DateTime.UtcNow, ReceivedDate = DateTime.UtcNow,
            RawMessage = new RawMessage { Sha256 = sha, Size = 10, Source = "imap", StorageType = RawMessageStorage.FileSystem, StoragePath = rel, CreatedAt = DateTime.UtcNow }
        };
        ctx.ArchivedEmails.Add(email);
        await ctx.SaveChangesAsync();
        return email.Id;
    }

    private async Task DeleteEmailAsync(int emailId)
    {
        await using var c = NewContext();
        await c.Database.ExecuteSqlInterpolatedAsync($"UPDATE mail_archiver.\"ArchivedEmails\" SET \"IsLocked\" = false WHERE \"Id\" = {emailId}");
        await c.ArchivedEmails.Where(e => e.Id == emailId).ExecuteDeleteAsync();
    }

    private async Task<List<AuditChainEntry>> ChainAsync(string kind)
    {
        await using var c = NewContext();
        return await c.AuditChain.AsNoTracking().Where(e => e.Kind == kind).OrderBy(e => e.Seq).ToListAsync();
    }

    private async Task BackdateBinnedAsync(string sha, int days)
        => await ExecAsync($"UPDATE mail_archiver.\"OriginalBinEntries\" SET \"BinnedAt\" = now() at time zone 'utc' - interval '{days} days' WHERE \"Sha256\" = '{sha}'");

    private async Task<int> BinEntriesAsync() { await using var c = NewContext(); return await c.OriginalBinEntries.CountAsync(); }

    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnUnreferencedOriginal_IsMovedToTheBin_NotDeleted_AndRecorded()
    {
        var (sha, rel) = WriteOriginal();
        var bin = NewBin(Raw());

        var run = await bin.RunOnceAsync("test");

        Assert.Equal(OriginalCleanupRun.Completed, run.Status);
        Assert.Equal(1, run.Binned);
        Assert.False(File.Exists(Abs(rel)));
        Assert.True(File.Exists(Abs(OriginalBin.BinRel(sha))));
        Assert.Equal(1, await BinEntriesAsync());
        var entry = Assert.Single(await ChainAsync("original.binned"));
        Assert.Equal(sha, entry.Sha256);
        // nothing is purged on the first pass, however old the file was
        Assert.Empty(await ChainAsync("original.purged"));
    }

    [Fact]
    public async Task AReferencedOriginal_Stays()
    {
        var (sha, rel) = WriteOriginal();
        await AddEmailWithRowAsync(sha, rel);

        var run = await NewBin(Raw()).RunOnceAsync("test");

        Assert.Equal(0, run.Binned);
        Assert.Equal(1, run.Referenced);
        Assert.True(File.Exists(Abs(rel)));
    }

    [Fact]
    public async Task ASharedOriginal_StaysWhileAnyRowRemains_ThenGoesToTheBin()
    {
        var (sha, rel) = WriteOriginal();
        var a = await AddEmailWithRowAsync(sha, rel);
        var b = await AddEmailWithRowAsync(sha, rel);
        var bin = NewBin(Raw());

        await DeleteEmailAsync(a);
        Assert.Equal(0, (await bin.RunOnceAsync("t")).Binned);
        Assert.True(File.Exists(Abs(rel)));

        await DeleteEmailAsync(b);
        Assert.Equal(1, (await bin.RunOnceAsync("t")).Binned);
        Assert.False(File.Exists(Abs(rel)));
        Assert.True(File.Exists(Abs(OriginalBin.BinRel(sha))));
    }

    [Fact]
    public async Task ARecentlyWrittenFile_IsLeftAlone()
    {
        var (_, rel) = WriteOriginal(aged: false);
        var run = await NewBin(Raw()).RunOnceAsync("test");
        Assert.Equal(0, run.Binned);
        Assert.Equal(1, run.Deferred);
        Assert.True(File.Exists(Abs(rel)));
    }

    [Fact]
    public async Task AFileThatBecomesReferencedAgain_IsMovedBackFromTheBin()
    {
        var (sha, rel) = WriteOriginal();
        var bin = NewBin(Raw());
        await bin.RunOnceAsync("t");
        Assert.False(File.Exists(Abs(rel)));

        await AddEmailWithRowAsync(sha, rel);      // the same email is archived again, pointing at the live path
        var run = await bin.RunOnceAsync("t");

        Assert.Equal(1, run.Restored);
        Assert.True(File.Exists(Abs(rel)));
        Assert.False(File.Exists(Abs(OriginalBin.BinRel(sha))));
        Assert.Equal(0, await BinEntriesAsync());
        Assert.Single(await ChainAsync("original.restored"));
    }

    [Fact]
    public async Task WhenTheBytesWereWrittenAgain_TheBinCopyIsJustADuplicate()
    {
        var (sha, rel) = WriteOriginal();
        var bin = NewBin(Raw());
        await bin.RunOnceAsync("t");
        // a new ingest of the same bytes wrote a fresh live file
        var abs = Abs(rel); Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        File.Copy(Abs(OriginalBin.BinRel(sha)), abs);
        await AddEmailWithRowAsync(sha, rel);

        var run = await bin.RunOnceAsync("t");

        Assert.Equal(1, run.Restored);
        Assert.True(File.Exists(abs));
        Assert.False(File.Exists(Abs(OriginalBin.BinRel(sha))));
        Assert.Equal(0, await BinEntriesAsync());
    }

    [Fact]
    public async Task APurge_NeedsTheFullRetention_AndLeavesARecord()
    {
        var (sha, _) = WriteOriginal();
        var bin = NewBin(Raw());
        await bin.RunOnceAsync("t");

        await BackdateBinnedAsync(sha, 29);
        Assert.Equal(0, (await bin.RunOnceAsync("t")).Purged);
        Assert.True(File.Exists(Abs(OriginalBin.BinRel(sha))));

        await BackdateBinnedAsync(sha, 31);
        var run = await bin.RunOnceAsync("t");

        Assert.Equal(1, run.Purged);
        Assert.False(File.Exists(Abs(OriginalBin.BinRel(sha))));
        Assert.Equal(0, await BinEntriesAsync());
        var rec = Assert.Single(await ChainAsync("original.purged"));
        Assert.Equal(sha, rec.Sha256);
    }

    [Fact]
    public async Task OldBinEntry_ThatIsReferencedAgain_IsRestoredNotPurged()
    {
        var (sha, rel) = WriteOriginal();
        var bin = NewBin(Raw());
        await bin.RunOnceAsync("t");
        await BackdateBinnedAsync(sha, 40);
        await AddEmailWithRowAsync(sha, rel);

        var run = await bin.RunOnceAsync("t");

        Assert.Equal(0, run.Purged);
        Assert.Equal(1, run.Restored);
        Assert.True(File.Exists(Abs(rel)));
    }

    private sealed class FailingChain : IAuditChain
    {
        public Task<AuditChainEntry?> AppendAsync(string kind, int? emailId = null, string? sha256 = null, object? payload = null, CancellationToken cancellationToken = default)
            => Task.FromResult<AuditChainEntry?>(null);
    }

    [Fact]
    public async Task NoMoveWithoutItsChainRecord_AndNoPurgeWithoutOne()
    {
        // Moving: the chain cannot record it, so the file stays where it is.
        var (sha, rel) = WriteOriginal();
        var failing = NewBin(Raw(), chainOn: true, chainOverride: new FailingChain());
        var run = await failing.RunOnceAsync("t");
        Assert.Equal(0, run.Binned);
        Assert.True(run.Errors >= 1);
        Assert.True(File.Exists(Abs(rel)));
        Assert.False(File.Exists(Abs(OriginalBin.BinRel(sha))));
        Assert.Equal(0, await BinEntriesAsync());

        // Purging (irreversible): bin it with the chain switched off, then try to purge with a chain that cannot record.
        var quiet = NewBin(Raw(), chainOn: false);
        await quiet.RunOnceAsync("t");
        Assert.True(File.Exists(Abs(OriginalBin.BinRel(sha))));
        await BackdateBinnedAsync(sha, 40);
        var purge = await failing.RunOnceAsync("t");
        Assert.Equal(0, purge.Purged);
        Assert.True(purge.Errors >= 1);
        Assert.True(File.Exists(Abs(OriginalBin.BinRel(sha))));        // the original survives a failed record
        Assert.Equal(1, await BinEntriesAsync());
    }

    [Fact]
    public async Task MaxFilesPerRun_CapsTheMoves()
    {
        var files = Enumerable.Range(0, 3).Select(_ => WriteOriginal()).ToList();
        var bin = NewBin(Raw(o => o.MaxFilesPerRun = 2));

        var run = await bin.RunOnceAsync("t");

        Assert.Equal(2, run.Binned);
        Assert.True(run.Deferred >= 1);
        Assert.Equal(1, files.Count(f => File.Exists(Abs(f.Rel))));
        Assert.Equal(1, (await bin.RunOnceAsync("t")).Binned);   // the next run takes the rest
    }

    [Fact]
    public async Task OnlyHashNamedFilesAreTouched_IdNamedMalformedAndOutsideAreNot()
    {
        var idNamed = Path.Combine(_root, "00", "1234.eml.gz"); Directory.CreateDirectory(Path.GetDirectoryName(idNamed)!);
        File.WriteAllBytes(idNamed, new byte[] { 1, 2, 3 }); File.SetLastWriteTimeUtc(idNamed, DateTime.UtcNow.AddDays(-5));
        var bad = Path.Combine(_root, "ab", "cd", "not-a-hash.eml.gz"); Directory.CreateDirectory(Path.GetDirectoryName(bad)!);
        File.WriteAllBytes(bad, new byte[] { 1 }); File.SetLastWriteTimeUtc(bad, DateTime.UtcNow.AddDays(-5));
        var wrongShard = Path.Combine(_root, "ff", "ff", new string('a', 64) + ".eml.gz"); Directory.CreateDirectory(Path.GetDirectoryName(wrongShard)!);
        File.WriteAllBytes(wrongShard, new byte[] { 1 }); File.SetLastWriteTimeUtc(wrongShard, DateTime.UtcNow.AddDays(-5));

        var run = await NewBin(Raw()).RunOnceAsync("t");

        Assert.Equal(0, run.Binned);
        Assert.True(File.Exists(idNamed)); Assert.True(File.Exists(bad)); Assert.True(File.Exists(wrongShard));
    }

    [Fact]
    public async Task ASymlinkedOriginal_IsNeverMoved()
    {
        var outside = Path.Combine(Path.GetTempPath(), "outside-" + Guid.NewGuid().ToString("N") + ".gz");
        File.WriteAllBytes(outside, new byte[] { 1, 2, 3 });
        try
        {
            var (sha, rel) = WriteOriginal();
            File.Delete(Abs(rel));
            File.CreateSymbolicLink(Abs(rel), outside);

            var run = await NewBin(Raw()).RunOnceAsync("t");

            Assert.Equal(0, run.Binned);
            Assert.True(new FileInfo(Abs(rel)).LinkTarget != null);   // still the link, untouched
            Assert.True(File.Exists(outside));
            Assert.False(OriginalBin.IsSafe(Abs(rel), Path.GetFullPath(_root)));
        }
        finally { File.Delete(outside); }
    }

    [Fact]
    public void PathsOutsideTheBase_AreNotSafe()
    {
        var baseFull = Path.GetFullPath(_root);
        Assert.False(OriginalBin.IsSafe(Path.Combine(baseFull, "..", "elsewhere", "x.eml.gz"), baseFull));
        Assert.False(OriginalBin.IsSafe(baseFull + "-sibling/x", baseFull));
    }

    [Fact]
    public async Task Disabled_StartsNothing_AndMovesNothing()
    {
        var (_, rel) = WriteOriginal();
        var bin = NewBin(Raw(o => o.Enabled = false));
        await Assert.ThrowsAsync<InvalidOperationException>(() => bin.StartAsync("t"));
        Assert.True(File.Exists(Abs(rel)));
        Assert.False((await bin.GetStatusAsync()).Enabled);
    }

    [Fact]
    public async Task WhereOriginalsAreNotHashNamedFiles_ItIsIdle()
    {
        var raw = Raw(); raw.FileSystem.HashNamed = false;
        var bin = NewBin(raw);
        await Assert.ThrowsAsync<InvalidOperationException>(() => bin.StartAsync("t"));
        var s = await bin.GetStatusAsync();
        Assert.True(s.Enabled); Assert.False(s.Applicable); Assert.NotNull(s.NotApplicableReason);
    }

    [Fact]
    public async Task TwoStarts_NeverRunAtTheSameTime()
    {
        for (var i = 0; i < 20; i++) WriteOriginal();
        var bin = NewBin(Raw());

        var runs = await Task.WhenAll(bin.StartAsync("a"), bin.StartAsync("b"));
        await bin.WaitForCurrentAsync();

        if (runs[0].Id != runs[1].Id)
        {
            // The first finished before the second looked: then they ran one after the other.
            await using var c = NewContext();
            var all = await c.OriginalCleanupRuns.AsNoTracking().OrderBy(r => r.Id).ToListAsync();
            for (var i = 1; i < all.Count; i++) Assert.True(all[i - 1].FinishedAt <= all[i].StartedAt, "two runs overlapped");
        }
    }

    [Fact]
    public async Task TheOldOrphanCollector_LeavesHashNamedFilesAndTheBinAlone()
    {
        var (sha, rel) = WriteOriginal();
        var (sha2, rel2) = WriteOriginal();
        var bin = NewBin(Raw());
        await bin.RunOnceAsync("t");   // both binned
        var (_, rel3) = WriteOriginal(); // a live hash-named one

        await using var db = NewContext();
        var raw = Raw();
        var orphans = new RawMessageOrphanCollector(db, Paths(), Opt.Create(raw), NullLogger<RawMessageOrphanCollector>.Instance);
        await orphans.CollectAsync(TimeSpan.Zero, CancellationToken.None);

        Assert.True(File.Exists(Abs(OriginalBin.BinRel(sha))));
        Assert.True(File.Exists(Abs(OriginalBin.BinRel(sha2))));
        Assert.True(File.Exists(Abs(rel3)));
    }

    [Fact]
    public async Task ChainVerify_CountsTheBinEvents_AndFlagsARowThatReferencesABinnedOriginal()
    {
        var (sha, rel) = WriteOriginal();
        var bin = NewBin(Raw());
        await bin.RunOnceAsync("t");
        await BackdateBinnedAsync(sha, 40);
        await bin.RunOnceAsync("t");                       // purged

        await using (var db = NewContext())
        {
            var ok = await new AuditChainVerifier(db).VerifyAsync();
            Assert.True(ok.Ok, string.Join("; ", ok.Warnings));
            Assert.Equal(1, ok.OriginalsBinned);
            Assert.Equal(1, ok.OriginalsPurged);
        }

        // A row that still refers to an original the bin purged, with no later store or restore: a fault.
        await AddEmailWithRowAsync(sha, rel);
        await using (var db = NewContext())
        {
            var bad = await new AuditChainVerifier(db).VerifyAsync();
            Assert.False(bad.Ok);
            Assert.Contains(bad.Warnings, w => w.Contains("still refers"));
        }
    }

    [Fact]
    public async Task ADeletedEmail_WhoseOriginalWasBinnedAndPurged_IsNotAFindingForTheChainOrTheManifest()
    {
        var (sha, rel) = WriteOriginal();
        var id = await AddEmailWithRowAsync(sha, rel);
        var mfDir = Path.Combine(Path.GetTempPath(), "mf-" + Guid.NewGuid().ToString("N"));
        var integrity = Opt.Create(new IntegrityOptions { ManifestPath = mfDir, ManifestKeyPath = Path.Combine(mfDir, "keys", "k.pem"), AuditChainEnabled = true });
        var services = new ServiceCollection(); services.AddScoped(_ => NewContext());
        var chain = new AuditChain(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), integrity, NullLogger<AuditChain>.Instance);
        await using var mdb = NewContext();
        var env = new Mock<IHostEnvironment>(); env.SetupGet(e => e.ContentRootPath).Returns(Path.GetTempPath());
        var manifests = new ManifestService(mdb, chain, new AuditChainVerifier(mdb), integrity, env.Object, NullLogger<ManifestService>.Instance);
        try
        {
            var info = await manifests.CreateAsync("t");

            await DeleteEmailAsync(id);                                    // the trigger writes email.deleted
            var bin = NewBin(Raw());
            await bin.RunOnceAsync("t"); await BackdateBinnedAsync(sha, 40); await bin.RunOnceAsync("t");

            var check = await manifests.VerifyAsync(info.Name);
            Assert.True(check!.Ok, string.Join("; ", check.Problems));
            await using var db = NewContext();
            var report = await new AuditChainVerifier(db).VerifyAsync();
            Assert.True(report.Ok, string.Join("; ", report.Warnings));
        }
        finally { try { Directory.Delete(mfDir, true); } catch { } }
    }

    [Fact]
    public async Task Restore_ByHash_BringsItBack_AndKeepsItLiveForADay()
    {
        var (sha, rel) = WriteOriginal();
        var bin = NewBin(Raw());
        await bin.RunOnceAsync("t");

        var r = await bin.RestoreAsync(sha, "admin");

        Assert.True(r.Restored);
        Assert.True(File.Exists(Abs(rel)));
        Assert.True(File.GetLastWriteTimeUtc(Abs(rel)) > DateTime.UtcNow.AddMinutes(-5));
        Assert.Equal(0, (await bin.RunOnceAsync("t")).Binned);        // not binned again for 24 hours
        Assert.False((await bin.RestoreAsync(new string('a', 64), "admin")).Restored);
        Assert.False((await bin.RestoreAsync("../../etc/passwd", "admin")).Restored);
    }

    [Fact]
    public async Task Describe_TellsWhereADeletedEmailsOriginalIs_AndChangesNothing()
    {
        var (sha, rel) = WriteOriginal();
        var a = await AddEmailWithRowAsync(sha, rel);
        var b = await AddEmailWithRowAsync(sha, rel);
        var bin = NewBin(Raw());

        Assert.Equal("EmailStillExists", (await bin.DescribeEmailAsync(a)).State);
        await DeleteEmailAsync(a);
        Assert.Equal("StillReferenced", (await bin.DescribeEmailAsync(a)).State);
        await DeleteEmailAsync(b);
        Assert.Equal("LiveUnreferenced", (await bin.DescribeEmailAsync(b)).State);
        await bin.RunOnceAsync("t");
        var inBin = await bin.DescribeEmailAsync(b);
        Assert.Equal("InBin", inBin.State);
        Assert.True(inBin.PurgeAfter > DateTime.UtcNow.AddDays(29));
        Assert.True(File.Exists(Abs(OriginalBin.BinRel(sha))));      // describing moved nothing
        Assert.Equal("NoDeletionRecord", (await bin.DescribeEmailAsync(int.MaxValue)).State);
    }
}
