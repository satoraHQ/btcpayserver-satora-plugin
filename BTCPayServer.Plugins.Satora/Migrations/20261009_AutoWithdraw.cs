using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace BTCPayServer.Plugins.Satora.Migrations
{
    [DbContext(typeof(SatoraPluginDbContext))]
    [Migration("20261009_AutoWithdraw")]
    public class AutoWithdraw : Migration
    {
        private const string Schema = "BTCPayServer.Plugins.Satora";

        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Origin",
                table: "SatoraWithdrawals",
                schema: Schema,
                nullable: false,
                defaultValue: "Manual");

            migrationBuilder.CreateTable(
                name: "SatoraAutoWithdrawRules",
                schema: Schema,
                columns: table => new
                {
                    StoreId = table.Column<string>(nullable: false),
                    Enabled = table.Column<bool>(nullable: false),
                    DestinationType = table.Column<string>(nullable: false),
                    Destination = table.Column<string>(nullable: false),
                    ThresholdSats = table.Column<long>(nullable: false),
                    ReserveSats = table.Column<long>(nullable: false),
                    MaxFeeBps = table.Column<int>(nullable: false),
                    Version = table.Column<int>(nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SatoraAutoWithdrawRules", x => x.StoreId);
                });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SatoraAutoWithdrawRules",
                schema: Schema);
            migrationBuilder.DropColumn(
                name: "Origin",
                table: "SatoraWithdrawals",
                schema: Schema);
        }
    }
}
