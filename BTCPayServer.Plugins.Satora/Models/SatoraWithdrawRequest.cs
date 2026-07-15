using System.ComponentModel.DataAnnotations;

namespace BTCPayServer.Plugins.Satora.Models;

public class SatoraWithdrawRequest
{
    public string StoreId { get; set; } = string.Empty;

    [Required]
    public string Destination { get; set; } = string.Empty;

    public decimal? AmountBtc { get; set; }
}
