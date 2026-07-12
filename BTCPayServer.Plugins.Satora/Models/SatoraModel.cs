
using uniffi.satora_sdk_ffi;

namespace BTCPayServer.Plugins.Satora.Models
{
    public class SatoraModel
    {
        public SatoraSettings Settings { get; set; }

        public List<SatoraTx> Transactions { get; set; }

        public ArkadeBalance? WalletBalance { get; set; }

        public bool CanManageSeed { get; set; }

        public string? OneTimeSeed { get; set; }
    }
}
