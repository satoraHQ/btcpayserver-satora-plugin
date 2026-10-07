using Satora.Sdk;

namespace BTCPayServer.Plugins.Satora.Models;

// Shaped after pseudozach's PR #2. No StoreId: the store always comes from the route.
public class SatoraWithdrawRequest
{
    public string? Destination { get; set; }

    // Text, parsed by SatoraWithdrawDestinations.Amount: MVC binds a decimal with the request
    // culture's group separator.
    public string? AmountBtc { get; set; }

    public bool SendAll { get; set; }

    // Execute only: the idempotency key the review handed out.
    public string? Key { get; set; }

    // Execute only: the total the review showed. A Lightning swap or an exit taking more is refused.
    public long? MaxTotalSats { get; set; }

    // Execute only: the fee the review showed. Sending everything on chain is refused at a higher fee.
    public long? MaxFeeSats { get; set; }
}

public class SatoraWithdrawReview
{
    public SatoraWithdrawRequest Request { get; set; } = new();

    public SatoraWithdrawalType Type { get; set; }

    public string Destination { get; set; } = "";

    // What the destination receives; null while unknown.
    public long? AmountSats { get; set; }

    public long FeeSats { get; set; }

    // What leaves the wallet.
    public long TotalSats { get; set; }

    // Confirmed and pre-confirmed sats, which withdrawals spend.
    public long AvailableSats { get; set; }

    // Spent by no withdrawal; they stay in the wallet until a settle, the owner's or the plugin's own.
    public long RecoverableSats { get; set; }

    public string? Key { get; set; }

    public string? Error { get; set; }
}

public class SatoraWithdrawPageModel
{
    public string StoreId { get; set; } = null!;

    // Why the store cannot withdraw at all (unsupported network, no wallet).
    public string? Unavailable { get; set; }

    public SatoraWithdrawReview? Review { get; set; }

    public List<SatoraWithdrawal> Withdrawals { get; set; } = [];

    public List<SatoraWithdrawal> Unresolved { get; set; } = [];

    // Ids of the withdrawals whose request is running.
    public HashSet<string> Running { get; set; } = [];

    // Lightning payments the worker cannot finish, which the store may close.
    public List<SatoraLightningSwap> Unfinished { get; set; } = [];

    // What each unfinished payment's VHTLC holds, by swap; null where it could not be read.
    public Dictionary<string, VhtlcAmounts?> UnfinishedHeld { get; set; } = [];

    public bool AutomatedLightningPayouts { get; set; }

    public SatoraAutoWithdrawRule? AutoWithdraw { get; set; }

    // What the owner submitted for the rule when it was refused, to show again.
    public SatoraAutoWithdrawRequest? AutoWithdrawForm { get; set; }

    public DateTimeOffset Now { get; set; }
}

// Amounts as text, parsed as SatoraWithdrawRequest.AmountBtc is.
public class SatoraAutoWithdrawRequest
{
    public string? Destination { get; set; }

    public string? ThresholdBtc { get; set; }

    public string? ReserveBtc { get; set; }

    public string? MaxFeePercent { get; set; }

    // The version of the rule the form showed; null when there was none.
    public int? Version { get; set; }
}

public class SatoraResolveWithdrawalViewModel
{
    public bool Sent { get; set; }
}

public class SatoraCloseSwapViewModel
{
    public string? Confirmation { get; set; }

    // The store's answer: the invoice was paid, or the funds are back in the wallet. Null when it gave none.
    public bool? Paid { get; set; }
}
