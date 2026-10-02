using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace BTCPayServer.Plugins.Satora.Migrations
{
    [DbContext(typeof(SatoraPluginDbContext))]
    [Migration("20261005_ChainSwaps")]
    public class ChainSwaps : Migration
    {
        private const string Schema = "BTCPayServer.Plugins.Satora";

        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SatoraChainSwaps",
                schema: Schema,
                columns: table => new
                {
                    SwapId = table.Column<string>(nullable: false),
                    StoreId = table.Column<string>(nullable: false),
                    InvoiceId = table.Column<string>(nullable: false),
                    Status = table.Column<string>(nullable: false),
                    HtlcAddress = table.Column<string>(nullable: false),
                    DepositSats = table.Column<long>(nullable: false),
                    ReceiveSats = table.Column<long>(nullable: false),
                    VhtlcRefundLocktime = table.Column<DateTimeOffset>(nullable: false),
                    BtcRefundLocktime = table.Column<DateTimeOffset>(nullable: false),
                    InvoiceExpiresAt = table.Column<DateTimeOffset>(nullable: false),
                    PaymentRecorded = table.Column<bool>(nullable: false),
                    ClaimTxId = table.Column<string>(nullable: true),
                    Credited = table.Column<bool>(nullable: false),
                    NeedsRefundAt = table.Column<DateTimeOffset>(nullable: true),
                    NeedsRefundReason = table.Column<string>(nullable: true),
                    RefundAddress = table.Column<string>(nullable: true),
                    RefundAttempted = table.Column<bool>(nullable: false),
                    RefundTxId = table.Column<string>(nullable: true),
                    ClosedReason = table.Column<string>(nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SatoraChainSwaps", x => x.SwapId);
                });

            // BTCPay keys tracked addresses on (address, payment method), and two swaps sharing an HTLC
            // address could not tell their deposits apart, so a reused one is refused at creation.
            migrationBuilder.CreateIndex(
                name: "IX_SatoraChainSwaps_HtlcAddress",
                schema: Schema,
                table: "SatoraChainSwaps",
                column: "HtlcAddress",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SatoraChainSwaps_Open",
                schema: Schema,
                table: "SatoraChainSwaps",
                column: "StoreId",
                filter: "\"CompletedAt\" IS NULL");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SatoraChainSwaps",
                schema: Schema);
        }
    }
}
