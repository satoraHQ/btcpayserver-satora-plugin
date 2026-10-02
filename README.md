# Satora plugin for BTCPay Server

Satora is a [BTCPay Server](https://btcpayserver.org) plugin that gives each
store its own Satora wallet on [Arkade](https://arkade.money). Customers pay
into it over Lightning (BTC-LN), and Satora swaps turn each payment into
bitcoin in the wallet. The store holds its own keys, with no custodian in
between.

The Satora page, under Wallets in the store's menu, sets up the store's Satora
wallet, shows its balance and lists earlier swaps (see [Wallet](#wallet)).
Lightning payments land in the wallet (see [Lightning](#lightning)).

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
including swaps waiting for a refund. Disable the wallet first, so no new swaps
start. The refusal lists what blocks it. A swap that pays an invoice is named
by that invoice, which BTCPay's invoice search finds. Everything else is named
by its id: incoming Lightning swaps without a BTCPay invoice and swaps of
earlier plugin versions. Replacing a wallet that holds funds needs a typed
confirmation. The replaced seed's ciphertext is kept in the
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

## Lightning

Enabling the wallet makes it the store's Lightning node (BTC-LN) when the store
has none or already uses a Satora wallet. Any other node stays in place, and
disabling the wallet removes only the wallet's own connection string. Starting
BTCPay never changes the setting. To use the wallet instead, open **Use the
Satora wallet as this store's Lightning node** on the Satora page and type
`REPLACE`. This replaces the store's Lightning connection string with the
wallet's, turns Lightning on at checkout if it was off, and turns LNURL off. To
go back, set the old node up again in the store's Lightning settings.

Each Lightning invoice is a Lightning to Arkade swap for the amount due. The
customer pays exactly the invoice, and the store pays the swap fee. If the fee
would be more than 3% of the amount due plus 100 sats, checkout offers the
invoice without Lightning. Each payment is claimed into the wallet and recorded
on the invoice once the wallet holds the claim's receipt. A claim that lands
after the invoice expired is recorded as paid late. A claim the swap server
reports but the wallet has no receipt for is never recorded as a payment.

- On mainnet, keep invoice expiry at 20 minutes or less. A longer invoice is
  offered without Lightning, because the swap server allows 30 minutes to claim
  a swap, the SDK keeps the last 5 as a margin, and 5 more are left for the
  payment to arrive and be claimed. BTCPay's default expiry of 15 minutes fits.
  Off mainnet each new swap is checked against the window its server reports.
- LNURL-pay and Lightning addresses are turned off for the store, because the
  swap server cannot put a description hash in the invoice. Top-up invoices
  without an amount are offered without Lightning.
- The wallet does not pay BTCPay's Lightning payouts and refunds. Pay them
  another way.

The store's connection string, `type=satora;storeid=<store>;key=<key>`, carries
a key of its own. Anyone with the string can use the wallet as a Lightning node
in BTCPay, so keep it private. **Rotate Lightning connection key** on the
Satora page replaces the key and updates the store's Lightning settings.
Replacing the wallet replaces the key too.

BTCPay notifies the store's owners and the server admins when:

- a claim fails twice
- a paid swap is still unclaimed 10 minutes before its claim deadline
- a paid swap was refunded before it could be claimed
- the swap server reports a claim the wallet has no receipt for. A claim that
  can still be finished is credited. Otherwise check the wallet and settle the
  invoice by hand.
- the swap server does not know a swap
- swap statuses could not be read for 5 minutes while a payment could still
  arrive
- a swap reaches an unexpected status.

An alert about a swap that pays an invoice names the invoice and links to it.

Each alert goes out once per swap, or once per outage for status alerts, and a
BTCPay restart does not send it again. After a restart, a claim that keeps
failing is reported again once it has failed twice more.

## Support

Contact Satora support at [support@satora.io](mailto:support@satora.io) or on
Telegram at [t.me/satora_io](https://t.me/satora_io). For a problem with a
payment, include its swap id.

## Data & storage

The plugin stores its data in the BTCPay Postgres database under the schema
`"BTCPayServer.Plugins.Satora"` (settings incl. the encrypted wallet seed, swap
transactions and Lightning swaps). Its EF Core migration-history table lives in
the search-path schema (typically `public`) as a table named
`BTCPayServer.Plugins.Satora`.

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

The integration tests of the wallet and the Lightning swap table are skipped
unless `SATORA_TEST_POSTGRES` holds a connection string to a Postgres 13 or
later server where they may create and drop a database. The ones that read a
wallet's balance or address also need `SATORA_TEST_ARKD`, the URL of a regtest
arkd. They only read from arkd.

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
`runtimes/<rid>/native/`, where the SDK's resolver looks first, but never runs
cargo: after changing the SDK, run `just build-native` again and rebuild.
BTCPay's Docker images are Linux, so a `.btcpay` built this way also needs the
library built for `x86_64-unknown-linux-gnu` and `aarch64-unknown-linux-gnu`
(`native/target/<triple>/release/`); without them `just verify-pack` fails. CI
runs the same check on a package built from the published `Satora.Sdk`.

### Running BTCPay with the plugin

BTCPay loads a plugin from its build output, through `BTCPAY_DEBUG_PLUGINS`,
only when BTCPay itself is a Debug build, and `just build` is Release. A Debug
build of the plugin builds the BTCPay checkout in Debug too. To use a local
SDK, export `SatoraSdkPath` first, as above:

```bash
dotnet build BTCPayServer.Plugins.Satora -c Debug
```

BTCPay needs Postgres and an NBXplorer on the same regtest bitcoind as the
Satora backend. The [ArkLabsHQ/arkade-regtest](https://github.com/ArkLabsHQ/arkade-regtest)
stack serves NBXplorer on 127.0.0.1:32838 and Postgres on 127.0.0.1:39372 with
trust auth. Start BTCPay from its project directory: a build that was not
published has no `wwwroot` beside its assembly, so BTCPay takes its content
root from the working directory, and started anywhere else it finds no web
root and its pages fail with an `ArgumentNullException` from `LanguageService`.

```bash
plugin=$PWD/BTCPayServer.Plugins.Satora/bin/Debug/net10.0/BTCPayServer.Plugins.Satora.dll
data=$HOME/.btcpay-satora-dev
cd btcpayserver/BTCPayServer
BTCPAY_NETWORK=regtest BTCPAY_CHAINS=btc \
BTCPAY_DATADIR=$data BTCPAY_PLUGINDIR=$data/Plugins \
BTCPAY_POSTGRES="User ID=postgres;Host=127.0.0.1;Port=39372;Database=btcpayserver" \
BTCPAY_BTCEXPLORERURL=http://127.0.0.1:32838/ \
BTCPAY_DEBUG_PLUGINS=$plugin \
dotnet bin/Debug/net10.0/BTCPayServer.dll
```

The log then shows `Running plugin BTCPayServer.Plugins.Satora`, and BTCPay
listens on http://127.0.0.1:23002, its regtest default. `BTCPAY_DATADIR` and
`BTCPAY_PLUGINDIR` keep this BTCPay's data and plugins apart from any other
BTCPay on the machine. The wallets use the SDK's regtest defaults (the Satora
backend on localhost:3333, the Arkade server on localhost:7070 and esplora on
localhost:3000/api); [Wallet](#wallet) says how to point them elsewhere.

Run the Satora backend's swap daemon with
`LENDASWAP_REGTEST_SERVER_TIMEOUT_SECS=1800`: its default window of 120 seconds
leaves no time to claim.
