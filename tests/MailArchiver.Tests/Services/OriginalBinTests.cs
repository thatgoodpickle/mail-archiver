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

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root); await ResetAsync();
        await AdoptIdentityAsync();   // the archive and its originals folder belong together, as in a normal installation
    }

    private async Task AdoptIdentityAsync()
    {
        await using var c = NewContext();
        await new ArchiveIdentityService(c).AdoptAsync(Path.GetFullPath(_root));
    }

    /// <summary>Records a delete request for an original, the way the database trigger does when an email row is deleted.</summary>
    private async Task DeleteRequestAsync(string sha, int emailId = 1)
    {
        var services = new ServiceCollection(); services.AddScoped(_ => NewContext());
        var chain = new AuditChain(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), Opt.Create(new IntegrityOptions()), NullLogger<AuditChain>.Instance);
        await chain.AppendAsync("email.deleted", emailId, sha, new { test = true });
    }

    private async Task<(string Sha, string Rel)> WriteOriginalAsync(bool aged = true, bool deleteRequest = true)
    {
        var f = WriteOriginal(aged);
        if (deleteRequest) await DeleteRequestAsync(f.Sha);
        return f;
    }

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
        services.AddScoped<IArchiveIdentityService, ArchiveIdentityService>();
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
        var (sha, rel) = await WriteOriginalAsync();
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
        var (sha, rel) = await WriteOriginalAsync();
        await AddEmailWithRowAsync(sha, rel);

        var run = await NewBin(Raw()).RunOnceAsync("test");

        Assert.Equal(0, run.Binned);
        Assert.Equal(1, run.Referenced);
        Assert.True(File.Exists(Abs(rel)));
    }

    [Fact]
    public async Task ASharedOriginal_StaysWhileAnyRowRemains_ThenGoesToTheBin()
    {
        var (sha, rel) = await WriteOriginalAsync();
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
        var (_, rel) = await WriteOriginalAsync(aged: false);
        var run = await NewBin(Raw()).RunOnceAsync("test");
        Assert.Equal(0, run.Binned);
        Assert.Equal(1, run.Deferred);
        Assert.True(File.Exists(Abs(rel)));
    }

    [Fact]
    public async Task AFileThatBecomesReferencedAgain_IsMovedBackFromTheBin()
    {
        var (sha, rel) = await WriteOriginalAsync();
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
        var (sha, rel) = await WriteOriginalAsync();
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
        var (sha, _) = await WriteOriginalAsync();
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
        var (sha, rel) = await WriteOriginalAsync();
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
        var (sha, rel) = await WriteOriginalAsync();
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
        var files = new List<(string Sha, string Rel)>(); for (var i = 0; i < 3; i++) files.Add(await WriteOriginalAsync());
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
            var (sha, rel) = await WriteOriginalAsync();
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
        var (_, rel) = await WriteOriginalAsync();
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
        for (var i = 0; i < 20; i++) await WriteOriginalAsync();
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
        var (sha, rel) = await WriteOriginalAsync();
        var (sha2, rel2) = await WriteOriginalAsync();
        var bin = NewBin(Raw());
        await bin.RunOnceAsync("t");   // both binned
        var (_, rel3) = await WriteOriginalAsync(); // a live hash-named one

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
        var (sha, rel) = await WriteOriginalAsync();
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
        var (sha, rel) = await WriteOriginalAsync();
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
        var (sha, rel) = await WriteOriginalAsync();
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
        var (sha, rel) = await WriteOriginalAsync();
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

    // ===== second review N-01: the Trash needs proof =======================================================

    [Fact]
    public async Task AnOriginalWithNoDeleteRequest_IsNeverMovedByARun_ButIsListed_AndCanBeMovedByAnAdmin()
    {
        var (shaA, relA) = await WriteOriginalAsync(deleteRequest: false);   // unreferenced, and nobody ever asked to delete it
        var (shaB, relB) = await WriteOriginalAsync();                       // deleted on purpose
        var bin = NewBin(Raw());

        var run = await bin.RunOnceAsync("t");

        Assert.Equal(1, run.Binned);
        Assert.Equal(1, run.Unclaimed);
        Assert.True(File.Exists(Abs(relA)));                    // untouched
        Assert.False(File.Exists(Abs(relB)));
        var listed = Assert.Single(await bin.ListUnclaimedAsync(50));
        Assert.Equal(shaA, listed.Sha256);

        var moved = await bin.MoveToTrashAsync(shaA, "admin");
        Assert.True(moved.Restored, moved.Message);
        Assert.False(File.Exists(Abs(relA)));
        Assert.True(File.Exists(Abs(OriginalBin.BinRel(shaA))));
        Assert.Contains(await ChainAsync("original.binned"), e => e.Sha256 == shaA);
        Assert.Empty(await bin.ListUnclaimedAsync(50));
    }

    [Fact]
    public async Task AnAdminCannotMoveAReferencedOriginal()
    {
        var (sha, rel) = await WriteOriginalAsync(deleteRequest: false);
        await AddEmailWithRowAsync(sha, rel);
        var r = await NewBin(Raw()).MoveToTrashAsync(sha, "admin");
        Assert.False(r.Restored);
        Assert.True(File.Exists(Abs(rel)));
    }

    [Fact]
    public async Task AnEmptyOrWrongDatabase_NextToAFullFolder_MovesNothing()
    {
        var files = new List<(string Sha, string Rel)>();
        for (var i = 0; i < 3; i++) files.Add(WriteOriginal());       // originals, aged, but this database knows nothing of them
        await ExecAsync(@"ALTER TABLE mail_archiver.""AuditChain"" DISABLE TRIGGER USER; DELETE FROM mail_archiver.""AuditChain""; ALTER TABLE mail_archiver.""AuditChain"" ENABLE TRIGGER USER;
                           DELETE FROM mail_archiver.""ArchiveIdentity"";");   // a different, empty database: no identity, no history

        var run = await NewBin(Raw()).RunOnceAsync("t");

        Assert.Equal(OriginalCleanupRun.Refused, run.Status);
        Assert.False(string.IsNullOrEmpty(run.Alarm));
        Assert.Equal(0, run.Binned);
        Assert.All(files, f => Assert.True(File.Exists(Abs(f.Rel))));
        Assert.Equal(0, await BinEntriesAsync());
    }

    [Fact]
    public async Task ADatabaseWithAnotherIdentityThanTheFolder_MovesNothing_EvenWithDeleteRequests()
    {
        var (_, rel) = await WriteOriginalAsync();
        File.WriteAllText(Path.Combine(_root, ArchiveIdentityService.MarkerFile), Guid.NewGuid().ToString("D"));

        var bin = NewBin(Raw());
        var run = await bin.RunOnceAsync("t");

        Assert.Equal(OriginalCleanupRun.Refused, run.Status);
        Assert.True(File.Exists(Abs(rel)));
        var id = await bin.GetIdentityAsync();
        Assert.False(id.Ok); Assert.Equal(ArchiveIdentityCheck.Mismatch, id.Code);
        Assert.False((await bin.MoveToTrashAsync(WriteOriginal().Sha, "admin")).Restored);   // the manual move refuses too
    }

    [Fact]
    public async Task AFolderWithoutItsMarker_IsRefusedUntilAnAdministratorAdoptsIt()
    {
        var (_, rel) = await WriteOriginalAsync();
        File.Delete(Path.Combine(_root, ArchiveIdentityService.MarkerFile));
        var bin = NewBin(Raw());

        var refused = await bin.RunOnceAsync("t");
        Assert.Equal(OriginalCleanupRun.Refused, refused.Status);
        Assert.True(File.Exists(Abs(rel)));
        Assert.Equal(ArchiveIdentityCheck.MarkerMissing, (await bin.GetIdentityAsync()).Code);

        var adopted = await bin.AdoptAsync("admin");
        Assert.True(adopted.Ok);
        Assert.Contains(await ChainAsync("archive.identity.adopted"), e => e.Payload.Contains("admin"));

        var run = await bin.RunOnceAsync("t");
        Assert.Equal(OriginalCleanupRun.Completed, run.Status);
        Assert.Equal(1, run.Binned);
    }

    [Fact]
    public async Task AFreshArchive_GetsItsIdentityAndMarker()
    {
        await ResetAsync();
        var fresh = Path.Combine(Path.GetTempPath(), "fresh-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var c = NewContext();
            var check = await new ArchiveIdentityService(c).CheckAsync(fresh);
            Assert.True(check.Ok);
            Assert.Equal(ArchiveIdentityCheck.Created, check.Code);
            Assert.Equal(check.DatabaseId, File.ReadAllText(Path.Combine(fresh, ArchiveIdentityService.MarkerFile)).Trim());
            Assert.Equal(ArchiveIdentityCheck.Matches, (await new ArchiveIdentityService(c).CheckAsync(fresh)).Code);
        }
        finally { try { Directory.Delete(fresh, true); } catch { } }
    }

    [Fact]
    public async Task AnExistingArchiveWithRowsButNoIdentity_IsRefusedUntilAdopted()
    {
        var (sha, rel) = await WriteOriginalAsync(deleteRequest: false);
        await AddEmailWithRowAsync(sha, rel);
        await ExecAsync(@"DELETE FROM mail_archiver.""ArchiveIdentity"";");
        File.Delete(Path.Combine(_root, ArchiveIdentityService.MarkerFile));

        await using var c = NewContext();
        var svc = new ArchiveIdentityService(c);
        var check = await svc.CheckAsync(Path.GetFullPath(_root));
        Assert.False(check.Ok);
        Assert.Equal(ArchiveIdentityCheck.NotAdopted, check.Code);
        Assert.True((await svc.AdoptAsync(Path.GetFullPath(_root))).Ok);
    }

    [Fact]
    public async Task TheCircuitBreaker_RefusesAHugeShare_UntilTheAdminRunsItAnyway()
    {
        var files = new List<(string Sha, string Rel)>();
        for (var i = 0; i < 60; i++) files.Add(await WriteOriginalAsync());      // 60 of 60 look deleted at once: above max(50, 5%)
        var bin = NewBin(Raw());

        var refused = await bin.RunOnceAsync("t");
        Assert.Equal(OriginalCleanupRun.Refused, refused.Status);
        Assert.Contains("safety limit", refused.Alarm);
        Assert.Equal(0, refused.Binned);
        Assert.All(files, f => Assert.True(File.Exists(Abs(f.Rel))));

        var anyway = await bin.RunOnceAsync("t", CancellationToken.None, overrideBreaker: true);
        Assert.Equal(OriginalCleanupRun.Completed, anyway.Status);
        Assert.Equal(60, anyway.Binned);
    }

    [Fact]
    public async Task TheBreakerDoesNotGetInTheWayOfASmallArchive()
    {
        for (var i = 0; i < 5; i++) await WriteOriginalAsync();                  // 5 of 5 is above 5% but below the minimum of 50 files
        Assert.Equal(5, (await NewBin(Raw()).RunOnceAsync("t")).Binned);
    }

    // ===== N-03: record and entry first, crash recovery ====================================================

    [Fact]
    public async Task AnEntryWhoseFileNeverArrivedInTheTrash_IsClearedAtTheNextRun_AndTheFileStaysLive()
    {
        var (sha, rel) = await WriteOriginalAsync(deleteRequest: false);       // simulate: entry written, process died before the move
        await using (var c = NewContext())
        {
            c.OriginalBinEntries.Add(new OriginalBinEntry { StoragePath = rel, Sha256 = sha, BinPath = OriginalBin.BinRel(sha), BinnedAt = DateTime.UtcNow, Size = 1 });
            await c.SaveChangesAsync();
        }

        var run = await NewBin(Raw()).RunOnceAsync("t");

        Assert.Equal(0, await BinEntriesAsync());
        Assert.True(File.Exists(Abs(rel)));
        Assert.Contains(await ChainAsync("original.restored"), e => e.Sha256 == sha && e.Payload.Contains("never completed"));
    }

    [Fact]
    public async Task AFileInTheTrashWithNoEntry_IsAdopted_ThenPurgedLikeAnyOther()
    {
        var (sha, rel) = WriteOriginal();
        var bin = Abs(OriginalBin.BinRel(sha));
        Directory.CreateDirectory(Path.GetDirectoryName(bin)!);
        File.Move(Abs(rel), bin);                                               // simulate: moved, then the entry was never saved

        var bins = NewBin(Raw());
        await bins.RunOnceAsync("t");

        Assert.Equal(1, await BinEntriesAsync());
        Assert.Contains(await ChainAsync("original.binned"), e => e.Sha256 == sha && e.Payload.Contains("adopted"));
        await BackdateBinnedAsync(sha, 40);
        Assert.Equal(1, (await bins.RunOnceAsync("t")).Purged);
        Assert.False(File.Exists(bin));
    }

    [Fact]
    public async Task AnUnlistedTrashFile_ThatARowNeedsAgain_ComesBack()
    {
        var (sha, rel) = WriteOriginal();
        var bin = Abs(OriginalBin.BinRel(sha));
        Directory.CreateDirectory(Path.GetDirectoryName(bin)!);
        File.Move(Abs(rel), bin);
        await AddEmailWithRowAsync(sha, rel);                                   // the email exists, its original sits unlisted in the Trash

        await NewBin(Raw()).RunOnceAsync("t");

        Assert.True(File.Exists(Abs(rel)));
        Assert.False(File.Exists(bin));
        Assert.Equal(0, await BinEntriesAsync());
    }

    [Fact]
    public async Task ALiveFileTouchedJustBeforeTheMove_IsNotMoved()
    {
        var (_, rel) = await WriteOriginalAsync(aged: false);                   // an import that reused it just touched it
        var run = await NewBin(Raw()).RunOnceAsync("t");
        Assert.Equal(0, run.Binned);
        Assert.True(File.Exists(Abs(rel)));
        Assert.Equal(0, await BinEntriesAsync());
    }

    // ===== N-15, N-07: housekeeping ========================================================================

    [Fact]
    public async Task LeftoverTempFilesOfInterruptedWrites_AreRemovedAfterADay_AndOnlyThose()
    {
        var (sha, rel) = WriteOriginal();
        var oldTmp = Abs(rel) + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var newTmp = Abs(rel) + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var odd = Abs(rel) + ".keep.tmp";
        File.WriteAllBytes(oldTmp, new byte[] { 1 }); File.SetLastWriteTimeUtc(oldTmp, DateTime.UtcNow.AddDays(-3));
        File.WriteAllBytes(newTmp, new byte[] { 1 });
        File.WriteAllBytes(odd, new byte[] { 1 }); File.SetLastWriteTimeUtc(odd, DateTime.UtcNow.AddDays(-3));

        await NewBin(Raw()).RunOnceAsync("t");

        Assert.False(File.Exists(oldTmp));
        Assert.True(File.Exists(newTmp));
        Assert.True(File.Exists(odd));
    }

    [Fact]
    public async Task ExtractedAttachmentText_NoAttachmentRefersToAnyMore_IsRemoved()
    {
        var orphan = Convert.ToHexString(SHA256.HashData(Guid.NewGuid().ToByteArray())).ToLowerInvariant();
        var recent = Convert.ToHexString(SHA256.HashData(Guid.NewGuid().ToByteArray())).ToLowerInvariant();
        await using (var c = NewContext())
        {
            c.AttachmentTexts.Add(new AttachmentText { Sha256 = orphan, Text = "orphan text", ExtractedAt = DateTime.UtcNow.AddDays(-3) });
            c.AttachmentTexts.Add(new AttachmentText { Sha256 = recent, Text = "recent text", ExtractedAt = DateTime.UtcNow });
            await c.SaveChangesAsync();
        }
        try
        {
            await NewBin(Raw()).RunOnceAsync("t");
            await using var c = NewContext();
            Assert.False(await c.AttachmentTexts.AnyAsync(t => t.Sha256 == orphan));
            Assert.True(await c.AttachmentTexts.AnyAsync(t => t.Sha256 == recent));     // too young to judge
        }
        finally { await using var c = NewContext(); await c.AttachmentTexts.Where(t => t.Sha256 == orphan || t.Sha256 == recent).ExecuteDeleteAsync(); }
    }

    // ===== N-08: one operation at a time =====================================================================

    [Fact]
    public async Task ARestoreAndARunNeverOverlap_TheRestoreWaitsOrSaysBusy()
    {
        var (sha, rel) = await WriteOriginalAsync();
        var bin = NewBin(Raw());
        await bin.RunOnceAsync("t");
        // run and restore at once: whichever way they interleave, the original ends up live or in the Trash with a matching entry, never unlisted
        var run = bin.RunOnceAsync("t2");
        var restore = bin.RestoreAsync(sha, "admin");
        await Task.WhenAll(run, restore);

        var live = File.Exists(Abs(rel)); var inBin = File.Exists(Abs(OriginalBin.BinRel(sha)));
        Assert.True(live ^ inBin, "the original must be in exactly one place");
        Assert.Equal(inBin ? 1 : 0, await BinEntriesAsync());
    }

    [Fact]
    public void TheBreakerSettings_AreValidated()
    {
        var raw = new RawMessageOptions();
        raw.OriginalCleanup.BreakerPercent = 0; raw.OriginalCleanup.BreakerMinFiles = 0;
        Assert.Equal(2, OriginalCleanupOptions.Validate(raw).Count);
        var def = new OriginalCleanupOptions();
        Assert.Equal(5, def.BreakerPercent); Assert.Equal(50, def.BreakerMinFiles);
    }
}
