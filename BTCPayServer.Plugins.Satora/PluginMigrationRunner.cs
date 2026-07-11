using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Plugins.Satora.Data;
using BTCPayServer.Plugins.Satora.Services;
using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.Plugins.Satora;

public class PluginMigrationRunner(
        ISettingsRepository settingsRepository,
        SatoraPluginDbContextFactory pluginDbContextFactory,
        SatoraPluginService pluginService) : IHostedService
{

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var settings = await settingsRepository.GetSettingAsync<PluginDataMigrationHistory>() ??
                       new PluginDataMigrationHistory();
        await using var ctx = pluginDbContextFactory.CreateContext();
        await ctx.Database.MigrateAsync(cancellationToken);
        await pluginService.SyncStorePaymentMethodConfigs(cancellationToken);

        // settings migrations
        if (!settings.UpdatedSomething)
        {
            settings.UpdatedSomething = true;
            await settingsRepository.UpdateSetting(settings);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    private class PluginDataMigrationHistory
    {
        public bool UpdatedSomething { get; set; }
    }
}
