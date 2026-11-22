using MailArchiver.Data;
using MailArchiver.Models;
using MailArchiver.Services.Shared;
using MailArchiver.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Opt = Microsoft.Extensions.Options.Options;

namespace MailArchiver.Tests.Services;

/// <summary>Review findings F-07 (the storage migrator and shared hash-named files) and F-22 (temp names).</summary>
[Collection(TestDbFixture.CollectionName)]
public class ReviewFixesStorageTests : IDisposable
{
    private readonly ReviewKit _kit;
    public ReviewFixesStorageTests(TestDbFixture fixture) => _kit = new ReviewKit(fixture);
    public void Dispose() => _kit.Dispose();

    private RawMessageStorageMigrator Migrator(MailArchiverDbContext ctx, bool allow, bool lean = true)
    {
        var raw = new RawMessageOptions
        {
            Enabled = true, Storage = RawMessageStorage.FileSystem, MaxMessageSizeMb = 0,
            FileSystem = new RawMessageFileSystemOptions { BasePath = _kit.Root, HashNamed = true }
        };
        return new RawMessageStorageMigrator(ctx, Opt.Create(raw), new RawMessagePathResolver(raw.FileSystem, _kit.Root),
            NullLogger<RawMessageStorageMigrator>.Instance,
            Opt.Create(new StorageModeOptions { Mode = lean ? "Lean" : "Standard", AllowHashNamedMigration = allow }));
    }

    // The same message, in two accounts: two emails whose originals are ONE hash-named file.
    private async Task<(MailArchiverDbContext Ctx, MailAccount A, MailAccount B, string Path)> TwoEmailsOneFileAsync()
    {
        var ctx = _kit.NewContext();
        var a = await ReviewKit.SeedAccountAsync(ctx);
        var b = await ReviewKit.SeedAccountAsync(ctx);
        var (msg, bytes, _) = ReviewKit.BuildMail();
        var wired = _kit.Make(ctx, lean: true);
        Assert.True(await wired.Svc.ArchiveEmailAsync(a, msg, false, "INBOX", new RawMessageCapture(bytes, RawMessageSource.Imap)));
        Assert.True(await wired.Svc.ArchiveEmailAsync(b, msg, false, "INBOX", new RawMessageCapture(bytes, RawMessageSource.Imap)));
        ctx.ChangeTracker.Clear();
        var rows = await ctx.RawMessages.AsNoTracking().ToListAsync();
        var mine = rows.Where(r => r.Sha256 == RawMessageCodec.ComputeSha256(bytes)).ToList();
        Assert.Equal(2, mine.Count);
        Assert.Equal(mine[0].StoragePath, mine[1].StoragePath);
        return (ctx, a, b, Path.Combine(_kit.Root, mine[0].StoragePath!));
    }

    [Fact]
    public async Task F07_MigratingHashNamedOriginalsToTheDatabase_IsRefusedUnlessAllowed()
    {
        var (ctx, a, b, path) = await TwoEmailsOneFileAsync();
        try
        {
            var result = await Migrator(ctx, allow: false).MigrateAsync(RawMessageStorage.FileSystem, RawMessageStorage.Postgres, 10);

            Assert.Equal(0, result.Moved);
            Assert.False(result.Success);
            Assert.Contains("refused", result.Errors.Single());
            Assert.True(File.Exists(path));
            ctx.ChangeTracker.Clear();
            Assert.Equal(0, await ctx.RawMessages.CountAsync(r => r.StorageType == RawMessageStorage.Postgres && (r.ArchivedEmail.MailAccountId == a.Id || r.ArchivedEmail.MailAccountId == b.Id)));
        }
        finally { await ReviewKit.CleanupAsync(ctx, a); await Cleanup2Async(b); }
    }

    [Fact]
    public async Task F07_AllowedMigration_NeverDeletesAFileAnotherRowStillUses()
    {
        var (ctx, a, b, path) = await TwoEmailsOneFileAsync();
        try
        {
            var migrator = Migrator(ctx, allow: true);

            // Batch 1 moves one row; the other still needs the shared file.
            var first = await migrator.MigrateAsync(RawMessageStorage.FileSystem, RawMessageStorage.Postgres, batchSize: 1);
            Assert.Equal(1, first.Moved);
            Assert.True(File.Exists(path), "the shared file must survive while another row refers to it");

            // Batch 2 moves the last row: now nothing refers to the file and it may go.
            ctx.ChangeTracker.Clear();
            var second = await migrator.MigrateAsync(RawMessageStorage.FileSystem, RawMessageStorage.Postgres, batchSize: 1);
            Assert.Equal(1, second.Moved);
            Assert.True(second.Success, string.Join("; ", second.Errors));
            Assert.False(File.Exists(path));
        }
        finally { await ReviewKit.CleanupAsync(ctx, a); await Cleanup2Async(b); }
    }

    [Fact]
    public async Task F22_TwoWritersOfTheSameBytes_LeaveOneGoodFileAndNoTempFiles()
    {
        var ctx = _kit.NewContext();
        try
        {
            var wired = _kit.Make(ctx, lean: true);
            var (_, bytes, _) = ReviewKit.BuildMail();
            var capture = new RawMessageCapture(bytes, RawMessageSource.Imap);
            var fs = new FileSystemRawMessageStore(ctx,
                Opt.Create(wired.Raw), new RawMessagePathResolver(wired.Raw.FileSystem, _kit.Root), NullLogger<FileSystemRawMessageStore>.Instance);

            var rows = Enumerable.Range(0, 6).Select(_ => fs.CreateRow(0, capture)!).ToList();
            await Task.WhenAll(rows.Select(r => fs.PersistAsync(r, capture)));

            var files = Directory.EnumerateFiles(_kit.Root, "*", SearchOption.AllDirectories).ToList();
            Assert.Single(files.Where(f => f.EndsWith(".eml.gz")));
            Assert.DoesNotContain(files, f => f.EndsWith(".tmp"));
            Assert.NotNull(await fs.ReadWrittenAsync(rows[0]));
        }
        finally { await ctx.DisposeAsync(); }
    }

    private async Task Cleanup2Async(MailAccount b)
    {
        var c = _kit.NewContext();
        await ReviewKit.CleanupAsync(c, b);
    }
}
