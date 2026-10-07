# Satora plugin for BTCPay Server

Satora is a [BTCPay Server](https://btcpayserver.org) plugin that gives each
store its own Satora wallet on [Arkade](https://arkade.money). Customers pay
into it over Lightning (BTC-LN) or on chain with **Bitcoin (Satora)**
(`BTC-SATORA-CHAIN`), and Satora swaps turn each payment into bitcoin in the
wallet. The wallet also pays BTCPay's Lightning payouts and refunds, and the
store can withdraw from it to Arkade, Lightning and Bitcoin destinations. The
store holds its own keys, with no custodian in between.

The Satora page, under Wallets in the store's menu, sets up the store's Satora
wallet, shows its balance and lists earlier swaps (see [Wallet](#wallet)).
Lightning and on-chain payments land in the wallet (see [Lightning](#lightning)
and [On-chain payments](#on-chain-payments)). The wallet pays out over
Lightning, to Arkade and on chain (see [Withdrawals](#withdrawals)).

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
including swaps waiting for a refund, or while a withdrawal waits to be marked
sent or failed. Disable the wallet first, so no new swaps start. The refusal
lists what blocks it. A swap that pays an invoice is named by that invoice,
which BTCPay's invoice search finds. Everything else is named by its id:
incoming Lightning swaps without a BTCPay invoice, Lightning payments out of
the wallet, withdrawals and swaps of earlier plugin versions. Replacing a
wallet that holds funds needs a typed confirmation. The replaced seed's
ciphertext is kept in the `SatoraRetiredSeeds` table. BTCPay's log records each
wallet generated or restored, each refusal and its reason, and each time the
wallet is enabled or disabled.

Back up `<BTCPay data directory>/SatoraWallets/` with the database and the
keys. The SDK keeps each store's swap and contract databases there, in
`<network>/<store id>/`, readable only by its owner on Unix. Swaps cannot be
funded, claimed or refunded without them.

Run one BTCPay instance per database. Never start a copy of the database and
data directory while the original runs, for example a new host started before
the old one stops during a migration, or a staging server restored from
production's backup. Two instances running the same wallets can pay one swap
twice, because each store's swap workers, its spending lock and the SDK's
record of each funding live in one process and its data directory. BTCPay's
automated payout processors assume the same, as they pay a payout before
recording it.

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
- BTCPay's Lightning payouts and refunds are paid from the wallet. Each payment
  is an Arkade to Lightning swap whose VHTLC the wallet funds. The payment
  completes once the swap server claims the VHTLC, which it can do only by
  paying the invoice. If the server will no longer pay it, the payment fails
  and the VHTLC is refunded into the wallet. The wallet does not pay the same
  invoice twice, or an invoice without an amount. It refuses a swap whose fees
  pass BTCPay's fee cap for the payment, or 3% plus 100 sats when there is
  none.

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

For payments out of the wallet it also notifies when:

- the outcome of funding a swap cannot be resolved yet. Do not pay that invoice
  another way.
- funding a swap keeps failing, with the SDK's reason
- refunding a failed payment fails twice
- a refund without the swap server keeps failing, or is still refused two hours
  past its refund locktime. The alert says what the swap still holds. Close the
  payment on the withdrawals page only once it holds nothing.
- payment statuses could not be read for 5 minutes
- a payment's funds were refunded without the swap server
- a withdrawal's outcome is unknown.

An alert about a swap that pays an invoice names the invoice and links to it.

Each alert goes out once per swap, or once per outage for status alerts, and a
BTCPay restart does not send it again. The on-chain and automatic withdrawal
alerts below work the same way. After a restart, a claim, settle or automatic
withdrawal that keeps failing is reported again once it has failed twice more.

## On-chain payments

Enabling the wallet also adds **Bitcoin (Satora)** (`BTC-SATORA-CHAIN`) to
checkout. It is a payment method of its own and never touches the store's
on-chain wallet. Disabling or replacing the Satora wallet removes it. The
create invoice form, the checkout settings, the invoice list and an invoice's
payments show it by name instead of by id. They do the same for BTCPay's own
payment methods, such as Lightning for `BTC-LN`, on every store, including
stores that do not use the Satora wallet. Each payment is a Bitcoin to Arkade
swap. The customer sends one transaction to the swap's address, the swap server
funds an Arkade VHTLC after one confirmation, and the wallet claims it. The
wallet receives exactly the amount due. The customer pays the swap fee on top,
which checkout shows as the network cost.

- Turn on lazy payment methods in the store's checkout settings ("Only enable
  the payment method after user explicitly chooses it"). Without it every
  invoice creates a swap whether or not the customer pays on chain, and each
  one stays open on the swap server and in the plugin until it expires about
  12 hours later.
- Checkout offers this method only for amounts the swap server takes. The
  server sets a minimum for the route, tens of thousands of sats on mainnet
  (its `/swap-pairs` lists the current one), and needs liquidity for the
  amount. The method is also left out when the swap fee would be more than 3%
  of the amount due plus 2,000 sats.
- On mainnet, keep invoice expiry under about 9 hours 55 minutes. A longer
  invoice is offered without this method and creates no swap. The wallet can
  claim until 12 hours after the swap was created, by the chain's median time
  past, less a 5 minute margin, and the swap server funds nothing after that.
  An invoice has to expire at least 2 hours before that deadline (5 minutes off
  mainnet), so that a payment made just before expiry can still confirm. A
  payment the swap server has not funded by the deadline, seen or confirmed,
  goes back to the customer. A claim already started is still finished after
  the deadline. Once the swap server can take the VHTLC back, the SDK is asked
  whether a claim can still complete. If one can, the claim continues and the
  payment is never refunded. If none can, the payment goes back to the
  customer.
- The customer has to pay at least the amount shown in a single output. Smaller
  payments do not add up. Once such an output is seen, in the mempool or
  confirmed, the invoice is processing. It settles once the wallet holds the
  claim's receipt, after one confirmation. If an unconfirmed output disappears,
  because a fee bump paid elsewhere or it was dropped, the payment comes off
  the invoice again. A payment after the invoice expires is recorded as paid
  late. A claim the swap server reports but the wallet has no receipt for never
  settles the invoice.
- An output larger than the amount shown still pays the invoice, but the swap
  server keeps the excess. The invoice does not show as overpaid. Once the
  wallet has claimed the swap, the invoice's Events note the excess and BTCPay
  notifies the store, so it can settle with the customer. Neither happens
  before the claim, while the payment can still be replaced or refunded.
- After a partial payment through another method, the prompt gets a new swap
  for what is left, the way BTCPay renews a Lightning invoice. The same happens
  when a payment taken off the invoice changes the amount due. Checkout shows
  no address while the swap's amount differs from the amount due. If the swap
  server refuses the new swap, or the prompt's swap fails or expires while the
  invoice is open, the method is turned off for the invoice. BTCPay then makes
  a new swap when the customer picks the method again, or leaves it out. A
  payment to an old address is credited or refunded as below, and an expired
  swap's address is watched until an hour after the invoice expires. If the
  wallet claims a payment after the invoice's other payments paid it in full,
  the invoice's Events note it and BTCPay notifies the store, as BTCPay does
  not show a settled invoice as overpaid. Neither happens before the claim,
  while the payment can still be replaced or refunded.

### Refunds

These payments go back to the customer:

- an underpayment
- a payment the swap server refuses after its sanctions screening
- a payment still unconfirmed at the claim deadline
- a payment whose VHTLC the server took back because it was not claimed in time
- a funded swap no claim can complete once the server can take its VHTLC back
- bitcoin sent to a swap's address after the swap ended.

A payment that was on the invoice comes off it, and BTCPay marks it
unaccounted, as for a replaced transaction. An underpayment, or bitcoin sent
after the swap ended, never counted toward the invoice. BTCPay notifies the
store, and the swap is listed on the **On-chain refunds** page with what sits
unspent at its address. The invoice's Events note that it needs a refund, when
the refund is broadcast and when the swap closes.

If the swap server reports that it swept the deposit but the output is still at
the address, the swap is listed only if the output is still there ten minutes
later. The sweep may not have reached esplora yet.

Only the Satora wallet can sign a refund, and only after the address's refund
locktime. That is 24 hours after the swap was created, by median time past,
which trails the clock by an hour or more. To refund, someone who can change
the store's settings enters the customer's Bitcoin address on the refunds page
and types `REFUND`. Nothing is refunded until the store enters an address.

The page shows the locktime, which is when the first refund attempt runs, and
the address can be changed until then. The refund goes out as soon as the chain
accepts it, usually within two hours of the locktime. It pays mempool's
recommended fee for confirmation within an hour, or an esplora's six-block
estimate without mempool's API, at a fee rate of at least 1 sat/vB and a fee of
at most 10,000 sats. A refund stays listed until it confirms. Outputs it left
out because of the fee cap, or that arrived since, are swept to the same
address. The wallet cannot be replaced while any swap waits for its refund. For
a swap listed because no claim could complete, the SDK is asked again before
each refund attempt, and the refund is held while a claim can complete after
all.

Once the refund could go out, a swap whose address holds nothing closes without
a refund, and BTCPay notifies the store. This happens when the payment was
replaced or dropped. Two reads at least ten minutes apart have to agree first,
as they do for a swap whose unconfirmed funding is gone by the claim deadline.
After a refund attempt, the swap also stays open while a transaction spending
from its address is in the mempool or pays the refund address.

A swap whose address holds only outputs worth less than a refund's fee closes
the same way, but only once that has held for 24 hours, so a fee spike does not
cost the customer the refund. The 24 hours start again when BTCPay restarts.
Meanwhile the SDK is asked for the refund every ten minutes, because it can
combine outputs that are each too small on their own, and the refund goes out
as soon as it is worth its fee.

If the store will not make a refund, for example because it paid the customer
back another way, type `DISMISS` under the swap on the refunds page. The swap
is no longer followed and nothing more is sent from its address, though a
refund already broadcast can still confirm. BTCPay's log records the dismissal.
A dismissal is refused while a refund attempt for the swap runs. Try again a
minute later.

If bitcoin reaches the address of a swap that has already closed (the store
was notified with its swap id), open it for a refund again in the database:

```sql
UPDATE "BTCPayServer.Plugins.Satora"."SatoraChainSwaps"
SET "CompletedAt" = NULL, "ClosedReason" = NULL,
    "NeedsRefundAt" = now(), "NeedsRefundReason" = 'LeftoverOutputs'
WHERE "SwapId" = '<swap id>';
```

The swap is then listed on the refunds page and refunded as above, to the
address entered for it before if there was one. A swap that closed without a
refund, or with outputs left after its refund, is reopened as it was, keeping
the reason it needed one; the store's notification gives this statement with
its swap id:

```sql
UPDATE "BTCPayServer.Plugins.Satora"."SatoraChainSwaps"
SET "CompletedAt" = NULL, "ClosedReason" = NULL
WHERE "SwapId" = '<swap id>';
```

BTCPay also notifies the store when:

- a claim fails twice
- a paid swap is still unclaimed 10 minutes before its claim deadline
- the swap server reports a claim the wallet has no receipt for
- the swap server does not know a swap
- statuses could not be read for half an hour
- a refund keeps failing after it should have become possible, and the address
  still holds something to refund
- a refund is held because a claim can complete after all
- a swap closes without a refund, or with outputs left after one
- a customer overpays
- a swap is claimed after its invoice was paid in full
- a swap reaches an unexpected status.

Each alert names the swap's invoice and, unless it is about a refund, links to
it. An invoice's page lists its Bitcoin (Satora) payments with their swap and
claim, and links a payment taken off the invoice to the refunds page.

## Withdrawals

The withdrawals page, opened with **Withdraw** on the Satora page, sends from
the wallet to an Arkade address, a Lightning invoice, a Lightning address or
LNURL, or a Bitcoin address. A Bitcoin withdrawal is a collaborative exit from
Arkade, and its fee comes on top of the amount. Each withdrawal is reviewed
first with its fees quoted, and runs only once per review. If its fees have
grown past the review by the time it runs, it is refused. Amounts use a point
as the decimal separator. A withdrawal to an Arkade address, Lightning address,
LNURL or Bitcoin address can send everything the wallet can spend. An on-chain
exit always leaves the Arkade server's dust amount, 330 sats, in the wallet as
Arkade change.

Withdrawals spend only confirmed and pre-confirmed funds. Recoverable funds
(expired, swept or below dust) stay in the wallet until they are settled into
an Arkade batch, and until then getting them back depends on the Arkade server.
They are settled automatically for every store whose Satora wallet is enabled,
within 5 minutes of turning recoverable on mainnet and 30 seconds on other
networks. This runs even while a withdrawal's outcome is unknown, because a
settle pays only the wallet. A failed settle is retried after 5 minutes, 15
minutes, an hour and then every 6 hours. BTCPay notifies the store's owners and
the server admins once it has failed twice in a row. While recoverable funds
add up to dust or more, **Settle recoverable funds** on the withdrawals page
settles them at once. Recoverable funds below dust, such as the change an
Arkade withdrawal leaves, cannot be settled on their own. They are settled once
more funds become recoverable, and they never hold up a withdrawal.

BTCPay's log records each withdrawal with its amount, fee and transaction, and
each settle with its Arkade batch and whether it was started on the withdrawals
page or automatically. For a Lightning withdrawal the transaction is the one
funding its swap, and the log also records the payment hash of the invoice it
pays. For a Lightning address or LNURL, that is the invoice its endpoint
issued.

If the wallet cannot tell whether an Arkade or on-chain withdrawal went out,
for example because the Arkade server stopped answering mid-send or BTCPay
restarted, the withdrawal is marked unknown or interrupted. No new withdrawal
starts until someone who can change the store's settings checks the balance and
marks it sent or failed. A running withdrawal cannot be marked, and an
interrupted one only 30 minutes after it started. A Lightning withdrawal
finishes by itself, like a Lightning payout. It is sent once the invoice is
paid, and failed once the funds are back in the wallet.

Funds in the last minutes before they expire (about 10 minutes on mainnet) are
left out of every withdrawal and payment, because what they pay would expire
with them. A withdrawal or payment the wallet cannot cover without them sends
nothing, and the wallet renews them in the next Arkade batch. The withdrawals
page then asks to try again in a minute. A Lightning payout or refund fails,
and BTCPay retries it. The spendable balance still includes these funds.
BTCPay's log records each renewal with its Arkade batch.

A Lightning address or LNURL is never handed to the swap server. BTCPay fetches
the invoice itself, as for its own Lightning payouts (an onion address through
BTCPay's Tor proxy), and checks the amounts it accepts. The swap server only
quotes and pays that invoice, so reviewing the withdrawal also asks the payee
for an invoice. To send everything, the swap server first quotes what the
wallet's sats pay after fees, including its estimate of the Lightning fee. The
payee is then asked for an invoice of that amount. If the invoice's actual fee
is higher than the estimate, the payee is asked once more for an invoice lower
by the difference. If it is lower, the difference stays in the wallet.

A Lightning payment's outcome is unknown when the swap server stops answering,
no longer knows the payment, or has neither paid nor refunded it by its refund
locktime. After that locktime the funds are taken back into the wallet without
the server, but the payment stays pending, as the server may have paid first.
The withdrawals page lists these payments, and payments in an unexpected
status, with what each one's VHTLC still holds (**Still held in the swap**).
Once the balance and the payee show what happened, someone who can change the
store's settings closes the payment as paid or failed by typing `CLOSE`.
Closing it as paid marks the BTCPay payout completed or the withdrawal sent.
Closing it as failed cancels the payout or marks the withdrawal failed. Leave a
payment open if its invoice was not paid and its funds are not back yet. They
are taken back from about an hour after the refund locktime. A payment cannot
be closed while its withdrawal is running, or closed as failed while its VHTLC
still holds funds or cannot be read.

### Who can spend from the wallet

Withdrawing needs permission to change the store's settings. It does not need
hot wallet rights, so the wallet keeps working for its store if an admin later
turns hot wallets off.

As the store's Lightning node, the wallet also pays whatever BTCPay pays over
Lightning, under BTCPay's own permissions. That covers payouts approved by
anyone who can manage the store's payouts, refunds, and invoices paid through
the Greenfield API with a key allowed to use the store's Lightning node. With
an automated Lightning payout processor enabled for the store, approved payouts
are paid without anyone opening the Satora page. The Satora pages warn while
one is enabled.

## Support

Contact Satora support at [support@satora.io](mailto:support@satora.io) or on
Telegram at [t.me/satora_io](https://t.me/satora_io). For a problem with a
payment, include its swap id.

## Data & storage

The plugin stores its data in the BTCPay Postgres database under the schema
`"BTCPayServer.Plugins.Satora"` (settings incl. the encrypted wallet seed, swap
transactions, Lightning and on-chain swaps, withdrawals and automatic
withdrawal rules). Its EF Core migration-history table lives in the search-path
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
