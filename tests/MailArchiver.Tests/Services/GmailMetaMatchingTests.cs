using MailArchiver.Data;
using MailArchiver.Models;
using MailArchiver.Services.Shared;
using MailArchiver.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MailArchiver.Tests.Services;

/// <summary>Audit finding 8: label refresh matches by Gmail id when the row has one; by Message-ID only for rows without a Gmail id (and only if unambiguous).</summary>
[Collection(TestDbFixture.CollectionName)]
public class GmailMetaMatchingTests
{
    private readonly ReviewKit _kit;
    public GmailMetaMatchingTests(TestDbFixture fixture) => _kit = new ReviewKit(fixture);

    private static GmailMeta Meta(string id, params string[] labels) => new(labels.ToList(), id, "t" + id);

    private static ArchivedEmail Row(int accountId, string messageId, string? gid, params string[] labels)
    {
        var e = DeleteReasonTests.NewEmail(accountId, gmailId: gid);
        e.MessageId = messageId;
        e.Labels = labels.ToList();
        return e;
    }

    private GmailMetaService Svc(MailArchiverDbContext ctx) => new(ctx, new MutableOptionsMonitor<IntegrityOptions>(new IntegrityOptions()));

    [Fact]
    public async Task TwoMessagesSharingAMessageId_NeverSwapLabelsOrIds()
    {
        var ctx = _kit.NewContext(); var a = await ReviewKit.SeedAccountAsync(ctx);
        try
        {
            var a1 = Row(a.Id, "<same@x>", "1", "\\Inbox"); var b1 = Row(a.Id, "<same@x>", "2", "Work");
            ctx.ArchivedEmails.AddRange(a1, b1); await ctx.SaveChangesAsync(); ctx.ChangeTracker.Clear();
            var svc = Svc(ctx);

            // Both server messages are refreshed, in both orders, twice: each row keeps its own id and gets only its own labels.
            for (var i = 0; i < 2; i++)
            {
                await svc.ApplyAsync(a.Id, "<same@x>", Meta("1", "\\Inbox", "Marketing"));
                await svc.ApplyAsync(a.Id, "<same@x>", Meta("2", "Work", "Projects"));
            }
            ctx.ChangeTracker.Clear();
            var ra = await ctx.ArchivedEmails.SingleAsync(e => e.Id == a1.Id);
            var rb = await ctx.ArchivedEmails.SingleAsync(e => e.Id == b1.Id);
            Assert.Equal("1", ra.GmailMessageId); Assert.Equal("2", rb.GmailMessageId);
            Assert.Equal(new[] { "Marketing", "\\Inbox" }, ra.Labels!.OrderBy(x => x, StringComparer.Ordinal).ToArray());
            Assert.Equal(new[] { "Projects", "Work" }, rb.Labels!.OrderBy(x => x, StringComparer.Ordinal).ToArray());
        }
        finally { await ReviewKit.CleanupAsync(ctx, a); }
    }

    [Fact]
    public async Task ARowWithAnotherGmailId_IsNotMatchedByMessageId()
    {
        var ctx = _kit.NewContext(); var a = await ReviewKit.SeedAccountAsync(ctx);
        try
        {
            var held = Row(a.Id, "<m@x>", "1", "Keep");
            ctx.ArchivedEmails.Add(held); await ctx.SaveChangesAsync(); ctx.ChangeTracker.Clear();

            Assert.Equal(0, await Svc(ctx).ApplyAsync(a.Id, "<m@x>", Meta("999", "Marketing")));   // a different Gmail message with the same Message-ID

            ctx.ChangeTracker.Clear();
            var r = await ctx.ArchivedEmails.SingleAsync(e => e.Id == held.Id);
            Assert.Equal("1", r.GmailMessageId); Assert.Equal(new[] { "Keep" }, r.Labels!.ToArray());
        }
        finally { await ReviewKit.CleanupAsync(ctx, a); }
    }

    [Fact]
    public async Task ARowWithoutAGmailId_IsFoundByMessageId_WhenExactlyOne_AndGetsTheId()
    {
        var ctx = _kit.NewContext(); var a = await ReviewKit.SeedAccountAsync(ctx);
        try
        {
            var old = Row(a.Id, "<old@x>", null, "x");
            ctx.ArchivedEmails.Add(old); await ctx.SaveChangesAsync(); ctx.ChangeTracker.Clear();

            Assert.Equal(1, await Svc(ctx).ApplyAsync(a.Id, "<old@x>", Meta("55", "\\Inbox")));

            ctx.ChangeTracker.Clear();
            var r = await ctx.ArchivedEmails.SingleAsync(e => e.Id == old.Id);
            Assert.Equal("55", r.GmailMessageId); Assert.Equal(new[] { "\\Inbox" }, r.Labels!.ToArray());
        }
        finally { await ReviewKit.CleanupAsync(ctx, a); }
    }

    [Fact]
    public async Task TwoRowsWithoutAGmailIdSharingAMessageId_AreAmbiguous_AndLeftAlone()
    {
        var ctx = _kit.NewContext(); var a = await ReviewKit.SeedAccountAsync(ctx);
        try
        {
            var r1 = Row(a.Id, "<dup@x>", null, "a"); var r2 = Row(a.Id, "<dup@x>", null, "b");
            ctx.ArchivedEmails.AddRange(r1, r2); await ctx.SaveChangesAsync(); ctx.ChangeTracker.Clear();

            Assert.Equal(0, await Svc(ctx).ApplyAsync(a.Id, "<dup@x>", Meta("7", "Marketing")));

            ctx.ChangeTracker.Clear();
            Assert.All(await ctx.ArchivedEmails.Where(e => e.MailAccountId == a.Id).ToListAsync(), e => { Assert.Null(e.GmailMessageId); Assert.DoesNotContain("Marketing", e.Labels!); });
        }
        finally { await ReviewKit.CleanupAsync(ctx, a); }
    }
}
