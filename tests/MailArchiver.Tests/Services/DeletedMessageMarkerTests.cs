using System.Text.Json;
using MailArchiver.Data;
using MailArchiver.Models;
using MailArchiver.Services.Shared;
using MailArchiver.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace MailArchiver.Tests.Services;

/// <summary>
/// Delete memory, step 2: the database writes the marker in the same transaction as the delete and the chain entry, only for the
/// reasons user and retention; the chain stays verifiable (the trigger's hash equals the application's).
/// </summary>
[Collection(TestDbFixture.CollectionName)]
public class DeletedMessageMarkerTests : IAsyncLifetime, IDisposable
{
    private readonly TestDbFixture _fixture;
    private readonly ReviewKit _kit;
    public DeletedMessageMarkerTests(TestDbFixture fixture) { _fixture = fixture; _kit = new ReviewKit(fixture); }
    public void Dispose() => _kit.Dispose();
    public Task InitializeAsync() => _kit.ResetChainAsync();
    public Task DisposeAsync() => _kit.ResetChainAsync();

    private static ArchivedEmail Email(int accountId, string? gmailId = null, string folder = "INBOX", string sha = "ab", int size = 1234, string? messageId = null)
    {
        var e = DeleteReasonTests.NewEmail(accountId, gmailId, folder);
        if (messageId != null) e.MessageId = messageId;
        e.RawMessage = new RawMessage { Sha256 = sha.PadRight(64, '0'), Size = size, Source = "imap", StorageType = RawMessageStorage.Postgres, Content = new byte[] { 1 }, CreatedAt = DateTime.UtcNow };
        return e;
    }

    private static async Task DeleteWithAsync(MailArchiverDbContext ctx, string? reason, params ArchivedEmail[] emails)
    {
        // Like the production paths: the email is loaded WITHOUT its original (EF would otherwise delete a tracked RawMessage row
        // first, and the trigger could no longer read the hash and size; the database cascade removes it after the trigger).
        var ids = emails.Select(e => e.Id).ToList();
        ctx.ChangeTracker.Clear();
        async Task Do()
        {
            ctx.ArchivedEmails.RemoveRange(await ctx.ArchivedEmails.Where(e => ids.Contains(e.Id)).ToListAsync());
            await ctx.SaveChangesAsync();
        }
        if (reason == null) await Do(); else await DeleteReasonScope.RunAsync(ctx, reason, Do);
    }

    private static async Task<AuditChainEntry> EntryOf(MailArchiverDbContext ctx, int emailId)
        => await ctx.AuditChain.AsNoTracking().SingleAsync(c => c.Kind == "email.deleted" && c.EmailId == emailId);

    private static bool HasMarkerId(AuditChainEntry e) => JsonDocument.Parse(e.Payload).RootElement.TryGetProperty("markerId", out _);

    private async Task<AuditChainReport> VerifyAsync()
    {
        await using var ctx = _kit.NewContext();
        return await new AuditChainVerifier(ctx).VerifyAsync();
    }

    [Theory]
    [InlineData("user")]
    [InlineData("retention")]
    public async Task DeliberateDelete_WritesAMarker_WithTheChainSeq_AndTheChainVerifies(string reason)
    {
        var ctx = _kit.NewContext();
        var account = await ReviewKit.SeedAccountAsync(ctx);
        try
        {
            var e = Email(account.Id, gmailId: "1700000000000000001", folder: "[Gmail]/All Mail", sha: "5a", size: 4321);
            ctx.ArchivedEmails.Add(e);
            await ctx.SaveChangesAsync();
            await DeleteWithAsync(ctx, reason, e);

            await using var read = _kit.NewContext();
            var marker = await read.DeletedMessageMarkers.AsNoTracking().SingleAsync(m => m.AccountId == account.Id);
            var entry = await EntryOf(read, e.Id);
            Assert.Equal(e.MessageId, marker.MessageId);
            Assert.Equal("[Gmail]/All Mail", marker.Folder);
            Assert.Equal(4321, marker.Size);
            Assert.Equal("5a".PadRight(64, '0'), marker.Sha256);
            Assert.Equal("1700000000000000001", marker.GmailMessageId);
            Assert.Equal(reason, marker.Reason);
            Assert.Equal(entry.Seq, marker.ChainSeq);
            Assert.Equal(marker.Id, JsonDocument.Parse(entry.Payload).RootElement.GetProperty("markerId").GetInt32());
            Assert.Equal(reason, JsonDocument.Parse(entry.Payload).RootElement.GetProperty("reason").GetString());

            var report = await VerifyAsync();
            Assert.True(report.Ok, string.Join("; ", report.Warnings) + report.FirstBreak?.Reason); // trigger hash == application hash
        }
        finally { await ReviewKit.CleanupAsync(ctx, account); }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("account")]
    [InlineData("internal")]
    [InlineData("something-else")]
    public async Task OtherReasons_WriteNoMarker_AndTheEntryKeepsTheOldPayloadShape(string? reason)
    {
        var ctx = _kit.NewContext();
        var account = await ReviewKit.SeedAccountAsync(ctx);
        try
        {
            var e = Email(account.Id);
            ctx.ArchivedEmails.Add(e);
            await ctx.SaveChangesAsync();
            await DeleteWithAsync(ctx, reason, e);

            await using var read = _kit.NewContext();
            Assert.False(await read.DeletedMessageMarkers.AnyAsync(m => m.AccountId == account.Id));
            Assert.False(HasMarkerId(await EntryOf(read, e.Id)));
            Assert.True((await VerifyAsync()).Ok);
        }
        finally { await ReviewKit.CleanupAsync(ctx, account); }
    }

    [Fact]
    public async Task AnEmailWithoutAStoredOriginal_StillGetsAMarker_WithSizeZeroAndNoHash()
    {
        var ctx = _kit.NewContext();
        var account = await ReviewKit.SeedAccountAsync(ctx);
        try
        {
            var e = DeleteReasonTests.NewEmail(account.Id, gmailId: "42");
            ctx.ArchivedEmails.Add(e);
            await ctx.SaveChangesAsync();
            await DeleteWithAsync(ctx, "user", e);

            await using var read = _kit.NewContext();
            var m = await read.DeletedMessageMarkers.AsNoTracking().SingleAsync(x => x.AccountId == account.Id);
            Assert.Equal(0, m.Size);
            Assert.Null(m.Sha256);
            Assert.Equal("42", m.GmailMessageId);
        }
        finally { await ReviewKit.CleanupAsync(ctx, account); }
    }

    [Fact]
    public async Task TwoEmailsWithTheSameKey_DeletedTogether_DoNotFail_AndLeaveOneMarker()
    {
        var ctx = _kit.NewContext();
        var account = await ReviewKit.SeedAccountAsync(ctx);
        try
        {
            var a = Email(account.Id, messageId: "<same@dm.test>"); var b = Email(account.Id, messageId: "<same@dm.test>");
            ctx.ArchivedEmails.AddRange(a, b);
            await ctx.SaveChangesAsync();
            await DeleteWithAsync(ctx, "user", a, b);

            await using var read = _kit.NewContext();
            var m = await read.DeletedMessageMarkers.AsNoTracking().SingleAsync(x => x.AccountId == account.Id);
            var last = (await EntryOf(read, a.Id)).Seq > (await EntryOf(read, b.Id)).Seq ? (await EntryOf(read, a.Id)).Seq : (await EntryOf(read, b.Id)).Seq;
            Assert.Equal(last, m.ChainSeq);                     // the marker points at the latest deletion entry
            Assert.True((await VerifyAsync()).Ok);
        }
        finally { await ReviewKit.CleanupAsync(ctx, account); }
    }

    [Fact]
    public async Task ARolledBackDelete_LeavesNoMarkerAndNoChainEntry()
    {
        await using var scope = await _fixture.CreateTransactionalContextAsync();
        var ctx = scope.Context;
        var account = await ReviewKit.SeedAccountAsync(ctx);
        var e = Email(account.Id);
        ctx.ArchivedEmails.Add(e);
        await ctx.SaveChangesAsync();
        await DeleteWithAsync(ctx, "user", e);
        Assert.Equal(1, await ctx.DeletedMessageMarkers.CountAsync(m => m.AccountId == account.Id));
        await scope.RollbackAsync();

        await using var read = _kit.NewContext();
        Assert.Equal(0, await read.DeletedMessageMarkers.CountAsync(m => m.AccountId == account.Id));
        Assert.Equal(0, await read.AuditChain.CountAsync(c => c.Kind == "email.deleted" && c.EmailId == e.Id));
    }

    [Fact]
    public async Task ALockedEmailThatTheTriggerRefuses_LeavesNoMarker()
    {
        var ctx = _kit.NewContext();
        var account = await ReviewKit.SeedAccountAsync(ctx);
        try
        {
            var e = Email(account.Id); e.IsLocked = true;
            ctx.ArchivedEmails.Add(e);
            await ctx.SaveChangesAsync();
            await Assert.ThrowsAnyAsync<Exception>(() => DeleteWithAsync(ctx, "user", e));

            await using var read = _kit.NewContext();
            Assert.Equal(0, await read.DeletedMessageMarkers.CountAsync(m => m.AccountId == account.Id));
            Assert.True(await read.ArchivedEmails.AnyAsync(x => x.Id == e.Id));
        }
        finally { await ReviewKit.CleanupAsync(ctx, account); }
    }

    [Fact]
    public async Task DeletingTheAccount_RemovesItsMarkers_AFullReset()
    {
        var ctx = _kit.NewContext();
        var account = await ReviewKit.SeedAccountAsync(ctx);
        var e = Email(account.Id);
        ctx.ArchivedEmails.Add(e);
        await ctx.SaveChangesAsync();
        await DeleteWithAsync(ctx, "user", e);
        Assert.Equal(1, await ctx.DeletedMessageMarkers.CountAsync(m => m.AccountId == account.Id));

        ctx.ChangeTracker.Clear();
        await ctx.MailAccounts.Where(a => a.Id == account.Id).ExecuteDeleteAsync();
        Assert.Equal(0, await ctx.DeletedMessageMarkers.CountAsync(m => m.AccountId == account.Id));
        await ctx.DisposeAsync();
    }

    [Fact]
    public void TheMarkerTable_HasNoSubjectSenderOrBodyColumn()
    {
        var names = typeof(DeletedMessageMarker).GetProperties().Select(p => p.Name).ToHashSet();
        foreach (var banned in new[] { "Subject", "From", "To", "Cc", "Bcc", "Body", "HtmlBody" })
            Assert.DoesNotContain(banned, names);
    }
}
