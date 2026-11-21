using MailArchiver.Data;
using MailArchiver.Models;
using MailArchiver.Services.Shared;
using MailArchiver.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;
using Opt = Microsoft.Extensions.Options.Options;

namespace MailArchiver.Tests.Services;

/// <summary>The hash-chained audit trail: append, verify, deliberate tampering, the append-only trigger.</summary>
[Collection(TestDbFixture.CollectionName)]
public class AuditChainTests : IAsyncLifetime
{
    private readonly TestDbFixture _fixture;
    public AuditChainTests(TestDbFixture fixture) => _fixture = fixture;

    private MailArchiverDbContext NewContext(params Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor[] interceptors)
        => new(new DbContextOptionsBuilder<MailArchiverDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .AddInterceptors(interceptors)
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning))
            .Options);

    private IAuditChain NewChain(bool enabled = true, params Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor[] interceptors)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => NewContext(interceptors));
        return new AuditChain(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            Opt.Create(new IntegrityOptions { AuditChainEnabled = enabled }), NullLogger<AuditChain>.Instance);
    }

    private async Task ExecAsync(string sql)
    {
        await using var c = new NpgsqlConnection(_fixture.ConnectionString);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        await cmd.ExecuteNonQueryAsync();
    }

    // The chain is global to the test database, so every test starts and ends with it empty.
    private async Task ResetAsync()
    {
        await ExecAsync(@"ALTER TABLE mail_archiver.""AuditChain"" DISABLE TRIGGER USER;
                          DELETE FROM mail_archiver.""AuditChain"";
                          ALTER TABLE mail_archiver.""AuditChain"" ENABLE TRIGGER USER;");
    }

    public Task InitializeAsync() => ResetAsync();
    public Task DisposeAsync() => ResetAsync();

    private async Task<AuditChainReport> VerifyAsync()
    {
        await using var ctx = NewContext();
        return await new AuditChainVerifier(ctx).VerifyAsync();
    }

    [Fact]
    public async Task EmptyChain_IsValid_WithNoHead()
    {
        var r = await VerifyAsync();
        Assert.True(r.Ok);
        Assert.Equal(0, r.Entries);
        Assert.Null(r.Head);
    }

    [Fact]
    public async Task Entries_LinkToEachOther_AndVerify()
    {
        var chain = NewChain();
        for (var i = 0; i < 5; i++) await chain.AppendAsync("test", 100 + i, null, new { n = i });

        var r = await VerifyAsync();
        Assert.True(r.Ok);
        Assert.Equal(5, r.Entries);
        Assert.Equal(5, r.Head!.Seq);

        await using var ctx = NewContext();
        var all = await ctx.AuditChain.AsNoTracking().OrderBy(e => e.Seq).ToListAsync();
        Assert.Equal(AuditChainEntry.GenesisHash, all[0].PrevHash);
        for (var i = 1; i < all.Count; i++) Assert.Equal(all[i - 1].EntryHash, all[i].PrevHash);
    }

    [Fact]
    public async Task Disabled_AppendsNothing()
    {
        Assert.Null(await NewChain(enabled: false).AppendAsync("test"));
        Assert.Equal(0, (await VerifyAsync()).Entries);
    }

    [Fact]
    public async Task ConcurrentAppends_StayOneLinearChain()
    {
        var chain = NewChain();
        await Task.WhenAll(Enumerable.Range(0, 25).Select(i => chain.AppendAsync("concurrent", i, null, new { i })));

        var r = await VerifyAsync();
        Assert.True(r.Ok);
        Assert.Equal(25, r.Entries);
    }

    [Fact]
    public async Task TheDatabaseRefusesUpdateDeleteAndTruncate()
    {
        await NewChain().AppendAsync("test", 1);
        await Assert.ThrowsAnyAsync<Exception>(() => ExecAsync(@"UPDATE mail_archiver.""AuditChain"" SET ""Kind"" = 'x'"));
        await Assert.ThrowsAnyAsync<Exception>(() => ExecAsync(@"DELETE FROM mail_archiver.""AuditChain"""));
        await Assert.ThrowsAnyAsync<Exception>(() => ExecAsync(@"TRUNCATE mail_archiver.""AuditChain"""));
        Assert.Equal(1, (await VerifyAsync()).Entries);
    }

    // Tampering needs the trigger out of the way, which is what an owner of the table could do;
    // the chain must still show it.
    private async Task TamperAsync(string sql)
    {
        await ExecAsync(@"ALTER TABLE mail_archiver.""AuditChain"" DISABLE TRIGGER USER;" + sql + @";
                          ALTER TABLE mail_archiver.""AuditChain"" ENABLE TRIGGER USER;");
    }

    [Fact]
    public async Task ChangedContent_IsReported_AtThatEntry()
    {
        var chain = NewChain();
        for (var i = 0; i < 4; i++) await chain.AppendAsync("test", i, null, new { i });
        await TamperAsync(@"UPDATE mail_archiver.""AuditChain"" SET ""Payload"" = '{""i"":99}' WHERE ""Seq"" = 2");

        var r = await VerifyAsync();
        Assert.False(r.Ok);
        Assert.Equal(2, r.FirstBreak!.Seq);
    }

    [Fact]
    public async Task ChangedContent_WithItsHashRecomputed_IsStillCaughtByTheNextEntry()
    {
        var chain = NewChain();
        for (var i = 0; i < 4; i++) await chain.AppendAsync("test", i, null, new { i });

        // A clever edit: change entry 2 and recompute its own hash. Entry 3 still points at the old hash.
        await using (var ctx = NewContext())
        {
            var e = await ctx.AuditChain.AsNoTracking().SingleAsync(x => x.Seq == 2);
            e.Payload = "{\"i\":99}";
            var newHash = AuditChainHash.Compute(e);
            await TamperAsync($"UPDATE mail_archiver.\"AuditChain\" SET \"Payload\" = '{{\"i\":99}}', \"EntryHash\" = '{newHash}' WHERE \"Seq\" = 2");
        }

        var r = await VerifyAsync();
        Assert.False(r.Ok);
        Assert.Equal(3, r.FirstBreak!.Seq);
    }

    [Fact]
    public async Task RemovedEntry_IsReportedAsAGap()
    {
        var chain = NewChain();
        for (var i = 0; i < 4; i++) await chain.AppendAsync("test", i, null, new { i });
        await TamperAsync(@"DELETE FROM mail_archiver.""AuditChain"" WHERE ""Seq"" = 3");

        var r = await VerifyAsync();
        Assert.False(r.Ok);
        Assert.Equal(3, r.FirstBreak!.Seq);
    }

    [Fact]
    public async Task OriginalsStored_AreChained_WithTheirHash()
    {
        var chain = NewChain();
        var interceptor = new AuditChainInterceptor(
            new ServiceCollection().AddSingleton(chain).BuildServiceProvider());

        MailAccount account;
        await using (var setup = NewContext())
        {
            account = new MailAccount
            {
                Name = $"ac-{Guid.NewGuid():N}".Substring(0, 20), EmailAddress = $"{Guid.NewGuid():N}@test.local",
                Provider = ProviderType.IMAP, IsEnabled = true, LastSync = DateTime.UtcNow
            };
            setup.MailAccounts.Add(account);
            await setup.SaveChangesAsync();
        }

        int emailId;
        var sha = new string('a', 64);
        await using (var ctx = NewContext(interceptor))
        {
            var email = new ArchivedEmail
            {
                MailAccountId = account.Id, MessageId = Guid.NewGuid().ToString("N"), Subject = "s", From = "a@b.test", To = "c@d.test",
                Body = "b", HtmlBody = "", Cc = "", Bcc = "", FolderName = "INBOX", SentDate = DateTime.UtcNow, ReceivedDate = DateTime.UtcNow,
                RawMessage = new RawMessage { Sha256 = sha, Size = 10, Source = "imap", StorageType = RawMessageStorage.Postgres, Content = new byte[] { 1 }, CreatedAt = DateTime.UtcNow }
            };
            ctx.ArchivedEmails.Add(email);
            await ctx.SaveChangesAsync();
            emailId = email.Id;
        }

        try
        {
            await using var read = NewContext();
            var entry = await read.AuditChain.AsNoTracking().SingleAsync(e => e.Kind == "original.stored");
            Assert.Equal(emailId, entry.EmailId);
            Assert.Equal(sha, entry.Sha256);

            var r = await VerifyAsync();
            Assert.True(r.Ok);
            Assert.Equal(0, r.EmailsWithoutArchiveEntry);

            // The hash in the row is changed after the fact: the chain still holds the old one.
            await ExecAsync($"UPDATE mail_archiver.\"RawMessages\" SET \"Sha256\" = '{new string('b', 64)}' WHERE \"ArchivedEmailId\" = {emailId}");
            var after = await VerifyAsync();
            Assert.False(after.Ok);
            Assert.Single(after.Warnings);
        }
        finally
        {
            await using var c = NewContext();
            await c.Database.ExecuteSqlInterpolatedAsync($"UPDATE mail_archiver.\"ArchivedEmails\" SET \"IsLocked\" = false WHERE \"MailAccountId\" = {account.Id}");
            await c.ArchivedEmails.Where(e => e.MailAccountId == account.Id).ExecuteDeleteAsync();
            await c.MailAccounts.Where(a => a.Id == account.Id).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task EmailsStoredBeforeTheChain_AreCountedAsUnchained()
    {
        MailAccount account;
        await using (var setup = NewContext())
        {
            account = new MailAccount
            {
                Name = $"uc-{Guid.NewGuid():N}".Substring(0, 20), EmailAddress = $"{Guid.NewGuid():N}@test.local",
                Provider = ProviderType.IMAP, IsEnabled = true, LastSync = DateTime.UtcNow
            };
            setup.MailAccounts.Add(account);
            await setup.SaveChangesAsync();
            setup.ArchivedEmails.Add(new ArchivedEmail
            {
                MailAccountId = account.Id, MessageId = Guid.NewGuid().ToString("N"), Subject = "s", From = "a@b.test", To = "c@d.test",
                Body = "b", HtmlBody = "", Cc = "", Bcc = "", FolderName = "INBOX", SentDate = DateTime.UtcNow, ReceivedDate = DateTime.UtcNow,
                RawMessage = new RawMessage { Sha256 = new string('c', 64), Size = 10, Source = "imap", StorageType = RawMessageStorage.Postgres, Content = new byte[] { 1 }, CreatedAt = DateTime.UtcNow }
            });
            await setup.SaveChangesAsync();
        }
        try
        {
            var r = await VerifyAsync();
            Assert.Equal(1, r.EmailsWithoutArchiveEntry);
        }
        finally
        {
            await using var c = NewContext();
            await c.Database.ExecuteSqlInterpolatedAsync($"UPDATE mail_archiver.\"ArchivedEmails\" SET \"IsLocked\" = false WHERE \"MailAccountId\" = {account.Id}");
            await c.ArchivedEmails.Where(e => e.MailAccountId == account.Id).ExecuteDeleteAsync();
            await c.MailAccounts.Where(a => a.Id == account.Id).ExecuteDeleteAsync();
        }
    }
}
