using BTCPayServer.Data;
using BTCPayServer.Logging;
using BTCPayServer.Plugins.Satora.Lightning;
using BTCPayServer.Plugins.Satora.Services;
using BTCPayServer.Services.Notifications;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace BTCPayServer.Plugins.Satora.Tests;

// Opt-in, against BTCPay's own migrated schema in a real Postgres: set SATORA_TEST_POSTGRES as for
// SatoraWalletIntegrationTests. The only check of the recipients query, as CI runs no Postgres.
public class SatoraNotifierIntegrationTests(SatoraChainPromptIntegrationTests.BtcPayDatabase btcpay)
    : IClassFixture<SatoraChainPromptIntegrationTests.BtcPayDatabase>
{
    private const string NeedsPostgres = "Set SATORA_TEST_POSTGRES to run";

    public static bool HasPostgres => SatoraWalletIntegrationTests.HasPostgres;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // The fixture's store is "store". Owners of another store, its other roles and a user who turned
    // Satora's Lightning alerts off get nothing; an admin who also owns the store gets one.
    [Fact(Skip = NeedsPostgres, SkipUnless = nameof(HasPostgres))]
    public async Task SendsEachAlertToTheStoresOwnersAndTheServerAdminsOnceEach()
    {
        await using (var ctx = btcpay.Contexts.CreateContext())
        {
            ctx.Stores.Add(new StoreData { Id = "other", StoreName = "other" });
            ctx.Roles.Add(new IdentityRole { Id = "admin-role", Name = Roles.ServerAdmin, NormalizedName = "SERVERADMIN" });
            foreach (var id in new[] { "admin-owner", "admin", "owner", "employee", "other-owner", "muted-owner" })
                ctx.Users.Add(new ApplicationUser
                {
                    Id = id,
                    UserName = id,
                    Email = $"{id}@example.com",
                    DisabledNotifications = id == "muted-owner" ? "satora-lightning-swap;" : null
                });
            ctx.UserRoles.AddRange(
                new IdentityUserRole<string> { UserId = "admin-owner", RoleId = "admin-role" },
                new IdentityUserRole<string> { UserId = "admin", RoleId = "admin-role" });
            ctx.UserStore.AddRange(
                new UserStore { ApplicationUserId = "admin-owner", StoreDataId = "store", StoreRoleId = "Owner" },
                new UserStore { ApplicationUserId = "owner", StoreDataId = "store", StoreRoleId = "Owner" },
                new UserStore { ApplicationUserId = "employee", StoreDataId = "store", StoreRoleId = "Employee" },
                new UserStore { ApplicationUserId = "other-owner", StoreDataId = "other", StoreRoleId = "Owner" },
                new UserStore { ApplicationUserId = "muted-owner", StoreDataId = "store", StoreRoleId = "Owner" });
            await ctx.SaveChangesAsync(Ct);
        }
        // UserScope never reaches the sender's scope factory, which only AdminScope uses.
        var sender = new NotificationSender(btcpay.Contexts, null!,
            new NotificationManager(btcpay.Contexts, new MemoryCache(new MemoryCacheOptions()), [], new EventAggregator(new Logs())));

        await new SatoraNotifier(btcpay.Contexts, sender).SendAsync("store", new SatoraLightningNotification
        {
            StoreId = "store",
            SwapId = "swap",
            Problem = SatoraLightningNotification.Problems.ClaimFailing
        });

        await using var read = btcpay.Contexts.CreateContext();
        var received = await read.Notifications
            .Where(n => n.NotificationType == "satora-lightning-swap")
            .GroupBy(n => n.ApplicationUserId)
            .Select(g => new { User = g.Key, Count = g.Count() })
            .ToListAsync(Ct);
        Assert.Equal([("admin", 1), ("admin-owner", 1), ("owner", 1)], received.Select(r => (r.User, r.Count)).Order());
    }
}
