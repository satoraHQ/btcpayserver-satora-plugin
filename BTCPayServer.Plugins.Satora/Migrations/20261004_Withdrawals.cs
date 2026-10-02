using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace BTCPayServer.Plugins.Satora.Migrations
{
    [DbContext(typeof(SatoraPluginDbContext))]
    [Migration("20261004_Withdrawals")]
    public class Withdrawals : Migration
    {
        private const string Schema = "BTCPayServer.Plugins.Satora";

        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "FundingAttempted",
                table: "SatoraLightningSwaps",
                schema: Schema,
                nullable: false,
                defaultValue: false);
            migrationBuilder.AddColumn<long>(
                name: "FundingSats",
                table: "SatoraLightningSwaps",
                schema: Schema,
                nullable: true);
            migrationBuilder.AddColumn<string>(
                name: "FundingTxId",
                table: "SatoraLightningSwaps",
                schema: Schema,
                nullable: true);
            migrationBuilder.AddColumn<string>(
                name: "RefundTxId",
                table: "SatoraLightningSwaps",
                schema: Schema,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SatoraWithdrawals",
                schema: Schema,
                columns: table => new
                {
                    Id = table.Column<string>(nullable: false),
                    StoreId = table.Column<string>(nullable: false),
                    Type = table.Column<string>(nullable: false),
                    Destination = table.Column<string>(nullable: false),
                    AmountSats = table.Column<long>(nullable: true),
                    FeeSats = table.Column<long>(nullable: true),
                    Status = table.Column<string>(nullable: false),
                    TxId = table.Column<string>(nullable: true),
                    SwapId = table.Column<string>(nullable: true),
                    Error = table.Column<string>(nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SatoraWithdrawals", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SatoraWithdrawals_StoreId_CreatedAt",
                schema: Schema,
                table: "SatoraWithdrawals",
                columns: new[] { "StoreId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SatoraWithdrawals_SwapId",
                schema: Schema,
                table: "SatoraWithdrawals",
                column: "SwapId",
                unique: true,
                filter: "\"SwapId\" IS NOT NULL");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SatoraWithdrawals",
                schema: Schema);
            migrationBuilder.DropColumn(
                name: "FundingAttempted",
                table: "SatoraLightningSwaps",
                schema: Schema);
            migrationBuilder.DropColumn(
                name: "FundingSats",
                table: "SatoraLightningSwaps",
                schema: Schema);
            migrationBuilder.DropColumn(
                name: "FundingTxId",
                table: "SatoraLightningSwaps",
                schema: Schema);
            migrationBuilder.DropColumn(
                name: "RefundTxId",
                table: "SatoraLightningSwaps",
                schema: Schema);
        }
    }
}
