using BTCPayServer.Plugins.Satora.Models;

namespace BTCPayServer.Plugins.Satora.Services;

public enum SeedWriteKind
{
    Generate,
    Restore
}

public enum SeedWriteRefusal
{
    None,
    NetworkUnsupported,
    // A locked seed may hold funds and may become readable again; only its own phrase replaces it.
    Locked,
    RestoreOnly,
    OpenOperations,
    BalanceUnknown,
    ConfirmationRequired
}

public static class SeedWriteGuard
{
    public const string ConfirmationWord = "REPLACE";

    // Swap statuses after which a swap needs nothing more from the seed; any other status, none or
    // one this list does not know, counts as open. A client-funded swap the server refunded, did
    // not fund, or found invalid or late is not here: this seed still has to refund it.
    public static readonly string[] SeedNotNeededStatuses =
    [
        "ClientRedeemed", "ServerRedeemed", "ClientRedeemedAndClientRefunded",
        "ClientRefunded", "ClientRefundedServerFunded", "ClientRefundedServerRefunded",
        "Expired"
    ];

    // sameWallet: a restore of the wallet the store already has (its phrase, or for a locked seed its
    // fingerprint), which brings that wallet back instead of replacing it.
    public static bool IsReplace(SatoraWalletState state, SeedWriteKind kind, bool sameWallet) =>
        state is SatoraWalletState.BackupRequired or SatoraWalletState.Ready &&
        !(kind == SeedWriteKind.Restore && sameWallet);

    public static SeedWriteRefusal Check(
        SatoraWalletState state,
        SeedWriteKind kind,
        bool sameWallet,
        int openOperations,
        ulong? balanceSats,
        string? confirmation)
    {
        switch (state)
        {
            case SatoraWalletState.NotSetUp:
                return SeedWriteRefusal.None;
            case SatoraWalletState.Locked:
                return kind == SeedWriteKind.Restore && sameWallet ? SeedWriteRefusal.None : SeedWriteRefusal.Locked;
            // An earlier plugin version's seed, AES keyed on the store id, is no longer read, so the
            // store restores its wallet from the recovery phrase before anything else, also with swap
            // rows open: refusing would leave it no wallet at all. Swaps that version funded need
            // that seed, which restoring its phrase brings back. Should another phrase be restored,
            // an operator can still decrypt the legacy ciphertext the restore keeps in
            // SatoraRetiredSeeds, as its key derives from the store id.
            case SatoraWalletState.RestoreRequired:
                return kind == SeedWriteKind.Restore ? SeedWriteRefusal.None : SeedWriteRefusal.RestoreOnly;
        }
        if (!IsReplace(state, kind, sameWallet))
            return SeedWriteRefusal.None;
        if (openOperations > 0)
            return SeedWriteRefusal.OpenOperations;
        if (balanceSats is null)
            return SeedWriteRefusal.BalanceUnknown;
        if (balanceSats > 0 && !string.Equals(confirmation?.Trim(), ConfirmationWord, StringComparison.Ordinal))
            return SeedWriteRefusal.ConfirmationRequired;
        return SeedWriteRefusal.None;
    }
}
