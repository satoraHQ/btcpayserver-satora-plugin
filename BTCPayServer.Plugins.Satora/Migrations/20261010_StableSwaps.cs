using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace BTCPayServer.Plugins.Satora.Migrations
{
    [DbContext(typeof(SatoraPluginDbContext))]
    [Migration("20261010_StableSwaps")]
    public class StableSwaps : Migration
    {
        private const string Schema = "BTCPayServer.Plugins.Satora";

        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SatoraStableSwaps",
                schema: Schema,
                columns: table => new
                {
                    SwapId = table.Column<string>(nullable: false),
                    StoreId = table.Column<string>(nullable: false),
                    InvoiceId = table.Column<string>(nullable: false),
                    PaymentMethodId = table.Column<string>(nullable: false),
                    ChainId = table.Column<long>(nullable: false),
                    Token = table.Column<string>(nullable: false),
                    TokenAddress = table.Column<string>(nullable: false),
                    Decimals = table.Column<int>(nullable: false),
                    DepositAddress = table.Column<string>(nullable: false),
                    SourceUnits = table.Column<string>(nullable: false),
                    ReceiveSats = table.Column<long>(nullable: false),
                    VhtlcSats = table.Column<long>(nullable: true),
                    Status = table.Column<string>(nullable: false),
                    VhtlcRefundLocktime = table.Column<DateTimeOffset>(nullable: false),
                    EvmRefundLocktime = table.Column<DateTimeOffset>(nullable: false),
                    FundingStartCutoff = table.Column<DateTimeOffset>(nullable: false),
                    InvoiceExpiresAt = table.Column<DateTimeOffset>(nullable: false),
                    FundingAttemptedAt = table.Column<DateTimeOffset>(nullable: true),
                    FundTxHash = table.Column<string>(nullable: true),
                    PaymentRecorded = table.Column<bool>(nullable: false),
                    ClaimTxId = table.Column<string>(nullable: true),
                    Credited = table.Column<bool>(nullable: false),
                    NeedsAttentionAt = table.Column<DateTimeOffset>(nullable: true),
                    AttentionReason = table.Column<string>(nullable: true),
                    SweepToken = table.Column<string>(nullable: true),
                    SweepDestination = table.Column<string>(nullable: true),
                    SweepAttempted = table.Column<bool>(nullable: false),
                    SweepTxHash = table.Column<string>(nullable: true),
                    HtlcRefundTxHash = table.Column<string>(nullable: true),
                    ClosedReason = table.Column<string>(nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SatoraStableSwaps", x => x.SwapId);
                });

            // A funding takes the swap's amount from its deposit address, so two swaps sharing one would
            // fund from each other's deposits; a reused address is refused at creation. EVM addresses
            // are case-insensitive, and the server may send one checksummed or not.
            migrationBuilder.Sql(
                $"CREATE UNIQUE INDEX \"IX_SatoraStableSwaps_DepositAddress\" ON \"{Schema}\".\"SatoraStableSwaps\" (lower(\"DepositAddress\"))");

            migrationBuilder.CreateIndex(
                name: "IX_SatoraStableSwaps_Open",
                schema: Schema,
                table: "SatoraStableSwaps",
                column: "StoreId",
                filter: "\"CompletedAt\" IS NULL");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SatoraStableSwaps",
                schema: Schema);
        }
    }
}
