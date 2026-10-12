using BTCPayServer.Plugins.Satora.Models;
using BTCPayServer.Plugins.Satora.Services;
using Xunit;
using SwapStatus = uniffi.satora_sdk_ffi.SwapStatus;
using static BTCPayServer.Plugins.Satora.Models.SatoraWalletState;
using static BTCPayServer.Plugins.Satora.Services.SeedWriteKind;

namespace BTCPayServer.Plugins.Satora.Tests;

public class SeedWriteGuardTests
{
    [Theory]
    // First wallet of the store.
    [InlineData(NotSetUp, Generate, false, 0, 0ul, null, SeedWriteRefusal.None)]
    [InlineData(NotSetUp, Restore, false, 0, 0ul, null, SeedWriteRefusal.None)]
    // A locked seed is only replaced by its own phrase, which also works with swaps open.
    [InlineData(Locked, Generate, false, 0, 0ul, null, SeedWriteRefusal.Locked)]
    [InlineData(Locked, Restore, false, 0, 0ul, SeedWriteGuard.ConfirmationWord, SeedWriteRefusal.Locked)]
    [InlineData(Locked, Restore, true, 2, null, null, SeedWriteRefusal.None)]
    // A legacy seed is restored, never generated over.
    [InlineData(RestoreRequired, Generate, false, 0, 0ul, null, SeedWriteRefusal.RestoreOnly)]
    [InlineData(RestoreRequired, Restore, false, 2, null, null, SeedWriteRefusal.None)]
    // Open swaps refuse a replace outright, even when confirmed.
    [InlineData(Ready, Generate, false, 1, 0ul, SeedWriteGuard.ConfirmationWord, SeedWriteRefusal.OpenOperations)]
    [InlineData(BackupRequired, Restore, false, 1, 0ul, null, SeedWriteRefusal.OpenOperations)]
    // An unread balance is not a zero balance.
    [InlineData(Ready, Generate, false, 0, null, SeedWriteGuard.ConfirmationWord, SeedWriteRefusal.BalanceUnknown)]
    // Funds need the typed confirmation; an empty wallet does not.
    [InlineData(Ready, Generate, false, 0, 1000ul, null, SeedWriteRefusal.ConfirmationRequired)]
    [InlineData(Ready, Restore, false, 0, 1ul, "replace", SeedWriteRefusal.ConfirmationRequired)]
    [InlineData(Ready, Generate, false, 0, 1000ul, " REPLACE ", SeedWriteRefusal.None)]
    [InlineData(Ready, Generate, false, 0, 0ul, null, SeedWriteRefusal.None)]
    [InlineData(BackupRequired, Generate, false, 0, 0ul, null, SeedWriteRefusal.None)]
    // Restoring the wallet the store already has replaces nothing.
    [InlineData(Ready, Restore, true, 3, null, null, SeedWriteRefusal.None)]
    public void Decides(
        SatoraWalletState state,
        SeedWriteKind kind,
        bool sameWallet,
        int openOperations,
        ulong? balanceSats,
        string? confirmation,
        SeedWriteRefusal expected)
    {
        Assert.Equal(expected, SeedWriteGuard.Check(state, kind, sameWallet, openOperations, balanceSats, confirmation));
    }

    // Every SwapStatus case, by the type name SatoraTx.Status stores, and whether the swap may still need the seed.
    private static readonly Dictionary<string, bool> SeedNeeded = new()
    {
        ["Pending"] = true,
        ["ClientFundingSeen"] = true,
        ["ClientFunded"] = true,
        ["ServerFunded"] = true,
        ["ClientRedeeming"] = true,
        // The client's funds wait for a refund that only this seed can sign.
        ["ClientFundedServerRefunded"] = true,
        ["ClientInvalidFunded"] = true,
        ["ClientFundedTooLate"] = true,
        ["ServerWontFund"] = true,
        // A status the SDK did not recognise.
        ["Other"] = true,
        ["ClientRedeemed"] = false,
        ["ServerRedeemed"] = false,
        ["ClientRedeemedAndClientRefunded"] = false,
        ["ClientRefunded"] = false,
        ["ClientRefundedServerFunded"] = false,
        ["ClientRefundedServerRefunded"] = false,
        ["Expired"] = false
    };

    public static IEnumerable<object[]> Statuses => SeedNeeded.Select(s => new object[] { s.Key, s.Value });

    [Theory]
    [MemberData(nameof(Statuses))]
    public void SwapIsOpenUntilTheSeedHasNothingLeftToClaimOrRefund(string status, bool seedNeeded)
    {
        Assert.Equal(seedNeeded, !SeedWriteGuard.SeedNotNeededStatuses.Contains(status));
    }

    [Fact]
    public void EveryStatusTheSdkReportsIsDecided()
    {
        var cases = typeof(SwapStatus).GetNestedTypes().Where(t => t.IsSubclassOf(typeof(SwapStatus))).Select(t => t.Name);

        Assert.Equal(cases.Order(), SeedNeeded.Keys.Order());
    }

    // The service only reads swaps and the balance when the write is a replace.
    [Theory]
    [InlineData(NotSetUp, Generate, false, false)]
    [InlineData(Locked, Restore, true, false)]
    [InlineData(RestoreRequired, Restore, false, false)]
    [InlineData(Ready, Restore, true, false)]
    [InlineData(Ready, Restore, false, true)]
    [InlineData(Ready, Generate, false, true)]
    [InlineData(BackupRequired, Generate, false, true)]
    public void KnowsWhatIsAReplace(SatoraWalletState state, SeedWriteKind kind, bool sameWallet, bool expected)
    {
        Assert.Equal(expected, SeedWriteGuard.IsReplace(state, kind, sameWallet));
    }
}
