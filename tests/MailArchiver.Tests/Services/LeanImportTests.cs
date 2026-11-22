using MailArchiver.Data;
using MailArchiver.Models;
using MailArchiver.Services;
using MailArchiver.Services.Providers;
using MailArchiver.Services.Providers.Eml;
using MailArchiver.Services.Shared;
using MailArchiver.Tests.Infrastructure;
using MailArchiver.Utilities;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;
using Moq;
using Npgsql;
using Xunit;
using Opt = Microsoft.Extensions.Options.Options;

namespace MailArchiver.Tests.Services;

public class MboxRefusalPolicyTests
{
    [Fact]
    public void Standard_AllowsMbox()
        => Assert.Null(new StorageModeOptions().MboxImportRefusal(new RawMessageOptions()));

    [Fact]
    public void Lean_RefusesMbox_WithAClearMessage()
    {
        var msg = new StorageModeOptions { Mode = "Lean" }.MboxImportRefusal(new RawMessageOptions());
        Assert.NotNull(msg);
        Assert.Contains("Storage:Mode=Lean", msg);
        Assert.Contains("Storage:AllowSerializedImports", msg);
    }

    [Fact]
    public void Required_RefusesMbox()
    {
        var msg = new StorageModeOptions().MboxImportRefusal(new RawMessageOptions { Enabled = true, Required = true });
        Assert.NotNull(msg);
        Assert.Contains("RawMessages:Required", msg);
    }

    [Fact]
    public void AllowSerializedImports_OpensTheDoor_InBothModes()
    {
        Assert.Null(new StorageModeOptions { Mode = "Lean", AllowSerializedImports = true }.MboxImportRefusal(new RawMessageOptions()));
        Assert.Null(new StorageModeOptions { AllowSerializedImports = true }.MboxImportRefusal(new RawMessageOptions { Required = true }));
    }
}

/// <summary>
/// EML import, the Graph lean decision and the mbox refusal, on real Postgres with synthetic mail.
/// (The Graph archiver itself needs a Graph client, so the Graph lean step is tested through the
/// shared LeanArchive helper it calls, with a parsed copy built the way the Graph archiver builds it.)
/// </summary>
[Collection(TestDbFixture.CollectionName)]
public class LeanImportTests : IAsyncLifetime
{
    private readonly TestDbFixture _fixture;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "leanimp-" + Guid.NewGuid().ToString("N"));
    public LeanImportTests(TestDbFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => ResetChainAsync();

    public async Task DisposeAsync()
    {
        await ResetChainAsync();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private async Task ExecAsync(string sql)
    {
        await using var c = new NpgsqlConnection(_fixture.ConnectionString);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        await cmd.ExecuteNonQueryAsync();
    }

    private Task ResetChainAsync() => ExecAsync(@"ALTER TABLE mail_archiver.""AuditChain"" DISABLE TRIGGER USER;
        DELETE FROM mail_archiver.""AuditChain""; ALTER TABLE mail_archiver.""AuditChain"" ENABLE TRIGGER USER;");

    private MailArchiverDbContext NewContext(params Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor[] extra)
        => new(new DbContextOptionsBuilder<MailArchiverDbContext>()
            .UseNpgsql(_fixture.ConnectionString, npgsql => npgsql.CommandTimeout(120))
            .AddInterceptors(new AttachmentDeduplicationInterceptor(NullLogger<AttachmentDeduplicationInterceptor>.Instance))
            .AddInterceptors(extra)
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning))
            .Options);

    private RawMessageOptions Raw(bool required = false) => new()
    {
        Enabled = true, Storage = RawMessageStorage.FileSystem, MaxMessageSizeMb = 0, Required = required,
        FileSystem = new RawMessageFileSystemOptions { BasePath = _root, HashNamed = true }
    };

    private sealed record Stack(MailImporter Importer, LeanIngest Ingest, StrippedContentHydrator Hydrator, IntegrityService Integrity,
        MailArchiverDbContext Ctx, IRawMessageStore Store, RawMessageOptions Raw, StorageModeOptions Mode);

    private Stack Build(RawMessageOptions raw, StorageModeOptions mode, bool chain = false)
    {
        var ctx = NewContext();
        var opts = Opt.Create(raw);
        var fs = new FileSystemRawMessageStore(ctx, opts, new RawMessagePathResolver(raw.FileSystem, _root), NullLogger<FileSystemRawMessageStore>.Instance);
        var router = new RawMessageStoreRouter(ctx, opts, new PostgresRawMessageStore(ctx, opts), fs);

        var services = new ServiceCollection();
        services.AddSingleton(new DateTimeHelper(Opt.Create(new TimeZoneOptions { DisplayTimeZoneId = "Europe/Berlin" })));
        if (chain)
        {
            var chainServices = new ServiceCollection();
            chainServices.AddScoped(_ => NewContext());
            var writer = new AuditChain(chainServices.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
                Opt.Create(new IntegrityOptions()), NullLogger<AuditChain>.Instance);
            var interceptor = new AuditChainInterceptor(new ServiceCollection().AddSingleton<IAuditChain>(writer).BuildServiceProvider());
            services.AddScoped(_ => NewContext(interceptor));
        }
        else
        {
            services.AddScoped(_ => NewContext());
        }

        var ingest = new LeanIngest(fs, opts, NullLogger<LeanIngest>.Instance);
        var importer = new MailImporter(services.BuildServiceProvider(), NullLogger<MailImporter>.Instance,
            new EmlAttachmentCollector(NullLogger<EmlAttachmentCollector>.Instance), router, opts, Opt.Create(mode), ingest);
        return new Stack(importer, ingest, new StrippedContentHydrator(ctx, new OriginalMessageReader(router)),
            new IntegrityService(ctx, router), ctx, router, raw, mode);
    }

    private static (MimeMessage Message, byte[] Bytes, byte[] Pdf) Mail(bool twoSenders = false, byte[]? pdf = null)
    {
        pdf ??= Enumerable.Range(0, 4000).Select(i => (byte)(i * 11)).ToArray();
        var alt = new MultipartAlternative { new TextPart("plain") { Text = "import plain words" }, new TextPart("html") { Text = "<p>import html</p>" } };
        var mixed = new Multipart("mixed") { alt, new MimePart("application", "pdf")
        {
            Content = new MimeContent(new MemoryStream(pdf)),
            ContentDisposition = new ContentDisposition(ContentDisposition.Attachment) { FileName = "doc.pdf" },
            ContentTransferEncoding = ContentEncoding.Base64
        } };
        var m = new MimeMessage();
        m.From.Add(new MailboxAddress("", "first@example.test"));
        if (twoSenders) m.From.Add(new MailboxAddress("", "second@example.test"));
        m.To.Add(new MailboxAddress("", "to@example.test"));
        m.Subject = "import " + Guid.NewGuid().ToString("N");
        m.MessageId = $"{Guid.NewGuid():N}@example.test";
        m.Body = mixed;
        using var ms = new MemoryStream();
        m.WriteTo(ms);
        var bytes = ms.ToArray();
        using var read = new MemoryStream(bytes);
        return (MimeMessage.Load(read), bytes, pdf);
    }

    private static async Task<MailAccount> SeedAccountAsync(MailArchiverDbContext ctx)
    {
        var a = new MailAccount
        {
            Name = $"li-{Guid.NewGuid():N}".Substring(0, 20), EmailAddress = $"{Guid.NewGuid():N}@test.local",
            Provider = ProviderType.IMAP, IsEnabled = true, LastSync = DateTime.UtcNow
        };
        ctx.MailAccounts.Add(a);
        await ctx.SaveChangesAsync();
        return a;
    }

    private static async Task CleanupAsync(MailArchiverDbContext ctx, MailAccount account)
    {
        ctx.ChangeTracker.Clear();
        await ctx.Database.ExecuteSqlInterpolatedAsync($"UPDATE mail_archiver.\"ArchivedEmails\" SET \"IsLocked\" = false WHERE \"MailAccountId\" = {account.Id}");
        await ctx.ArchivedEmails.Where(e => e.MailAccountId == account.Id).ExecuteDeleteAsync();
        await ctx.MailAccounts.Where(a => a.Id == account.Id).ExecuteDeleteAsync();
    }

    private static StorageModeOptions Lean() => new() { Mode = "Lean" };

    [Fact]
    public async Task EmlImport_Lean_StoresALeanRow_AndTheOriginalGivesEverythingBack()
    {
        var s = Build(Raw(), Lean());
        var account = await SeedAccountAsync(s.Ctx);
        try
        {
            var (msg, bytes, pdf) = Mail();
            var result = await s.Importer.ImportEmailToDatabase(msg, account, "job", "INBOX", new RawMessageCapture(bytes, RawMessageSource.EmlFile));
            Assert.True(result.Success, result.Error);

            s.Ctx.ChangeTracker.Clear();
            var email = await s.Ctx.ArchivedEmails.Include(e => e.Attachments).Include(e => e.RawMessage).SingleAsync(e => e.MailAccountId == account.Id);
            Assert.True(email.ContentStripped);
            Assert.Equal(string.Empty, email.HtmlBody);
            Assert.Contains("import plain words", email.Body);
            var att = Assert.Single(email.Attachments);
            Assert.False(string.IsNullOrEmpty(att.PartPath));
            Assert.Equal(RawMessageCodec.ComputeSha256(pdf), att.Sha256);
            Assert.Null(att.AttachmentContentId);
            Assert.Null(att.LegacyContent);
            Assert.Equal(0, await s.Ctx.AttachmentContents.CountAsync(c => c.Hash == RawMessageCodec.ComputeSha256(pdf)));
            Assert.Equal(RawMessageSource.EmlFile, email.RawMessage!.Source);
            Assert.EndsWith($"{RawMessageCodec.ComputeSha256(bytes)}.eml.gz", email.RawMessage.StoragePath);

            await s.Hydrator.HydrateAsync(email);
            Assert.Contains("import html", email.HtmlBody);
            Assert.Equal(pdf, Assert.Single(email.Attachments).Content);

            Assert.Equal(IntegrityStatus.Valid, (await s.Integrity.VerifyAsync(email.Id)).Status);
        }
        finally { await CleanupAsync(s.Ctx, account); }
    }

    [Fact]
    public async Task EmlImport_Lean_IndexFieldsComeFromTheOriginal_SoAMultiSenderMailStaysValid()
    {
        // The import's own parse joins every sender; the integrity check (and IMAP) use the first.
        // Without re-deriving from the verified original this email would read as Inconsistent.
        var s = Build(Raw(), Lean());
        var account = await SeedAccountAsync(s.Ctx);
        try
        {
            var (msg, bytes, _) = Mail(twoSenders: true);
            Assert.True((await s.Importer.ImportEmailToDatabase(msg, account, "job", "INBOX", new RawMessageCapture(bytes, RawMessageSource.EmlFile))).Success);

            s.Ctx.ChangeTracker.Clear();
            var email = await s.Ctx.ArchivedEmails.SingleAsync(e => e.MailAccountId == account.Id);
            Assert.True(email.ContentStripped);
            Assert.Equal("first@example.test", email.From);
            Assert.Equal(IntegrityStatus.Valid, (await s.Integrity.VerifyAsync(email.Id)).Status);
        }
        finally { await CleanupAsync(s.Ctx, account); }
    }

    // The parsed copy holds one PDF, the stored original another: the attachment cannot be found again.
    private static (MimeMessage Message, byte[] OriginalBytes) Mismatched()
    {
        var (msg, _, _) = Mail(pdf: Enumerable.Range(0, 3000).Select(i => (byte)(i * 3)).ToArray());
        var (_, otherBytes, _) = Mail(pdf: Enumerable.Range(0, 3000).Select(i => (byte)(i * 5)).ToArray());
        return (msg, otherBytes);
    }

    [Fact]
    public async Task EmlImport_Lean_WhenAnAttachmentCannotBeFoundAgain_IsStoredInFull_NotDropped()
    {
        var s = Build(Raw(), Lean());
        var account = await SeedAccountAsync(s.Ctx);
        try
        {
            var (msg, original) = Mismatched();
            Assert.True((await s.Importer.ImportEmailToDatabase(msg, account, "job", "INBOX", new RawMessageCapture(original, RawMessageSource.EmlFile))).Success);

            s.Ctx.ChangeTracker.Clear();
            var email = await s.Ctx.ArchivedEmails.Include(e => e.Attachments).ThenInclude(a => a.AttachmentContent)
                .SingleAsync(e => e.MailAccountId == account.Id);
            Assert.False(email.ContentStripped);
            Assert.Contains("import html", email.HtmlBody);
            Assert.NotEmpty(Assert.Single(email.Attachments).Content);
        }
        finally { await CleanupAsync(s.Ctx, account); }
    }

    [Fact]
    public async Task EmlImport_Lean_WithRequired_RefusesWhatItCannotVerify_AndSavesNothing()
    {
        var s = Build(Raw(required: true), Lean());
        var account = await SeedAccountAsync(s.Ctx);
        try
        {
            var (msg, original) = Mismatched();
            var result = await s.Importer.ImportEmailToDatabase(msg, account, "job", "INBOX", new RawMessageCapture(original, RawMessageSource.EmlFile));
            Assert.False(result.Success);

            s.Ctx.ChangeTracker.Clear();
            Assert.Equal(0, await s.Ctx.ArchivedEmails.CountAsync(e => e.MailAccountId == account.Id));
        }
        finally { await CleanupAsync(s.Ctx, account); }
    }

    [Fact]
    public async Task EmlImport_Standard_IsUnchanged_FullRowAndAnOriginalOnTheSide()
    {
        var s = Build(Raw(), new StorageModeOptions());
        var account = await SeedAccountAsync(s.Ctx);
        try
        {
            var (msg, bytes, pdf) = Mail();
            Assert.True((await s.Importer.ImportEmailToDatabase(msg, account, "job", "INBOX", new RawMessageCapture(bytes, RawMessageSource.EmlFile))).Success);

            s.Ctx.ChangeTracker.Clear();
            var email = await s.Ctx.ArchivedEmails.Include(e => e.Attachments).ThenInclude(a => a.AttachmentContent).Include(e => e.RawMessage)
                .SingleAsync(e => e.MailAccountId == account.Id);
            Assert.False(email.ContentStripped);
            Assert.Contains("import html", email.HtmlBody);
            Assert.Equal(pdf, Assert.Single(email.Attachments).Content);
            Assert.Null(email.Attachments.Single().PartPath);
            Assert.NotNull(email.RawMessage);
            Assert.Equal(IntegrityStatus.Valid, (await s.Integrity.VerifyAsync(email.Id)).Status);
        }
        finally { await CleanupAsync(s.Ctx, account); }
    }

    [Fact]
    public async Task EmlImport_Lean_IsChained_WithTheOriginalsHash()
    {
        var s = Build(Raw(), Lean(), chain: true);
        var account = await SeedAccountAsync(s.Ctx);
        try
        {
            var (msg, bytes, _) = Mail();
            Assert.True((await s.Importer.ImportEmailToDatabase(msg, account, "job", "INBOX", new RawMessageCapture(bytes, RawMessageSource.EmlFile))).Success);

            s.Ctx.ChangeTracker.Clear();
            var email = await s.Ctx.ArchivedEmails.SingleAsync(e => e.MailAccountId == account.Id);
            await using var read = NewContext();
            var entry = await read.AuditChain.AsNoTracking().SingleAsync(e => e.Kind == "original.stored" && e.EmailId == email.Id);
            Assert.Equal(RawMessageCodec.ComputeSha256(bytes), entry.Sha256);
            Assert.True((await new AuditChainVerifier(read).VerifyAsync()).Ok);
        }
        finally { await CleanupAsync(s.Ctx, account); }
    }

    // ---- the Graph lean step, through the helper the Graph archiver calls --------------------------------

    // What the Graph archiver builds: fields from the Graph JSON (a body PREVIEW as the text body, its own
    // HTML) and attachment bytes from the attachment API.
    private static ArchivedEmail GraphShaped(int accountId, byte[] pdf) => new()
    {
        MailAccountId = accountId, MessageId = Guid.NewGuid().ToString("N") + "@example.test",
        Subject = "graph subject", From = "graph-from@example.test", To = "graph-to@example.test", Cc = "", Bcc = "",
        Body = "short graph preview", HtmlBody = "<p>graph html</p>", FolderName = "INBOX",
        SentDate = DateTime.UtcNow, ReceivedDate = DateTime.UtcNow, HasAttachments = true,
        Attachments = new List<EmailAttachment>
        {
            new() { FileName = "doc.pdf", ContentType = "application/pdf", Content = pdf, Size = pdf.Length }
        }
    };

    [Fact]
    public async Task GraphShaped_Lean_IndexFieldsAreRederivedFromTheMimeOriginal_AndTheEmailIsValid()
    {
        var s = Build(Raw(), Lean(), chain: true);
        var account = await SeedAccountAsync(s.Ctx);
        try
        {
            var (_, bytes, pdf) = Mail();
            var email = GraphShaped(account.Id, pdf);
            var attachments = email.Attachments.ToList();

            var decision = await LeanArchive.DecideAsync(s.Mode, s.Ingest, s.Raw, new RawMessageCapture(bytes, RawMessageSource.Graph),
                attachments, synthesizedContent: false, rederiveInto: email, NullLogger.Instance, email.Subject);
            Assert.Null(decision.RefusalReason);
            Assert.NotNull(decision.Plan);
            LeanArchive.Apply(email, attachments, decision.Plan!);

            // The index fields now agree with the original, not with the Graph JSON.
            Assert.Equal("first@example.test", email.From);
            Assert.Equal("to@example.test", email.To);
            Assert.Contains("import plain words", email.Body);

            // Saved through a context with the chain interceptor, as production does.
            var chainServices = new ServiceCollection();
            chainServices.AddScoped(_ => NewContext());
            var writer = new AuditChain(chainServices.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
                Opt.Create(new IntegrityOptions()), NullLogger<AuditChain>.Instance);
            await using (var save = NewContext(new AuditChainInterceptor(new ServiceCollection().AddSingleton<IAuditChain>(writer).BuildServiceProvider())))
            {
                save.ArchivedEmails.Add(email);
                await save.SaveChangesAsync();
            }

            s.Ctx.ChangeTracker.Clear();
            var stored = await s.Ctx.ArchivedEmails.Include(e => e.Attachments).Include(e => e.RawMessage).SingleAsync(e => e.MailAccountId == account.Id);
            Assert.True(stored.ContentStripped);
            Assert.Equal(RawMessageSource.Graph, stored.RawMessage!.Source);
            Assert.Equal(IntegrityStatus.Valid, (await s.Integrity.VerifyAsync(stored.Id)).Status);

            await s.Hydrator.HydrateAsync(stored);
            Assert.Equal(pdf, Assert.Single(stored.Attachments).Content);

            await using var read = NewContext();
            Assert.Equal(1, await read.AuditChain.CountAsync(e => e.Kind == "original.stored" && e.EmailId == stored.Id));
        }
        finally { await CleanupAsync(s.Ctx, account); }
    }

    [Fact]
    public async Task GraphShaped_SynthesizedBody_IsStoredInFull()
    {
        var s = Build(Raw(), Lean());
        var account = await SeedAccountAsync(s.Ctx);
        try
        {
            var (_, bytes, pdf) = Mail();
            var email = GraphShaped(account.Id, pdf);
            var decision = await LeanArchive.DecideAsync(s.Mode, s.Ingest, s.Raw, new RawMessageCapture(bytes, RawMessageSource.Graph),
                email.Attachments.ToList(), synthesizedContent: true, rederiveInto: email, NullLogger.Instance, email.Subject);
            Assert.Null(decision.Plan);
            Assert.Null(decision.RefusalReason);
            Assert.Equal("graph-from@example.test", email.From); // untouched
        }
        finally { await CleanupAsync(s.Ctx, account); }
    }

    [Fact]
    public async Task GraphShaped_NoOriginal_IsFullStorage_OrRefusedUnderRequired()
    {
        var s = Build(Raw(), Lean());
        var account = await SeedAccountAsync(s.Ctx);
        try
        {
            var (_, _, pdf) = Mail();
            var email = GraphShaped(account.Id, pdf);
            var plain = await LeanArchive.DecideAsync(s.Mode, s.Ingest, Raw(), null, email.Attachments.ToList(), false, email, NullLogger.Instance, email.Subject);
            Assert.Null(plain.Plan);
            Assert.Null(plain.RefusalReason);

            var strict = await LeanArchive.DecideAsync(s.Mode, s.Ingest, Raw(required: true), null, email.Attachments.ToList(), false, email, NullLogger.Instance, email.Subject);
            Assert.NotNull(strict.RefusalReason);
        }
        finally { await CleanupAsync(s.Ctx, account); }
    }

    // ---- mbox ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(true, false, false)]   // lean: refused
    [InlineData(false, true, false)]   // required: refused
    [InlineData(true, false, true)]    // lean + AllowSerializedImports: not refused
    public async Task MboxImportJob_IsRefusedAtStart_UnlessSerializedImportsAreAllowed(bool lean, bool required, bool allow)
    {
        var dir = Path.Combine(Path.GetTempPath(), "mbox-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "x.mbox");
        // A syntactically empty mbox: nothing to import, so an allowed run just completes without messages.
        await File.WriteAllTextAsync(file, "");
        try
        {
            var env = new Mock<IWebHostEnvironment>();
            env.SetupGet(e => e.ContentRootPath).Returns(dir);
            var services = new ServiceCollection();
            services.AddSingleton(Opt.Create(new StorageModeOptions { Mode = lean ? "Lean" : "Standard", AllowSerializedImports = allow }));
            services.AddSingleton(Opt.Create(new RawMessageOptions { Enabled = true, Required = required }));
            var svc = new MBoxImportService(services.BuildServiceProvider(), NullLogger<MBoxImportService>.Instance, env.Object,
                Opt.Create(new BatchOperationOptions()));

            var job = await svc.ProcessFileAsync(file, "x.mbox", targetAccountId: 1, targetFolder: "INBOX", userId: "test");

            if (allow)
            {
                // Not refused: it moves on (and may fail later for other reasons, such as the missing account).
                Assert.DoesNotContain("refused because", job.ErrorMessage ?? string.Empty);
            }
            else
            {
                Assert.Equal(MBoxImportJobStatus.Failed, job.Status);
                Assert.Contains("mbox import is refused because", job.ErrorMessage);
            }
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }
}
