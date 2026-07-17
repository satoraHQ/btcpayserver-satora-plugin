namespace BTCPayServer.Plugins.Satora.Models
{
    public class SatoraSwapStatusView
    {
        public string RawStatus { get; set; } = "";

        public string DisplayStatus { get; set; } = "";

        public string Severity { get; set; } = "secondary";

        public string ActionHint { get; set; } = "";

        public bool IsTerminal { get; set; }

        public bool NeedsManualAction { get; set; }

        public bool IsStale { get; set; }

        public bool CanRetry { get; set; }

        public string? RetryUnavailableReason { get; set; }
    }
}
