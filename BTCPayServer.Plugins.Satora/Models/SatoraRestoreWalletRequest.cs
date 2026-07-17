using System.ComponentModel.DataAnnotations;

namespace BTCPayServer.Plugins.Satora.Models;

public class SatoraRestoreWalletRequest
{
    public string StoreId { get; set; } = null!;

    [Required]
    public string Seed { get; set; } = null!;

    [Required]
    public string SeedConfirmation { get; set; } = null!;
}
