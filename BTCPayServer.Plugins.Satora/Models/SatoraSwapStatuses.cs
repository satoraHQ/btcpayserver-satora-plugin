namespace BTCPayServer.Plugins.Satora.Models
{
    public static class SatoraSwapStatuses
    {
        public static readonly string[] Terminal =
        {
            "ClientRedeemed", "ServerRedeemed", "ClientRefunded",
            "ClientFundedServerRefunded", "ClientRefundedServerFunded",
            "ClientRefundedServerRefunded", "Expired", "ClientInvalidFunded",
            "ClientFundedTooLate", "ServerWontFund", "ClientRedeemedAndClientRefunded"
        };

        public static bool IsTerminal(string? status)
        {
            return status is not null && Terminal.Contains(status);
        }
    }
}
