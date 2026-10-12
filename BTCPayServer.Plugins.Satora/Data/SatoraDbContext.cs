using BTCPayServer.Plugins.Satora.Models;
using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.Plugins.Satora;

public class SatoraPluginDbContext : DbContext
{
    private readonly bool _designTime;

    public SatoraPluginDbContext(DbContextOptions<SatoraPluginDbContext> options, bool designTime = false)
        : base(options)
    {
        _designTime = designTime;
    }

    public DbSet<SatoraSettings> SatoraSettings { get; set; }
    public DbSet<SatoraTx> SatoraTransactions { get; set; }
    public DbSet<SatoraRetiredSeed> SatoraRetiredSeeds { get; set; }
    public DbSet<SatoraLightningSwap> SatoraLightningSwaps { get; set; }
    public DbSet<SatoraWithdrawal> SatoraWithdrawals { get; set; }
    public DbSet<SatoraSentAlert> SatoraSentAlerts { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.HasDefaultSchema("BTCPayServer.Plugins.Satora");
        modelBuilder.Entity<SatoraLightningSwap>(swap =>
        {
            swap.Property(s => s.Direction).HasConversion<string>();
            swap.Property(s => s.Status).HasConversion<string>();
        });
        modelBuilder.Entity<SatoraWithdrawal>(withdrawal =>
        {
            withdrawal.Property(w => w.Type).HasConversion<string>();
            withdrawal.Property(w => w.Status).HasConversion<string>();
        });
        modelBuilder.Entity<SatoraSentAlert>().HasKey(a => new { a.StoreId, a.SwapId, a.Problem });
    }
}
