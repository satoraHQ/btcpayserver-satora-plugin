# Satora plugin for BTCPay Server

Satora is a [BTCPay Server](https://btcpayserver.org) plugin that gives each
store its own Satora wallet on [Arkade](https://arkade.money). The store holds
its own keys, with no custodian in between.

The Satora page, under Wallets in the store's menu, sets up the store's Satora
wallet, shows its balance and lists earlier swaps (see [Wallet](#wallet)).

## Requirements

BTCPay Server 2.4.2 or later, on mainnet, signet or regtest. Satora does not
support testnet.

## Installing

In BTCPay Server: **Server Settings → Plugins → Upload Plugin**, select the
`.btcpay` file, then restart when prompted.

## Wallet

Each store's Satora wallet is a hot wallet: its seed is stored on the server. As
with BTCPay's own hot wallets, only a server admin can generate or restore it,
show its recovery phrase or enable it, unless **Server Settings → Policies**
allows hot wallets for all users. Other users who can change the store's
settings see its state and can disable it.

BTCPay shows a hot wallet's recovery phrase to anyone who can change the
store's settings, so for an existing seed this is stricter. If an admin turns
the policy off after other users set up wallets, those users can no longer show
the phrase, restore, replace or enable the wallet. An enabled wallet stays
enabled, and they can still disable it.

Back up BTCPay's data protection keys, the key files in BTCPay's data
directory, with the database. The wallet's seed is encrypted with them. If
BTCPay loses them, the wallet stays locked until it is restored from its
recovery phrase. BTCPay then logs an error once and notifies the store's owners
and the server admins.

A seed saved by an earlier version of this plugin was encrypted with a key
derived from the store id, and is not read. The Satora page shows such a wallet
as **Restore required** and offers only a restore from its recovery phrase. The
earlier ciphertext is kept in `SatoraRetiredSeeds`.

A wallet cannot be replaced while swaps that depend on it are in progress,
including swaps waiting for a refund. The refusal names each blocking swap of
an earlier plugin version by its id. Replacing a wallet that holds funds needs
a typed confirmation. The replaced seed's ciphertext is kept in the
`SatoraRetiredSeeds` table. BTCPay's log records each wallet generated or
restored, each refusal and its reason, and each time the wallet is enabled or
disabled.

Back up `<BTCPay data directory>/SatoraWallets/` with the database and the
keys. The SDK keeps each store's swap and contract databases there, in
`<network>/<store id>/`, readable only by its owner on Unix. Swaps cannot be
funded, claimed or refunded without them.

Satora runs on mainnet, signet and regtest; on testnet the wallet is
unavailable. On signet the wallets use Mutinynet (the SDK's signet defaults)
unless the endpoints are overridden. Off mainnet, `BTCPAY_SATORA_BASEURL`,
`BTCPAY_SATORA_ARKADESERVERURL` and `BTCPAY_SATORA_ESPLORAURL` (or
`satora_baseurl=` and so on in BTCPay's `settings.config`) point the wallets at
another Satora backend, Arkade server and esplora. Mainnet always uses the SDK
defaults.

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

**Warning:** dropping `SatoraSettings` deletes each Satora wallet's encrypted
seed. Without its recovery phrase, a wallet's funds are lost.

## Repository layout

| Path                                 | What                                                           |
|--------------------------------------|----------------------------------------------------------------|
| `BTCPayServer.Plugins.Satora/`       | The plugin source                                              |
| `BTCPayServer.Plugins.Satora.Tests/` | Unit tests (xunit), run without a BTCPay host                  |
| `btcpayserver/`                      | BTCPay Server checkout, referenced by the plugin at build time |

## Development

The plugin builds against BTCPay Server v2.4.4 (the `btcpayserver/` submodule).
`global.json` requires a .NET 10 SDK, 10.0.400 (the one BTCPay 2.4.4's Docker
build uses) or later; the newest installed 10.0 SDK is picked.

```bash
git submodule update --init
just build        # Release build of the plugin
just test         # unit tests
just pack         # dist/BTCPayServer.Plugins.Satora/<version>/BTCPayServer.Plugins.Satora.btcpay
just verify-pack  # fails unless the .btcpay has linux-x64 and linux-arm64 native libraries
```

`just pack` runs BTCPay's plugin packer (`btcpayserver/BTCPayServer.PluginPacker`)
on the build output.

The wallet's integration tests are skipped unless `SATORA_TEST_POSTGRES` holds
a connection string to a Postgres 13 or later server where they may create and
drop a database. The ones that read a wallet's balance or address also need
`SATORA_TEST_ARKD`, the URL of a regtest arkd. They only read from arkd.

### Satora.Sdk

By default the plugin uses the `Satora.Sdk` package from nuget.org, the only
feed in `nuget.config`. Restoring it, locally or in CI, needs no credentials.

To work against a local SDK instead, set `SatoraSdkPath` to the absolute path
of the `client-sdk/dotnet-sdk` directory in a checkout of satoraHQ/swap. A
relative path does not work, because MSBuild resolves it against each
project's own directory. Build the SDK's native library first (Rust and
`protoc` required):

```bash
(cd ../swap/client-sdk/dotnet-sdk && just build-native)   # native/target/debug
export SatoraSdkPath=$PWD/../swap/client-sdk/dotnet-sdk
just build test
```

MSBuild reads `SatoraSdkPath` from the environment, so plain `dotnet build` and
`dotnet test` pick it up too. The build copies the host library to
`runtimes/<rid>/native/`, where the SDK's resolver looks first; that is enough
to run BTCPay locally with the plugin. BTCPay's Docker images are Linux, so a
`.btcpay` built this way also needs the library built for
`x86_64-unknown-linux-gnu` and `aarch64-unknown-linux-gnu`
(`native/target/<triple>/release/`); without them `just verify-pack` fails. CI
runs the same check on a package built from the published `Satora.Sdk`.
