using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace BTCPayServer.Plugins.Satora.Migrations
{
    [DbContext(typeof(SatoraPluginDbContext))]
    [Migration("20261003_LightningSwaps")]
    public class LightningSwaps : Migration
    {
        private const string Schema = "BTCPayServer.Plugins.Satora";

        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SatoraLightningSwaps",
                schema: Schema,
                columns: table => new
                {
                    SwapId = table.Column<string>(nullable: false),
                    StoreId = table.Column<string>(nullable: false),
                    Direction = table.Column<string>(nullable: false),
                    Status = table.Column<string>(nullable: false),
                    PaymentHash = table.Column<string>(nullable: false),
                    Bolt11 = table.Column<string>(nullable: false),
                    AmountMsat = table.Column<long>(nullable: false),
                    ReceiveSats = table.Column<long>(nullable: true),
                    ClaimCutoff = table.Column<DateTimeOffset>(nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(nullable: false),
                    DoNotClaim = table.Column<bool>(nullable: false),
                    Credited = table.Column<bool>(nullable: false),
                    ClaimTxId = table.Column<string>(nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(nullable: false),
                    PaidAt = table.Column<DateTimeOffset>(nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SatoraLightningSwaps", x => x.SwapId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SatoraLightningSwaps_StoreId_Direction_PaymentHash",
                schema: Schema,
                table: "SatoraLightningSwaps",
                columns: new[] { "StoreId", "Direction", "PaymentHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SatoraLightningSwaps_Open",
                schema: Schema,
                table: "SatoraLightningSwaps",
                columns: new[] { "StoreId", "Direction" },
                filter: "\"CompletedAt\" IS NULL");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SatoraLightningSwaps",
                schema: Schema);
        }
    }
}
