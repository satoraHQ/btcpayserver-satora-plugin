using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Data;
using BTCPayServer.Services.Notifications;
using BTCPayServer.Services.Stores;
using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.Plugins.Satora.Services;

// Every Satora alert goes to the store's owners and the server admins, each once. Admins get them
// because acting on one, such as restoring a locked wallet, can need hot wallet rights an owner may
// lack. BTCPay's NotificationSender resolves each scope on its own, so sending the store and admin
// scopes would give an admin who owns the store two of each.
public class SatoraNotifier(ApplicationDbContextFactory contexts, NotificationSender sender)
{
    public async Task SendAsync(string storeId, BaseNotification notification)
    {
        // UserScope still applies each user's disabled notifications.
        foreach (var userId in await RecipientsAsync(storeId))
            await sender.SendNotification(new UserScope(userId), notification);
    }

    // As BTCPay's StoreScope with the owner role and its AdminScope.
    private async Task<string[]> RecipientsAsync(string storeId)
    {
        var owner = StoreRoleId.Owner.Id;
        await using var ctx = contexts.CreateContext();
        var owners = ctx.UserStore.Where(u => u.StoreDataId == storeId && u.StoreRoleId == owner).Select(u => u.ApplicationUserId);
        var admins = from userRole in ctx.UserRoles
                     join role in ctx.Roles on userRole.RoleId equals role.Id
                     where role.Name == Roles.ServerAdmin
                     select userRole.UserId;
        return await owners.Union(admins).ToArrayAsync();
    }
}
