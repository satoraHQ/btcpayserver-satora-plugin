using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace BTCPayServer.Plugins.Satora.Migrations
{
    [DbContext(typeof(SatoraPluginDbContext))]
    [Migration("20261002_WalletSeed")]
    public class WalletSeed : Migration
    {
        private const string Schema = "BTCPayServer.Plugins.Satora";

        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ProtectedSeed",
                table: "SatoraSettings",
                schema: Schema,
                nullable: true);
            migrationBuilder.AddColumn<string>(
                name: "SeedFingerprint",
                table: "SatoraSettings",
                schema: Schema,
                nullable: true);
            migrationBuilder.AddColumn<string>(
                name: "LightningConnectionKey",
                table: "SatoraSettings",
                schema: Schema,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SatoraRetiredSeeds",
                schema: Schema,
                columns: table => new
                {
                    Id = table.Column<string>(nullable: false),
                    StoreId = table.Column<string>(nullable: false),
                    Format = table.Column<string>(nullable: false),
                    Ciphertext = table.Column<string>(nullable: false),
                    SeedFingerprint = table.Column<string>(nullable: true),
                    RetiredAt = table.Column<DateTimeOffset>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SatoraRetiredSeeds", x => x.Id);
                });
            migrationBuilder.CreateIndex(
                name: "IX_SatoraRetiredSeeds_StoreId",
                schema: Schema,
                table: "SatoraRetiredSeeds",
                column: "StoreId");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SatoraRetiredSeeds",
                schema: Schema);
            migrationBuilder.DropColumn(
                name: "ProtectedSeed",
                table: "SatoraSettings",
                schema: Schema);
            migrationBuilder.DropColumn(
                name: "SeedFingerprint",
                table: "SatoraSettings",
                schema: Schema);
            migrationBuilder.DropColumn(
                name: "LightningConnectionKey",
                table: "SatoraSettings",
                schema: Schema);
        }
    }
}
