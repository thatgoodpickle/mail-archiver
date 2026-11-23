using MailArchiver.Controllers;
using MailArchiver.Models;
using MailArchiver.Services.Shared;
using MailArchiver.Tests.Infrastructure;
using MailArchiver.Models.ViewModels;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MailArchiver.Tests.Services;

/// <summary>Audit findings 6 and 7: an empty filter field means "no filter, archive everything", distinct from "never set = the default".</summary>
public class GmailFilterFieldTests
{
    private static IModelMetadataProvider Provider() => new ServiceCollection().AddControllersWithViews().Services.BuildServiceProvider().GetRequiredService<IModelMetadataProvider>();

    [Theory]
    [InlineData(typeof(MailAccountViewModel))]
    [InlineData(typeof(CreateMailAccountViewModel))]
    public void TheFilterField_KeepsAnEmptyValueEmpty_InsteadOfBindingItToNull(Type model)
    {
        var p = Provider();
        Assert.False(p.GetMetadataForProperty(model, "GmailSearchFilter").ConvertEmptyStringToNull);
        // Control: an ordinary string property still converts, so the check above is a real one.
        Assert.True(p.GetMetadataForProperty(model, "Name").ConvertEmptyStringToNull);
    }

    [Fact] public void TheNewAccountForm_StartsWithTheDefaultFilter() =>
        Assert.Equal(GmailAccount.DefaultFilter, new CreateMailAccountViewModel().GmailSearchFilter);

    [Fact]
    public void Stored_EmptyIsNoFilter_NullIsTheDefault()
    {
        Assert.Equal("", MailAccountsController.FilterToStore(ProviderType.IMAP, "imap.gmail.com", ""));
        Assert.Equal("", MailAccountsController.FilterToStore(ProviderType.IMAP, "imap.gmail.com", "   "));
        Assert.Null(MailAccountsController.FilterToStore(ProviderType.IMAP, "imap.gmail.com", null));
        Assert.Equal("label:Work", MailAccountsController.FilterToStore(ProviderType.IMAP, "imap.gmail.com", " label:Work "));
        Assert.Null(MailAccountsController.FilterToStore(ProviderType.IMAP, "mail.example.com", ""));

        var empty = new MailAccount { ImapServer = "imap.gmail.com", GmailSearchFilter = "" };
        var never = new MailAccount { ImapServer = "imap.gmail.com", GmailSearchFilter = null };
        Assert.Null(GmailAccount.EffectiveFilter(empty));
        Assert.Equal(GmailAccount.DefaultFilter, GmailAccount.EffectiveFilter(never));
    }

    private static HttpRequest Post(string? filter)
    {
        var ctx = new DefaultHttpContext();
        if (filter == null) return ctx.Request;
        ctx.Request.ContentType = "application/x-www-form-urlencoded";
        ctx.Request.Form = new FormCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues> { ["filter"] = filter });
        return ctx.Request;
    }

    [Fact]
    public void TheTestButton_ReadsTheRawPostedValue_EmptyStaysEmpty_AbsentIsTheDefault()
    {
        Assert.Equal("", MailAccountsController.FilterPosted(Post("")));
        Assert.Equal("label:Work", MailAccountsController.FilterPosted(Post("label:Work")));
        Assert.Equal(GmailAccount.DefaultFilter, MailAccountsController.FilterPosted(Post(null)));
    }
}

[Collection(TestDbFixture.CollectionName)]
public class GmailEmptyFilterSyncTests
{
    private readonly TestDbFixture _fixture;
    public GmailEmptyFilterSyncTests(TestDbFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task AnAccountStoredWithAnEmptyFilter_ArchivesEverything_IncludingMarketing()
    {
        using var rig = new SyncRig(_fixture); await rig.InitAsync(filter: "", setFilter: true);
        rig.AddMail(null, "Inbox"); rig.AddMail(null, "Marketing");

        await rig.RunAsync();

        Assert.Equal(2, (await rig.ArchivedMessageIdsAsync()).Count);
    }
}
