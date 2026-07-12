using System.ComponentModel.DataAnnotations;

namespace BTCPayServer.Plugins.Satora.Models;

public class SatoraConfirmSeedRequest
{
    public string StoreId { get; set; } = null!;

    [Required]
    public string SeedConfirmation { get; set; } = null!;
}
