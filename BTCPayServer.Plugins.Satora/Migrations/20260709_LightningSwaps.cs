using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace BTCPayServer.Plugins.Satora.Migrations
{
    [DbContext(typeof(SatoraPluginDbContext))]
    [Migration("20260709_LightningSwaps")]
    public class LightningSwaps : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SatoraLightningSwaps",
                schema: "BTCPayServer.Plugins.Satora",
                columns: table => new
                {
                    SwapId = table.Column<string>(nullable: false),
                    StoreId = table.Column<string>(nullable: false),
                    Direction = table.Column<string>(nullable: false),
                    Bolt11 = table.Column<string>(nullable: false),
                    PaymentHash = table.Column<string>(nullable: false),
                    AmountSats = table.Column<long>(nullable: false),
                    FundingAmountSats = table.Column<long>(nullable: true),
                    FundingAddress = table.Column<string>(nullable: true),
                    FundingTxId = table.Column<string>(nullable: true),
                    ClaimTxId = table.Column<string>(nullable: true),
                    Status = table.Column<string>(nullable: true),
                    Created = table.Column<DateTimeOffset>(nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(nullable: false),
                    PaidAt = table.Column<DateTimeOffset>(nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SatoraLightningSwaps", x => x.SwapId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SatoraLightningSwaps_StoreId_Direction_Status",
                schema: "BTCPayServer.Plugins.Satora",
                table: "SatoraLightningSwaps",
                columns: new[] { "StoreId", "Direction", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_SatoraLightningSwaps_PaymentHash",
                schema: "BTCPayServer.Plugins.Satora",
                table: "SatoraLightningSwaps",
                column: "PaymentHash");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SatoraLightningSwaps",
                schema: "BTCPayServer.Plugins.Satora");
        }
    }
}
