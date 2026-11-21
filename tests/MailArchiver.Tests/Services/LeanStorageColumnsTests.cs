using MailArchiver.Models;
using MailArchiver.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MailArchiver.Tests.Services;

/// <summary>Checks the lean-storage columns exist after migration and default to "not lean".</summary>
[Collection(TestDbFixture.CollectionName)]
public class LeanStorageColumnsTests
{
    private readonly TestDbFixture _fixture;
    public LeanStorageColumnsTests(TestDbFixture fixture) => _fixture = fixture;

    [Theory]
    [InlineData("ArchivedEmails", "ContentStripped")]
    [InlineData("EmailAttachments", "PartPath")]
    [InlineData("EmailAttachments", "Sha256")]
    public async Task Column_Exists(string table, string column)
    {
        await using var scope = await _fixture.CreateTransactionalContextAsync();
        var count = await scope.Context.Database
            .SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM information_schema.columns WHERE table_schema = 'mail_archiver' AND table_name = {table} AND column_name = {column}")
            .SingleAsync();
        Assert.Equal(1, count);
        await scope.RollbackAsync();
    }

    [Fact]
    public async Task ContentStripped_DefaultsToFalse_AndLeanFieldsRoundTrip()
    {
        await using var scope = await _fixture.CreateTransactionalContextAsync();
        var ctx = scope.Context;

        var account = new MailAccount
        {
            Name = "lean-test",
            EmailAddress = "lean@example.test",
            Provider = ProviderType.IMAP,
            ImapServer = "imap.example.test",
            ImapPort = 993,
            Username = "u",
            Password = "p",
            IsEnabled = false
        };
        ctx.MailAccounts.Add(account);
        await ctx.SaveChangesAsync();

        var email = new ArchivedEmail
        {
            MailAccountId = account.Id,
            MessageId = "lean-test-1@example.test",
            Subject = "s", Body = "b", HtmlBody = "", From = "a@example.test", To = "b@example.test",
            Cc = "", Bcc = "", FolderName = "INBOX"
        };
        ctx.ArchivedEmails.Add(email);
        await ctx.SaveChangesAsync();
        Assert.False(email.ContentStripped);

        var attachment = new EmailAttachment
        {
            ArchivedEmailId = email.Id, FileName = "a.bin", ContentType = "application/octet-stream",
            Size = 3, PartPath = "b/c1", Sha256 = new string('a', 64)
        };
        ctx.EmailAttachments.Add(attachment);
        await ctx.SaveChangesAsync();

        ctx.ChangeTracker.Clear();
        var loaded = await ctx.EmailAttachments.SingleAsync(a => a.Id == attachment.Id);
        Assert.Equal("b/c1", loaded.PartPath);
        Assert.Equal(new string('a', 64), loaded.Sha256);
        await scope.RollbackAsync();
    }
}
