using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace BTCPayServer.Plugins.Satora.Migrations
{
    [DbContext(typeof(SatoraPluginDbContext))]
    [Migration("20261012_SentAlerts")]
    public class SentAlerts : Migration
    {
        private const string Schema = "BTCPayServer.Plugins.Satora";

        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SatoraSentAlerts",
                schema: Schema,
                columns: table => new
                {
                    StoreId = table.Column<string>(nullable: false),
                    SwapId = table.Column<string>(nullable: false),
                    Problem = table.Column<string>(nullable: false),
                    SentAt = table.Column<DateTimeOffset>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SatoraSentAlerts", x => new { x.StoreId, x.SwapId, x.Problem });
                });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SatoraSentAlerts",
                schema: Schema);
        }
    }
}
