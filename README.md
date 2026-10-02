# Satora plugin for BTCPay Server

Satora is a [BTCPay Server](https://btcpayserver.org) plugin that gives each
store its own wallet on [Arkade](https://arkade.money). The plugin's page shows
the wallet's balance and the store's earlier stablecoin swaps.

## Requirements

BTCPay Server 2.4.2 or later.

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

**Warning:** dropping `SatoraSettings` deletes each store's encrypted wallet
seed. Without a copy of its seed phrase, the wallet's funds are lost.

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
