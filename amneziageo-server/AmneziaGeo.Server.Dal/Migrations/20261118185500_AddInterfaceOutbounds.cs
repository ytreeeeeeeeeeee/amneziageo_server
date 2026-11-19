using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AmneziaGeo.Server.Dal.Migrations
{
    /// <inheritdoc />
    public partial class AddInterfaceOutbounds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "InterfaceOutbounds",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false),
                    InterfaceName = table.Column<string>(type: "TEXT", maxLength: 15, nullable: false),
                    Ipv4 = table.Column<bool>(type: "INTEGER", nullable: false),
                    Ipv6 = table.Column<bool>(type: "INTEGER", nullable: false),
                    Gateway4 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Gateway6 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InterfaceOutbounds", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InterfaceOutbounds_Outbounds_Id",
                        column: x => x.Id,
                        principalTable: "Outbounds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Older models cannot read interface rows; refuse to discard or reinterpret their routing settings.
            migrationBuilder.Sql("""
                CREATE TEMP TABLE "InterfaceOutboundDowngradeGuard" (
                    "Count" INTEGER CONSTRAINT "RemoveInterfaceOutboundsBeforeDowngrade" CHECK ("Count" = 0)
                );
                INSERT INTO "InterfaceOutboundDowngradeGuard" SELECT COUNT(*) FROM "InterfaceOutbounds";
                DROP TABLE "InterfaceOutboundDowngradeGuard";
                """);
            migrationBuilder.DropTable(
                name: "InterfaceOutbounds");
        }
    }
}
