using System.Text.Json;
using MailArchiver.Data;
using Microsoft.Extensions.DependencyInjection;
using MailArchiver.Models;
using MailArchiver.Services.Shared;
using MailArchiver.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MailArchiver.Tests.Services;

/// <summary>Delete memory, step 4: "Allow again" records marker.released in the chain first, then removes the marker.</summary>
[Collection(TestDbFixture.CollectionName)]
public class DeletionMarkerReleaseTests : IAsyncLifetime, IDisposable
{
    private readonly ReviewKit _kit;
    public DeletionMarkerReleaseTests(TestDbFixture fixture) => _kit = new ReviewKit(fixture);
    public void Dispose() => _kit.Dispose();
    public Task InitializeAsync() => _kit.ResetChainAsync();
    public Task DisposeAsync() => _kit.ResetChainAsync();

    private async Task<(MailArchiverDbContext Ctx, MailAccount Account, List<int> EmailIds)> SeedDeletedAsync(int count)
    {
        var ctx = _kit.NewContext();
        var account = await ReviewKit.SeedAccountAsync(ctx);
        var emails = Enumerable.Range(0, count).Select(i => DeleteReasonTests.NewEmail(account.Id, gmailId: (900 + i).ToString())).ToList();
        ctx.ArchivedEmails.AddRange(emails);
        await ctx.SaveChangesAsync();
        var ids = emails.Select(e => e.Id).ToList();
        ctx.ChangeTracker.Clear();
        await DeleteReasonScope.RunAsync(ctx, DeleteReasons.User, async () =>
        {
            ctx.ArchivedEmails.RemoveRange(await ctx.ArchivedEmails.Where(e => ids.Contains(e.Id)).ToListAsync());
            await ctx.SaveChangesAsync();
        });
        return (ctx, account, ids);
    }

    [Fact]
    public async Task ReleasingOne_WritesTheChainEntryAndRemovesOnlyThatMarker_AndTheChainStillVerifies()
    {
        var (ctx, account, _) = await SeedDeletedAsync(2);
        try
        {
            var svc = new DeletionMarkerService(_kit.NewContext(), _kit.Chain);
            var list = await svc.ListAsync(account.Id, 10);
            Assert.Equal(2, list.Count);

            Assert.Equal(1, await svc.ReleaseAsync(account.Id, list[0].Id, "tester"));
            Assert.Equal(1, await svc.CountAsync(account.Id));

            await using var read = _kit.NewContext();
            var entry = await read.AuditChain.AsNoTracking().SingleAsync(c => c.Kind == "marker.released");
            var p = JsonDocument.Parse(entry.Payload).RootElement;
            Assert.Equal(list[0].Id, p.GetProperty("markerId").GetInt32());
            Assert.Equal("tester", p.GetProperty("by").GetString());
            Assert.Equal(list[0].ChainSeq, p.GetProperty("deleteChainSeq").GetInt64());
            Assert.True((await new AuditChainVerifier(read).VerifyAsync()).Ok);
        }
        finally { await ReviewKit.CleanupAsync(ctx, account); }
    }

    [Fact]
    public async Task ReleasingAll_ReleasesEveryMarkerOfThatAccountOnly()
    {
        var (ctx, a, _) = await SeedDeletedAsync(3);
        var (ctx2, b, _) = await SeedDeletedAsync(1);
        try
        {
            var svc = new DeletionMarkerService(_kit.NewContext(), _kit.Chain);
            Assert.Equal(3, await svc.ReleaseAsync(a.Id, null, "tester"));
            Assert.Equal(0, await svc.CountAsync(a.Id));
            Assert.Equal(1, await svc.CountAsync(b.Id));
            await using var read = _kit.NewContext();
            Assert.Equal(3, await read.AuditChain.CountAsync(c => c.Kind == "marker.released"));
        }
        finally { await ReviewKit.CleanupAsync(ctx, a); await ReviewKit.CleanupAsync(ctx2, b); }
    }

    [Fact]
    public async Task AMarkerOfAnotherAccount_CannotBeReleasedThroughThisAccount()
    {
        var (ctx, a, _) = await SeedDeletedAsync(1);
        var (ctx2, b, _) = await SeedDeletedAsync(1);
        try
        {
            var svc = new DeletionMarkerService(_kit.NewContext(), _kit.Chain);
            var bMarker = (await svc.ListAsync(b.Id, 5)).Single();
            Assert.Equal(0, await svc.ReleaseAsync(a.Id, bMarker.Id, "tester"));
            Assert.Equal(1, await svc.CountAsync(b.Id));
        }
        finally { await ReviewKit.CleanupAsync(ctx, a); await ReviewKit.CleanupAsync(ctx2, b); }
    }

    [Fact]
    public async Task WhenTheChainEntryCannotBeWritten_TheMarkerStays()
    {
        var (ctx, a, _) = await SeedDeletedAsync(1);
        try
        {
            var disabled = new AuditChain(new Microsoft.Extensions.DependencyInjection.ServiceCollection().BuildServiceProvider()
                .GetRequiredService<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(),
                Microsoft.Extensions.Options.Options.Create(new IntegrityOptions { AuditChainEnabled = false }),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<AuditChain>.Instance);
            var svc = new DeletionMarkerService(_kit.NewContext(), disabled);
            Assert.Equal(0, await svc.ReleaseAsync(a.Id, null, "tester"));
            Assert.Equal(1, await svc.CountAsync(a.Id));
        }
        finally { await ReviewKit.CleanupAsync(ctx, a); }
    }

    [Fact]
    public async Task AReleasedMessage_IsNoLongerMatchedByTheGate_SoTheNextFullResyncArchivesIt()
    {
        var (ctx, a, _) = await SeedDeletedAsync(1);
        try
        {
            var svc = new DeletionMarkerService(_kit.NewContext(), _kit.Chain);
            Task<IReadOnlyList<MarkerProbe>> Probe() => Task.FromResult<IReadOnlyList<MarkerProbe>>(new[] { new MarkerProbe(1, "900", "<x@x>", 1, DateTime.UtcNow) });
            Assert.True((await MarkerBatchCheck.RunAsync(svc, a.Id, true, "x", 1, Probe)).Hits.ContainsKey(1));
            await svc.ReleaseAsync(a.Id, null, "tester");
            Assert.False((await MarkerBatchCheck.RunAsync(svc, a.Id, true, "x", 1, Probe)).Hits.ContainsKey(1));
        }
        finally { await ReviewKit.CleanupAsync(ctx, a); }
    }
}
