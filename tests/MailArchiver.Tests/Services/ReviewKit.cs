using MailArchiver.Data;
using MailArchiver.Models;
using MailArchiver.Services;
using MailArchiver.Services.Core;
using MailArchiver.Services.Shared;
using MailArchiver.Tests.Infrastructure;
using MailArchiver.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;
using Opt = Microsoft.Extensions.Options.Options;

namespace MailArchiver.Tests.Services;

/// <summary>Shared test harness for the review-fix tests: a real Postgres context, a filesystem store and a wired EmailCoreService.</summary>
internal sealed class ReviewKit : IDisposable
{
    public readonly string Root = Path.Combine(Path.GetTempPath(), "rk-" + Guid.NewGuid().ToString("N"));
    private readonly TestDbFixture _fixture;
    public ReviewKit(TestDbFixture fixture) => _fixture = fixture;

    public void Dispose() { try { Directory.Delete(Root, recursive: true); } catch { } }

    public MailArchiverDbContext NewContext()
        => new(new DbContextOptionsBuilder<MailArchiverDbContext>()
            .UseNpgsql(_fixture.ConnectionString, npgsql => npgsql.CommandTimeout(120))
            .AddInterceptors(new AttachmentDeduplicationInterceptor(NullLogger<AttachmentDeduplicationInterceptor>.Instance))
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning))
            .Options);

    public sealed record Wired(EmailCoreService Svc, IntegrityService Integrity, IRawMessageStore Store, StrippedContentHydrator Hydrator, RawMessageOptions Raw);

    public Wired Make(MailArchiverDbContext ctx, bool lean, bool required = false, bool hashNamed = true)
    {
        var raw = new RawMessageOptions
        {
            Enabled = true, Required = required, Storage = RawMessageStorage.FileSystem, MaxMessageSizeMb = 0,
            FileSystem = new RawMessageFileSystemOptions { BasePath = Root, HashNamed = hashNamed }
        };
        var opts = Opt.Create(raw);
        var fs = new FileSystemRawMessageStore(ctx, opts, new RawMessagePathResolver(raw.FileSystem, Root), NullLogger<FileSystemRawMessageStore>.Instance);
        var router = new RawMessageStoreRouter(ctx, opts, new PostgresRawMessageStore(ctx, opts), fs);
        var hydrator = new StrippedContentHydrator(ctx, new OriginalMessageReader(router));
        var svc = new EmailCoreService(ctx, NullLogger<EmailCoreService>.Instance,
            new DateTimeHelper(Opt.Create(new TimeZoneOptions { DisplayTimeZoneId = "Europe/Berlin" })),
            Opt.Create(new BatchOperationOptions()), Opt.Create(new DashboardOptions { CacheSeconds = 0 }),
            memoryCache: null, rawMessageOptions: opts, rawMessageStore: router, contentHydrator: hydrator,
            storageMode: Opt.Create(new StorageModeOptions { Mode = lean ? "Lean" : "Standard" }),
            leanIngest: new LeanIngest(fs, opts, NullLogger<LeanIngest>.Instance));
        return new Wired(svc, new IntegrityService(ctx, router), router, hydrator, raw);
    }

    public static async Task<MailAccount> SeedAccountAsync(MailArchiverDbContext ctx)
    {
        var a = new MailAccount
        {
            Name = $"rk-{Guid.NewGuid():N}".Substring(0, 18), EmailAddress = $"{Guid.NewGuid():N}@test.local",
            Provider = ProviderType.IMAP, IsEnabled = true, LastSync = DateTime.UtcNow
        };
        ctx.MailAccounts.Add(a);
        await ctx.SaveChangesAsync();
        return a;
    }

    public static async Task CleanupAsync(MailArchiverDbContext ctx, MailAccount account)
    {
        ctx.ChangeTracker.Clear();
        await ctx.Database.ExecuteSqlInterpolatedAsync($"UPDATE mail_archiver.\"ArchivedEmails\" SET \"IsLocked\" = false WHERE \"MailAccountId\" = {account.Id}");
        await ctx.ArchivedEmails.Where(e => e.MailAccountId == account.Id).ExecuteDeleteAsync();
        await ctx.MailAccounts.Where(a => a.Id == account.Id).ExecuteDeleteAsync();
        await ctx.DisposeAsync();
    }

    /// <summary>A synthetic mail (text + html, optional pdf attachment, optional floating text/calendar part), parsed back from its own bytes.</summary>
    public static (MimeMessage Message, byte[] Bytes, byte[]? Pdf) BuildMail(bool withPdf = false, bool withCalendar = false, string? text = null)
    {
        var alt = new MultipartAlternative { new TextPart("plain") { Text = text ?? ("review plain " + Guid.NewGuid().ToString("N")) }, new TextPart("html") { Text = "<p>review html</p>" } };
        if (withCalendar)
            alt.Add(new TextPart("calendar") { Text = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nBEGIN:VEVENT\r\nSUMMARY:Team sync\r\nDTSTART:20261201T090000Z\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n" });
        MimeEntity body = alt;
        byte[]? pdf = null;
        if (withPdf)
        {
            pdf = Guid.NewGuid().ToByteArray().Concat(Enumerable.Range(0, 3000).Select(i => (byte)(i * 11 + 3))).ToArray();
            var mixed = new Multipart("mixed") { alt, new MimePart("application", "pdf")
            {
                Content = new MimeContent(new MemoryStream(pdf)),
                ContentDisposition = new ContentDisposition(ContentDisposition.Attachment) { FileName = "r.pdf" },
                ContentTransferEncoding = ContentEncoding.Base64
            } };
            body = mixed;
        }
        var m = new MimeMessage();
        m.From.Add(new MailboxAddress("Sender Name", "from@example.test"));
        m.To.Add(new MailboxAddress("To Name", "to@example.test"));
        m.Subject = "review " + Guid.NewGuid().ToString("N");
        m.MessageId = $"{Guid.NewGuid():N}@example.test";
        m.Date = DateTimeOffset.UtcNow.AddHours(-3);
        m.Body = body;
        using var ms = new MemoryStream();
        m.WriteTo(ms);
        var bytes = ms.ToArray();
        using var read = new MemoryStream(bytes);
        return (MimeMessage.Load(read), bytes, pdf);
    }
}
