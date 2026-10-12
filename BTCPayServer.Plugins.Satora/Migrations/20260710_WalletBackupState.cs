using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace BTCPayServer.Plugins.Satora.Migrations
{
    [DbContext(typeof(SatoraPluginDbContext))]
    [Migration("20260710_WalletBackupState")]
    public class WalletBackupState : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "BackupConfirmed",
                table: "SatoraSettings",
                schema: "BTCPayServer.Plugins.Satora",
                nullable: false,
                defaultValue: false);
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "WalletCreated",
                table: "SatoraSettings",
                schema: "BTCPayServer.Plugins.Satora",
                nullable: true);
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "BackupConfirmedAt",
                table: "SatoraSettings",
                schema: "BTCPayServer.Plugins.Satora",
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BackupConfirmed",
                table: "SatoraSettings",
                schema: "BTCPayServer.Plugins.Satora");
            migrationBuilder.DropColumn(
                name: "WalletCreated",
                table: "SatoraSettings",
                schema: "BTCPayServer.Plugins.Satora");
            migrationBuilder.DropColumn(
                name: "BackupConfirmedAt",
                table: "SatoraSettings",
                schema: "BTCPayServer.Plugins.Satora");
        }
    }
}
