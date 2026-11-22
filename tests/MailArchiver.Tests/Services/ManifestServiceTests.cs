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

/// <summary>Signed manifests: written, checked, and caught when the header, the entry list or the chain changes.</summary>
[Collection(TestDbFixture.CollectionName)]
public class ManifestServiceTests : IAsyncLifetime
{
    private readonly TestDbFixture _fixture;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "manifest-" + Guid.NewGuid().ToString("N"));
    public ManifestServiceTests(TestDbFixture fixture) => _fixture = fixture;

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

    private Task ResetChainAsync() => ExecAsync(@"ALTER TABLE mail_archiver.""AuditChain"" DISABLE TRIGGER USER;
        DELETE FROM mail_archiver.""AuditChain""; ALTER TABLE mail_archiver.""AuditChain"" ENABLE TRIGGER USER;");

    public Task InitializeAsync() => ResetChainAsync();

    public async Task DisposeAsync()
    {
        await ResetChainAsync();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private (ManifestService Svc, MailArchiverDbContext Db) NewService()
    {
        var options = Opt.Create(new IntegrityOptions { ManifestPath = _dir, ManifestKeyPath = Path.Combine(_dir, "keys", "k.pem") });
        var services = new ServiceCollection();
        services.AddScoped(_ => NewContext());
        var chain = new AuditChain(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), options, NullLogger<AuditChain>.Instance);
        var db = NewContext();
        var env = new Mock<IHostEnvironment>();
        env.SetupGet(e => e.ContentRootPath).Returns(Path.GetTempPath());
        return (new ManifestService(db, chain, new AuditChainVerifier(db), options, env.Object, NullLogger<ManifestService>.Instance), db);
    }

    private async Task<MailAccount> SeedEmailAsync()
    {
        await using var ctx = NewContext();
        var account = new MailAccount
        {
            Name = $"mf-{Guid.NewGuid():N}".Substring(0, 20), EmailAddress = $"{Guid.NewGuid():N}@test.local",
            Provider = ProviderType.IMAP, IsEnabled = true, LastSync = DateTime.UtcNow
        };
        ctx.MailAccounts.Add(account);
        await ctx.SaveChangesAsync();
        ctx.ArchivedEmails.Add(new ArchivedEmail
        {
            MailAccountId = account.Id, MessageId = Guid.NewGuid().ToString("N"), Subject = "s", From = "a@b.test", To = "c@d.test",
            Body = "b", HtmlBody = "", Cc = "", Bcc = "", FolderName = "INBOX", SentDate = DateTime.UtcNow, ReceivedDate = DateTime.UtcNow,
            RawMessage = new RawMessage { Sha256 = new string('d', 64), Size = 10, Source = "imap", StorageType = RawMessageStorage.Postgres, Content = new byte[] { 1 }, CreatedAt = DateTime.UtcNow }
        });
        await ctx.SaveChangesAsync();
        return account;
    }

    private async Task CleanupAsync(MailAccount account)
    {
        await using var c = NewContext();
        await c.Database.ExecuteSqlInterpolatedAsync($"UPDATE mail_archiver.\"ArchivedEmails\" SET \"IsLocked\" = false WHERE \"MailAccountId\" = {account.Id}");
        await c.ArchivedEmails.Where(e => e.MailAccountId == account.Id).ExecuteDeleteAsync();
        await c.MailAccounts.Where(a => a.Id == account.Id).ExecuteDeleteAsync();
    }

    [Fact]
    public async Task Created_Manifest_VerifiesAndIsReadableWithThePublicKeyAlone()
    {
        var account = await SeedEmailAsync();
        try
        {
            var (svc, db) = NewService();
            using var _ = db;
            var info = await svc.CreateAsync("test");
            Assert.True(info.Entries >= 1);

            var check = await svc.VerifyAsync(info.Name);
            Assert.NotNull(check);
            Assert.True(check!.Ok, string.Join("; ", check.Problems));
            Assert.True(check.SignatureValid);

            // What an outsider does: public key + header + signature, nothing of ours.
            using var pub = ECDsa.Create();
            pub.ImportFromPem(File.ReadAllText(Path.Combine(_dir, "public-key.pem")));
            Assert.True(pub.VerifyData(File.ReadAllBytes(Path.Combine(_dir, info.Name + ".json")),
                File.ReadAllBytes(Path.Combine(_dir, info.Name + ".sig")), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));

            Assert.Single(await svc.ListAsync());
        }
        finally { await CleanupAsync(account); }
    }

    [Fact]
    public async Task ChangedHeader_FailsTheSignature()
    {
        var (svc, db) = NewService();
        using var _ = db;
        var info = await svc.CreateAsync("test");
        var path = Path.Combine(_dir, info.Name + ".json");
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"version\": 1", "\"version\": 2"));

        var check = await svc.VerifyAsync(info.Name);
        Assert.False(check!.Ok);
        Assert.False(check.SignatureValid);
    }

    [Fact]
    public async Task ChangedEntryList_IsCaught_EvenWithAValidSignature()
    {
        var account = await SeedEmailAsync();
        try
        {
            var (svc, db) = NewService();
            using var _ = db;
            var info = await svc.CreateAsync("test");
            var path = Path.Combine(_dir, info.Name + ".jsonl");
            File.WriteAllText(path, File.ReadAllText(path).Replace(new string('d', 64), new string('e', 64)));

            var check = await svc.VerifyAsync(info.Name);
            Assert.False(check!.Ok);
            Assert.True(check.SignatureValid);
            Assert.False(check.EntriesFileMatches);
        }
        finally { await CleanupAsync(account); }
    }

    [Fact]
    public async Task OriginalChangedAfterTheManifest_IsCounted()
    {
        var account = await SeedEmailAsync();
        try
        {
            var (svc, db) = NewService();
            using var _ = db;
            var info = await svc.CreateAsync("test");
            await ExecAsync($"UPDATE mail_archiver.\"RawMessages\" SET \"Sha256\" = '{new string('f', 64)}' WHERE \"ArchivedEmailId\" IN (SELECT \"Id\" FROM mail_archiver.\"ArchivedEmails\" WHERE \"MailAccountId\" = {account.Id})");

            var check = await svc.VerifyAsync(info.Name);
            Assert.True(check!.EmailsChangedOrMissingSince >= 1);
            // F-05: a changed hash is not "ok" any more (the old assertion only counted it).
            Assert.False(check.Ok);
            Assert.True(check.EmailsChanged >= 1);
        }
        finally { await CleanupAsync(account); }
    }

    [Fact]
    public async Task ChainEntryRemoved_IsCaught()
    {
        var (svc, db) = NewService();
        using var _ = db;
        // Give the chain a head the manifest can point at (the manifest itself is chained after the head is read).
        var chain = new AuditChain(new ServiceCollection().AddScoped(_ => NewContext()).BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            Opt.Create(new IntegrityOptions()), NullLogger<AuditChain>.Instance);
        await chain.AppendAsync("test");
        var info = await svc.CreateAsync("test");
        Assert.Equal(1, info.ChainSeq);

        await ExecAsync(@"ALTER TABLE mail_archiver.""AuditChain"" DISABLE TRIGGER USER;
            UPDATE mail_archiver.""AuditChain"" SET ""EntryHash"" = repeat('9', 64) WHERE ""Seq"" = 1;
            ALTER TABLE mail_archiver.""AuditChain"" ENABLE TRIGGER USER;");

        var check = await svc.VerifyAsync(info.Name);
        Assert.False(check!.Ok);
        Assert.False(check.ChainStillContainsHead);
    }

    [Theory]
    [InlineData("../etc/passwd")]
    [InlineData("manifest-1")]
    [InlineData("")]
    public async Task OddNames_AreRefused_NotResolvedAsPaths(string name)
    {
        var (svc, db) = NewService();
        using var _ = db;
        Assert.Null(await svc.VerifyAsync(name));
    }

    [Fact]
    public async Task PublicKeyFingerprint_IsNullBeforeTheFirstManifest_ThenTheHashOfTheDerKey()
    {
        var (svc, db) = NewService();
        using var _ = db;
        Assert.Null(svc.GetPublicKeyFingerprint());

        await svc.CreateAsync("test");
        var fp = svc.GetPublicKeyFingerprint();

        using var pub = ECDsa.Create();
        pub.ImportFromPem(File.ReadAllText(Path.Combine(_dir, "public-key.pem")));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(pub.ExportSubjectPublicKeyInfo())).ToLowerInvariant(), fp);
    }

    [Fact]
    public async Task F05_ADeletionTheChainRecords_DoesNotFailTheManifest()
    {
        var account = await SeedEmailAsync();
        var (svc, db) = NewService();
        using var _ = db;
        var info = await svc.CreateAsync("test");
        await CleanupAsync(account);   // deletes through the database: the trigger chains it

        var check = await svc.VerifyAsync(info.Name);
        Assert.True(check!.EmailsChangedOrMissingSince >= 1);
        Assert.Equal(0, check.EmailsMissingUnexplained);
        Assert.True(check.Ok, string.Join("; ", check.Problems));
    }

    [Fact]
    public async Task F05_AnEmailThatVanishedWithoutADeletionRecord_FailsTheManifest()
    {
        var account = await SeedEmailAsync();
        var (svc, db) = NewService();
        using var _ = db;
        var info = await svc.CreateAsync("test");

        // Someone with enough rights switches the deletion trigger off and deletes.
        await ExecAsync(@"ALTER TABLE mail_archiver.""ArchivedEmails"" DISABLE TRIGGER audit_chain_email_deleted;
            UPDATE mail_archiver.""ArchivedEmails"" SET ""IsLocked"" = false WHERE ""MailAccountId"" = " + account.Id + @";
            DELETE FROM mail_archiver.""ArchivedEmails"" WHERE ""MailAccountId"" = " + account.Id + @";
            ALTER TABLE mail_archiver.""ArchivedEmails"" ENABLE TRIGGER audit_chain_email_deleted;");
        await using (var c = NewContext())
            await c.MailAccounts.Where(a => a.Id == account.Id).ExecuteDeleteAsync();

        var check = await svc.VerifyAsync(info.Name);
        Assert.False(check!.Ok);
        Assert.True(check.EmailsMissingUnexplained >= 1);
    }

    [Fact]
    public async Task F12_ALostKeyIsNotSilentlyReplaced_WhenManifestsExist()
    {
        var (svc, db) = NewService();
        using var _ = db;
        await svc.CreateAsync("test");
        File.Delete(Path.Combine(_dir, "keys", "k.pem"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.CreateAsync("test"));
    }

    [Fact]
    public async Task F23_TheSigningKeyIsCreatedOwnerOnly()
    {
        if (OperatingSystem.IsWindows()) return;
        var (svc, db) = NewService();
        using var _ = db;
        await svc.CreateAsync("test");
        var mode = File.GetUnixFileMode(Path.Combine(_dir, "keys", "k.pem"));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
    }
}
