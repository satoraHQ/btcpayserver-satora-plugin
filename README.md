# Satora — Stablecoin payments for BTCPay Server

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

Deposit detection and server-side funding are wired up and working. The
**claim + invoice-settlement step is currently disabled** pending a
per-merchant wallet rework — see
[`BTCPayServer.Plugins.Satora/docs/background-watcher.md`](BTCPayServer.Plugins.Satora/docs/background-watcher.md).
Until that lands, swaps detect the deposit and server-fund the VHTLC but do not
yet auto-complete into the merchant wallet. This is a work in progress.

## Repository layout

| Path | What |
|------|------|
| `BTCPayServer.Plugins.Satora/` | The plugin source |
| `btcpayserver/` | BTCPay Server checkout, referenced by the plugin at build time |

## Building

The plugin references the local `btcpayserver` checkout, so build from the
plugin directory:

```bash
cd BTCPayServer.Plugins.Satora
dotnet build -c Release
```

## Packaging a `.btcpay`

Use BTCPay's plugin packer (included in the `btcpayserver` checkout) to produce
an installable `.btcpay` package:

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
DROP TABLE  public."BTCPayServer.Plugins.Satora";
DELETE FROM "Settings"
WHERE "Id" = 'BTCPayServer.Plugins.Satora.PluginMigrationRunner+PluginDataMigrationHistory';
```

Then restart BTCPay so the plugin recreates the schema on boot.

⚠️ Dropping `SatoraSettings` destroys the encrypted Arkade wallet seed — any
funds held in that wallet become unrecoverable.
