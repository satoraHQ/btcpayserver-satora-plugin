using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Plugins.Satora.Lightning;
using NBitcoin;
using NBXplorer;
using Xunit;

namespace BTCPayServer.Plugins.Satora.Tests;

public class SatoraStoreLightningConfigTests
{
    private const string Lnd = "type=lnd-rest;server=https://lnd.example;macaroon=00";

    public static TheoryData<string, bool, SatoraLightningConfigChange> Cases => new()
    {
        // Enabling takes over only an empty slot or one that already points at a Satora wallet.
        { "none", true, SatoraLightningConfigChange.Write },
        { "own-old-key", true, SatoraLightningConfigChange.Write },
        { "other-store", true, SatoraLightningConfigChange.Write },
        { "lnd", true, SatoraLightningConfigChange.None },
        { "internal", true, SatoraLightningConfigChange.None },
        // Disabling removes only this store's own Satora wallet.
        { "own-old-key", false, SatoraLightningConfigChange.Remove },
        { "other-store", false, SatoraLightningConfigChange.None },
        { "lnd", false, SatoraLightningConfigChange.None },
        { "internal", false, SatoraLightningConfigChange.None },
        { "none", false, SatoraLightningConfigChange.None }
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void NeverReplacesOrRemovesALightningNodeTheMerchantSetUp(string current, bool enabled, SatoraLightningConfigChange expected)
    {
        Assert.Equal(expected, SatoraStoreLightningConfig.Decide(Config(current), "store", enabled));
    }

    // Only the owner's explicit replace takes over a node the merchant set up; disabling ignores it.
    [Theory]
    [InlineData("none")]
    [InlineData("own-old-key")]
    [InlineData("other-store")]
    [InlineData("lnd")]
    [InlineData("internal")]
    public void AReplaceWritesOverAnyNode(string current)
    {
        Assert.Equal(SatoraLightningConfigChange.Write, SatoraStoreLightningConfig.Decide(Config(current), "store", enabled: true, replace: true));
        Assert.Equal(SatoraStoreLightningConfig.Decide(Config(current), "store", enabled: false),
            SatoraStoreLightningConfig.Decide(Config(current), "store", enabled: false, replace: true));
    }

    [Fact]
    public void AReplaceSwapsTheMerchantsNodeForTheStoresOwnStringAndExcludesLnurl()
    {
        var store = new StoreData { Id = "store" };
        store.SetPaymentMethodConfig(Lightning, new LightningPaymentMethodConfig { ConnectionString = Lnd });

        Assert.True(SatoraStoreLightningConfig.Apply(store, Lightning, enabled: true, "key", replace: true));

        Assert.Equal(SatoraLightningConnectionString.Format("store", "key"), Current(store)?.GetExternalLightningUrl());
        Assert.True(store.GetStoreBlob().IsExcluded(PaymentTypes.LNURL.GetPaymentMethodId("BTC")));
    }

    // The action reports the wallet as the store's Lightning node, which it is not while BTC-LN is turned off.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AReplaceTurnsLightningBackOn(bool alreadyTheStoresString)
    {
        var store = new StoreData { Id = "store" };
        if (alreadyTheStoresString)
            SatoraStoreLightningConfig.Apply(store, Lightning, enabled: true, "key");
        else
            store.SetPaymentMethodConfig(Lightning, new LightningPaymentMethodConfig { ConnectionString = Lnd });
        SetExcluded(store, Lightning.PaymentMethodId, true);

        Assert.True(SatoraStoreLightningConfig.Apply(store, Lightning, enabled: true, "key", replace: true));

        Assert.False(store.GetStoreBlob().IsExcluded(Lightning.PaymentMethodId));
        Assert.Equal(SatoraLightningConnectionString.Format("store", "key"), Current(store)?.GetExternalLightningUrl());
    }

    [Fact]
    public void EnablingLeavesLightningTurnedOffIfTheMerchantTurnedItOff()
    {
        var store = new StoreData { Id = "store" };
        SetExcluded(store, Lightning.PaymentMethodId, true);

        Assert.True(SatoraStoreLightningConfig.Apply(store, Lightning, enabled: true, "key"));

        Assert.True(store.GetStoreBlob().IsExcluded(Lightning.PaymentMethodId));
    }

    // The merchant may have turned LNURL back on; writing the same string turns it off again.
    [Fact]
    public void WritingTheSameStringStillExcludesLnurl()
    {
        var store = new StoreData { Id = "store" };
        SatoraStoreLightningConfig.Apply(store, Lightning, enabled: true, "key");
        SetExcluded(store, PaymentTypes.LNURL.GetPaymentMethodId("BTC"), false);

        Assert.True(SatoraStoreLightningConfig.Apply(store, Lightning, enabled: true, "key"));

        Assert.True(store.GetStoreBlob().IsExcluded(PaymentTypes.LNURL.GetPaymentMethodId("BTC")));
    }

    // As BTCPay reads it: LightningListener and LightningLikePaymentHandler parse BTC-LN with the handler's serializer.
    [Fact]
    public void EnablingWritesTheStoresOwnStringAndExcludesLnurl()
    {
        var store = new StoreData { Id = "store" };

        Assert.True(SatoraStoreLightningConfig.Apply(store, Lightning, enabled: true, "key"));

        Assert.Equal(SatoraLightningConnectionString.Format("store", "key"), Current(store)?.GetExternalLightningUrl());
        Assert.True(store.GetStoreBlob().IsExcluded(PaymentTypes.LNURL.GetPaymentMethodId("BTC")));
        Assert.False(SatoraStoreLightningConfig.Apply(store, Lightning, enabled: true, "key"));
    }

    [Fact]
    public void ARotatedKeyRewritesTheStoresOwnString()
    {
        var store = new StoreData { Id = "store" };
        SatoraStoreLightningConfig.Apply(store, Lightning, enabled: true, "old");

        Assert.True(SatoraStoreLightningConfig.Apply(store, Lightning, enabled: true, "new"));

        Assert.Equal(SatoraLightningConnectionString.Format("store", "new"), Current(store)?.GetExternalLightningUrl());
    }

    [Fact]
    public void DisablingRemovesOnlyTheStoresOwnString()
    {
        var own = new StoreData { Id = "store" };
        SatoraStoreLightningConfig.Apply(own, Lightning, enabled: true, "key");
        var merchants = new StoreData { Id = "store" };
        merchants.SetPaymentMethodConfig(Lightning, new LightningPaymentMethodConfig { ConnectionString = Lnd });

        Assert.True(SatoraStoreLightningConfig.Apply(own, Lightning, enabled: false, "key"));
        Assert.False(SatoraStoreLightningConfig.Apply(merchants, Lightning, enabled: false, "key"));
        Assert.False(SatoraStoreLightningConfig.Apply(merchants, Lightning, enabled: true, "key"));

        Assert.Null(Current(own));
        Assert.Equal(Lnd, Current(merchants)?.GetExternalLightningUrl());
    }

    private static readonly LightningLikePaymentHandler Lightning = CreateHandler();

    private static LightningLikePaymentHandler CreateHandler()
    {
        var nbx = new NBXplorerNetworkProvider(ChainName.Regtest);
        var btc = new BTCPayNetwork { CryptoCode = "BTC", NBXplorerNetwork = nbx.GetFromCryptoCode("BTC") };
        // Only the payment method id and the serializer, built from the network, are used.
        return new LightningLikePaymentHandler(PaymentTypes.LN.GetPaymentMethodId("BTC"), null!, null!, btc, null!, null!, null!, null!);
    }

    private static void SetExcluded(StoreData store, PaymentMethodId paymentMethodId, bool excluded)
    {
        var blob = store.GetStoreBlob();
        blob.SetExcluded(paymentMethodId, excluded);
        store.SetStoreBlob(blob);
    }

    private static LightningPaymentMethodConfig? Current(StoreData store) =>
        store.GetPaymentMethodConfig(Lightning.PaymentMethodId) is { } config ? Lightning.ParsePaymentMethodConfig(config) : null;

    private static LightningPaymentMethodConfig? Config(string kind)
    {
        switch (kind)
        {
            case "none":
                return null;
            case "internal":
                var internalNode = new LightningPaymentMethodConfig();
                internalNode.SetInternalNode();
                return internalNode;
            default:
                return new LightningPaymentMethodConfig
                {
                    ConnectionString = kind switch
                    {
                        "own-old-key" => SatoraLightningConnectionString.Format("store", "old"),
                        "other-store" => SatoraLightningConnectionString.Format("other", "key"),
                        _ => Lnd
                    }
                };
        }
    }
}
