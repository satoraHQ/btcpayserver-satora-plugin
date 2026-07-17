# Satora — Stablecoin payments for BTCPay Server

> # ⚠️ NOT PRODUCTION READY — UNDER HEAVY DEVELOPMENT
>
> **This plugin is experimental and under active development. It is NOT ready
> for real use.** Do **not** install it on a production BTCPay Server and do
> **not** point real funds at it. Swaps do not yet complete end-to-end (see
> [Status](#status)); APIs, data schemas and behaviour may change or break
> without notice; and bugs may result in **loss of funds**. Use only at your 
> own risk.

Satora is a [BTCPay Server](https://btcpayserver.org) plugin that lets a store
accept **stablecoin payments** (USDC, USDT, and others across Polygon, Arbitrum
and Ethereum) and receive the value as **BTC on [Arkade](https://arkade.money)**
— non-custodially, via atomic swaps.

The customer pays in the stablecoin of their choice at checkout; the plugin
runs the swap and the merchant ends up with BTC in a store-owned Arkade wallet.
There is no third-party custodian in the path: the store holds its own keys.

## How it works

1. **Embedded Arkade wallet.** Each store gets its own Arkade wallet, derived
   from an encrypted BIP-39 seed stored per-store. The wallet is provided by the
   embedded [`Satora.Sdk`](https://github.com/lendasat/lendaswap) — the plugin
   does **not** depend on any other BTCPay plugin.

2. **Checkout.** When a customer selects a stablecoin, the plugin creates a swap
   (EVM stablecoin → BTC on Arkade) and shows the customer a deposit address to
   send funds to.

3. **Background settlement.** A background watcher (`SatoraSwapWatcher`, ~60s
   interval) drives every in-flight swap forward:
    - polls the deposit address on-chain until the customer's funds arrive,
    - submits the gasless funding step (the server pays gas),
    - claims the resulting BTC into the store's Arkade wallet,
    - marks the BTCPay invoice paid, recording a real Arkade payment against it.

Everything is keyed by swap id, so the address shown to the customer is always
the address the watcher monitors.

## Supported assets

Stablecoins/tokens: **USDC, USDT, USDT0, EURC, tBTC, USAT, WBTC, XAUt**
Chains: **Ethereum, Polygon, Arbitrum**
(availability varies per token — see `Models/SatoraData.cs`)

Settlement target: **BTC on Arkade**.

## Status

Deposit detection, server-side funding, Arkade claim and BTCPay invoice
settlement are wired into the background watcher. Swap state is persisted in
the plugin database and the watcher retries non-terminal swaps on restart.

The plugin is still experimental. Terminal recovery states such as expired,
invalid, late-funded or refunded swaps depend on SDK/contract-manager recovery
behavior. The plugin does not expose an EVM refund workflow because the merchant
wallet is Arkade/BTC-side only.

On-chain Arkade-to-Bitcoin exits are not implemented yet because the local SDK
does not currently expose an on-chain withdrawal API.

## Operator guide

1. Install the plugin package and restart BTCPay.
2. Open the store's **Satora** wallet page.
3. As the store owner, generate a new seed or restore an existing seed.
4. Store the seed offline. The generated seed is displayed once.
5. Re-enter the seed to confirm the backup. The wallet is enabled only after
   this confirmation step.
6. Enable Satora in the wallet configuration. The plugin syncs the native
   Satora payment method and the store Lightning connection string.
7. Use the wallet page to view Arkade balances, withdrawals and swap status.

## Security model

- Each store has its own BIP-39 seed. The seed is encrypted at rest in the
  plugin settings row.
- Only the store owner, or a server admin, can generate, import, confirm or
  enable the wallet seed.
- Withdrawals use BTCPay's store payout permission surface. Review and execute
  are separate requests so the operator sees destination type, amount and fee
  estimate before submission.
- Stablecoin swaps are created from the BTCPay invoice's server-calculated
  remaining due amount, not from a client-supplied amount.
- Invoice settlement uses a deterministic payment id per swap so repeated
  watcher runs do not settle the same invoice payment twice.

## Recovery and monitoring

- Non-terminal swaps are retried by the background watcher every 60 seconds.
- The Satora wallet page lists every swap with a human status, action hint,
  stale flag and retry button where retry is supported.
- The page also shows total, active, needs-action, stale and completed swap
  counts.
- A CSV export is available from the swaps table for bookkeeping and external
  reconciliation.
- Terminal recovery states remain visible but are not force-refunded by the
  plugin. Recovery should happen through the SDK contract manager so funds are
  delivered to the BTC/Arkade side.

## Repository layout

| Path                           | What                                                           |
|--------------------------------|----------------------------------------------------------------|
| `BTCPayServer.Plugins.Satora/` | The plugin source                                              |
| `btcpayserver/`                | BTCPay Server checkout, referenced by the plugin at build time |

## Building

This repo uses a [`justfile`](justfile). To build the plugin:

```bash
just build
```

Equivalently, the plugin references the local `btcpayserver` checkout, so you
can build it directly from the plugin directory:

```bash
cd BTCPayServer.Plugins.Satora
dotnet build -c Release
```

## Packaging a `.btcpay`

To build and produce an installable `.btcpay` package in one step:

```bash
just package
```

Under the hood this runs BTCPay's plugin packer (included in the `btcpayserver`
checkout):

```bash
# from the repository root
dotnet run --project btcpayserver/BTCPayServer.PluginPacker -- \
  BTCPayServer.Plugins.Satora/bin/Release/net10.0 \
  BTCPayServer.Plugins.Satora \
  BTCPayServer.Plugins.Satora/dist
```

The package is written to
`BTCPayServer.Plugins.Satora/dist/BTCPayServer.Plugins.Satora/<version>/BTCPayServer.Plugins.Satora.btcpay`.
(The `dist/` directory is git-ignored.)

## Installing

In BTCPay Server: **Server Settings → Plugins → Upload Plugin**, select the
`.btcpay` file, then restart when prompted.

## Data & storage

The plugin stores its data in the BTCPay Postgres database under the schema
`"BTCPayServer.Plugins.Satora"` (settings incl. the encrypted wallet seed, and
swap transactions). Its EF Core migration-history table lives in the search-path
schema (typically `public`) as a table named `BTCPayServer.Plugins.Satora`.

To fully purge the plugin's data:

```sql
DROP SCHEMA "BTCPayServer.Plugins.Satora" CASCADE;
DROP TABLE public."BTCPayServer.Plugins.Satora";
DELETE
FROM "Settings"
WHERE "Id" = 'BTCPayServer.Plugins.Satora.PluginMigrationRunner+PluginDataMigrationHistory';
```

Then restart BTCPay so the plugin recreates the schema on boot.

⚠️ Dropping `SatoraSettings` destroys the encrypted Arkade wallet seed — any
funds held in that wallet become unrecoverable.
