using MailArchiver.Models;
using MailArchiver.Services.Shared;
using MailArchiver.Utilities;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using Microsoft.Extensions.Options;

namespace MailArchiver.Services.Providers.Imap
{
    /// <summary>
    /// The coverage check's IMAP adapter. READ-ONLY towards the mailbox: it only lists (SEARCH) and fetches keys (UID, Message-ID, size,
    /// and for Gmail the X-GM-MSGID), never a body, and opens every folder with <see cref="FolderAccess.ReadOnly"/>. It never sets a flag,
    /// copies, moves, deletes, expunges, appends or creates anything (a test scans this file for such calls). It reads the same folders
    /// the sync reads: for Gmail the folder with all mail (unless the account syncs every folder), otherwise every folder that is
    /// selectable and not excluded.
    /// </summary>
    public sealed class ImapServerKeySource : IServerKeySource
    {
        private const int Batch = 500;
        private readonly ImapConnectionFactory _connectionFactory;
        private readonly IImapFolderService _folderService;
        private readonly MailSyncOptions _syncOptions;

        public ImapServerKeySource(ImapConnectionFactory connectionFactory, IImapFolderService folderService, IOptions<MailSyncOptions> syncOptions)
        {
            _connectionFactory = connectionFactory; _folderService = folderService; _syncOptions = syncOptions.Value;
        }

        public async Task<ServerListing> ListAsync(MailAccount account, CancellationToken ct = default)
        {
            var gmail = GmailAccount.Is(account);
            using var client = _connectionFactory.CreateImapClient(account.Name);
            client.Timeout = 300000;
            client.ServerCertificateValidationCallback = _connectionFactory.ServerCertificateValidationCallback;
            await _connectionFactory.ConnectWithFallbackAsync(client, account.ImapServer, account.ImapPort ?? 993, account.UseSSL, account.Name);
            await _connectionFactory.AuthenticateClientAsync(client, account);

            var gmailExt = gmail && client.Capabilities.HasFlag(ImapCapabilities.GMailExt1);
            var filter = gmailExt ? GmailAccount.EffectiveFilter(account) : null;
            var keys = new List<ServerMessageKey>();
            var keep = filter != null ? new HashSet<string>() : null;
            var read = new List<string>();

            foreach (var folder in await _folderService.GetAllFoldersAsync(client, account.Name))
            {
                ct.ThrowIfCancellationRequested();
                if (folder == null || string.IsNullOrEmpty(folder.FullName) || folder.Attributes.HasFlag(FolderAttributes.NonExistent) || folder.Attributes.HasFlag(FolderAttributes.NoSelect)) continue;
                if (GmailAccount.AllMailOnly(account) && !folder.Attributes.HasFlag(FolderAttributes.All)) continue;
                if (FolderExclusionMatcher.IsExcluded(folder.FullName, folder.Name, account.ExcludedFoldersList, _syncOptions.GlobalExcludedFolders, _syncOptions.ExcludeSubfolders)) continue;

                await folder.OpenAsync(FolderAccess.ReadOnly, ct);
                read.Add(folder.FullName);
                var folderName = MailContentHelper.CleanText(folder.FullName);
                var uids = await folder.SearchAsync(SearchQuery.All, ct);

                var gidByUid = new Dictionary<uint, string>();
                var items = MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.Size;
                if (gmailExt) items |= MessageSummaryItems.GMailMessageId;
                for (var i = 0; i < uids.Count; i += Batch)
                {
                    ct.ThrowIfCancellationRequested();
                    var summaries = await folder.FetchAsync(uids.Skip(i).Take(Batch).ToList(), items, ct);
                    foreach (var s in summaries)
                    {
                        var gid = s.GMailMessageId?.ToString();
                        if (gid != null) gidByUid[s.UniqueId.Id] = gid;
                        keys.Add(new ServerMessageKey(gid, s.Envelope?.MessageId, s.Size, folderName, folder.UidValidity, s.UniqueId.Id));
                    }
                }

                // The filter's result as a SET of Gmail ids (the keep set): what the sync would archive from this folder.
                if (filter != null && keep != null)
                {
                    var kept = await folder.SearchAsync(SearchQuery.GMailRawSearch(filter), ct);
                    foreach (var u in kept)
                        if (gidByUid.TryGetValue(u.Id, out var g)) keep.Add(g);
                }
                await folder.CloseAsync(false, ct);   // expunge = false: nothing is changed
            }

            await client.DisconnectAsync(true, ct);
            return new ServerListing(keys, keep, read);
        }
    }
}
