using MailArchiver.Models;
using MailArchiver.Services.Shared;
using Xunit;

namespace MailArchiver.Tests.Services;

/// <summary>Audit finding 4: Gmail accounts get the existing FullSyncIntervalHours setting with an effective default of 24 hours.</summary>
public class GmailPeriodicFullSyncTests
{
    private static MailAccount A(string server, int? own = null) => new() { ImapServer = server, FullSyncIntervalHours = own };

    [Fact] public void GmailAccount_WithNoValueAnywhere_DefaultsToDaily() =>
        Assert.Equal(24, GmailAccount.EffectiveFullSyncIntervalHours(A("imap.gmail.com"), null));

    [Fact] public void GmailAccount_OwnValueWins_ThenTheInstallationValue()
    {
        Assert.Equal(6, GmailAccount.EffectiveFullSyncIntervalHours(A("imap.gmail.com", 6), 48));
        Assert.Equal(48, GmailAccount.EffectiveFullSyncIntervalHours(A("imap.gmail.com"), 48));
    }

    [Fact] public void GmailAccount_ZeroSwitchesItOff()
    {
        Assert.Null(GmailAccount.EffectiveFullSyncIntervalHours(A("imap.gmail.com", 0), null));
        Assert.Null(GmailAccount.EffectiveFullSyncIntervalHours(A("imap.googlemail.com", 0), 24));
    }

    [Fact] public void NonGmailAccount_IsUnchanged_NoneUnlessSet()
    {
        Assert.Null(GmailAccount.EffectiveFullSyncIntervalHours(A("mail.example.com"), null));
        Assert.Equal(12, GmailAccount.EffectiveFullSyncIntervalHours(A("mail.example.com", 12), null));
        Assert.Equal(48, GmailAccount.EffectiveFullSyncIntervalHours(A("mail.example.com"), 48));
        Assert.Null(GmailAccount.EffectiveFullSyncIntervalHours(new MailAccount { ImapServer = null }, null));
    }

    [Fact]
    public void TheScheduler_UsesTheEffectiveValue_AndTheAutomaticFullSyncGoesThroughTheCheckpointClearing()
    {
        // The scheduling loop is private to the background service; this pins that it uses the shared rule and the same
        // request path as the manual resync (FullSyncRequest, tested in FullResyncCheckpointTests against the real loop).
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var text = File.ReadAllText(Path.Combine(root, "Services", "MailSyncBackgroundService.cs"));
        Assert.Contains("GmailAccount.EffectiveFullSyncIntervalHours(account, defaultFullSyncIntervalHours)", text);
        Assert.Contains("FullSyncRequest.RequestAsync(", text);
        Assert.Contains("FullSyncRequest.ShouldClearForAutomatic(", text);
    }
}
