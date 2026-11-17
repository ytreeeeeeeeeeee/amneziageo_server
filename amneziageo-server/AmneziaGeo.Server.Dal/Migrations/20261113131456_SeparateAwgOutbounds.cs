using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AmneziaGeo.Server.Dal.Migrations
{
    /// <inheritdoc />
    public partial class SeparateAwgOutbounds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AwgOutbounds",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false),
                    Host = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    Port = table.Column<int>(type: "INTEGER", nullable: false),
                    Proxy = table.Column<string>(type: "TEXT", nullable: false),
                    PrivateKey = table.Column<string>(type: "TEXT", nullable: false),
                    PublicKey = table.Column<string>(type: "TEXT", nullable: false),
                    PeerKey = table.Column<string>(type: "TEXT", nullable: false),
                    PresharedKey = table.Column<string>(type: "TEXT", nullable: false),
                    Address = table.Column<string>(type: "TEXT", nullable: false),
                    Mtu = table.Column<int>(type: "INTEGER", nullable: false),
                    Keepalive = table.Column<int>(type: "INTEGER", nullable: false),
                    Jc = table.Column<int>(type: "INTEGER", nullable: false),
                    Jmin = table.Column<int>(type: "INTEGER", nullable: false),
                    Jmax = table.Column<int>(type: "INTEGER", nullable: false),
                    S1 = table.Column<int>(type: "INTEGER", nullable: false),
                    S2 = table.Column<int>(type: "INTEGER", nullable: false),
                    S3 = table.Column<int>(type: "INTEGER", nullable: false),
                    S4 = table.Column<int>(type: "INTEGER", nullable: false),
                    H1 = table.Column<string>(type: "TEXT", nullable: false),
                    H2 = table.Column<string>(type: "TEXT", nullable: false),
                    H3 = table.Column<string>(type: "TEXT", nullable: false),
                    H4 = table.Column<string>(type: "TEXT", nullable: false),
                    I1 = table.Column<string>(type: "TEXT", nullable: true),
                    I2 = table.Column<string>(type: "TEXT", nullable: true),
                    I3 = table.Column<string>(type: "TEXT", nullable: true),
                    I4 = table.Column<string>(type: "TEXT", nullable: true),
                    I5 = table.Column<string>(type: "TEXT", nullable: true),
                    HeaderProtectionKey = table.Column<string>(type: "TEXT", nullable: false),
                    ContentPaddingAddition = table.Column<string>(type: "TEXT", nullable: false),
                    RekeyAfterTime = table.Column<string>(type: "TEXT", nullable: false),
                    RekeyTimeout = table.Column<string>(type: "TEXT", nullable: false),
                    RejectAfterTime = table.Column<string>(type: "TEXT", nullable: false),
                    KeepaliveTimeout = table.Column<string>(type: "TEXT", nullable: false),
                    MaxHandshakeAttempts = table.Column<string>(type: "TEXT", nullable: false),
                    RandomTrailers = table.Column<bool>(type: "INTEGER", nullable: false),
                    DisableCookies = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AwgOutbounds", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AwgOutbounds_Outbounds_Id",
                        column: x => x.Id,
                        principalTable: "Outbounds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            // Copies protocol settings before SQLite rebuilds the common table without these columns.
            migrationBuilder.Sql("""
                INSERT INTO "AwgOutbounds" ("Id", "Address", "ContentPaddingAddition", "DisableCookies", "H1", "H2", "H3", "H4", "HeaderProtectionKey", "Host", "I1", "I2", "I3", "I4", "I5", "Jc", "Jmax", "Jmin", "Keepalive", "KeepaliveTimeout", "MaxHandshakeAttempts", "Mtu", "PeerKey", "Port", "PresharedKey", "PrivateKey", "Proxy", "PublicKey", "RandomTrailers", "RejectAfterTime", "RekeyAfterTime", "RekeyTimeout", "S1", "S2", "S3", "S4")
                SELECT "Id", "Address", "ContentPaddingAddition", "DisableCookies", "H1", "H2", "H3", "H4", "HeaderProtectionKey", "Host", "I1", "I2", "I3", "I4", "I5", "Jc", "Jmax", "Jmin", "Keepalive", "KeepaliveTimeout", "MaxHandshakeAttempts", "Mtu", "PeerKey", "Port", "PresharedKey", "PrivateKey", "Proxy", "PublicKey", "RandomTrailers", "RejectAfterTime", "RekeyAfterTime", "RekeyTimeout", "S1", "S2", "S3", "S4" FROM "Outbounds" WHERE "Kind" IN ('wg', 'ws');
                """);

            migrationBuilder.DropColumn(
                name: "Address",
                table: "Outbounds");

            migrationBuilder.DropColumn(
                name: "ContentPaddingAddition",
                table: "Outbounds");

            migrationBuilder.DropColumn(
                name: "DisableCookies",
                table: "Outbounds");

            migrationBuilder.DropColumn(
                name: "H1",
                table: "Outbounds");

            migrationBuilder.DropColumn(
                name: "H2",
                table: "Outbounds");

            migrationBuilder.DropColumn(
                name: "H3",
                table: "Outbounds");

            migrationBuilder.DropColumn(
                name: "H4",
                table: "Outbounds");

            migrationBuilder.DropColumn(
                name: "HeaderProtectionKey",
                table: "Outbounds");

            migrationBuilder.DropColumn(
                name: "Host",
                table: "Outbounds");

            migrationBuilder.DropColumn(
                name: "I1",
                table: "Outbounds");

            migrationBuilder.DropColumn(
                name: "I2",
                table: "Outbounds");

            migrationBuilder.DropColumn(
                name: "I3",
                table: "Outbounds");

            migrationBuilder.DropColumn(
                name: "I4",
                table: "Outbounds");

            migrationBuilder.DropColumn(
                name: "I5",
                table: "Outbounds");

            migrationBuilder.DropColumn(
                name: "Jc",
                table: "Outbounds");

            migrationBuilder.DropColumn(
                name: "Jmax",
                table: "Outbounds");

            migrationBuilder.DropColumn(
                name: "Jmin",
                table: "Outbounds");

            migrationBuilder.DropColumn(
                name: "Keepalive",
                table: "Outbounds");

            migrationBuilder.DropColumn(
                name: "KeepaliveTimeout",
                table: "Outbounds");

            migrationBuilder.DropColumn(
                name: "MaxHandshakeAttempts",
                table: "Outbounds");

            migrationBuilder.DropColumn(
                name: "Mtu",
                table: "Outbounds");

            migrationBuilder.DropColumn(
                name: "PeerKey",
                table: "Outbounds");

            migrationBuilder.DropColumn(
                name: "Port",
                table: "Outbounds");

            migrationBuilder.DropColumn(
                name: "PresharedKey",
                table: "Outbounds");

            migrationBuilder.DropColumn(
                name: "PrivateKey",
                table: "Outbounds");

            migrationBuilder.DropColumn(
                name: "Proxy",
                table: "Outbounds");

            migrationBuilder.DropColumn(
                name: "PublicKey",
                table: "Outbounds");

            migrationBuilder.DropColumn(
                name: "RandomTrailers",
                table: "Outbounds");

            migrationBuilder.DropColumn(
                name: "RejectAfterTime",
                table: "Outbounds");

            migrationBuilder.DropColumn(
                name: "RekeyAfterTime",
                table: "Outbounds");

            migrationBuilder.DropColumn(
                name: "RekeyTimeout",
                table: "Outbounds");

            migrationBuilder.DropColumn(
                name: "S1",
                table: "Outbounds");

            migrationBuilder.DropColumn(
                name: "S2",
                table: "Outbounds");

            migrationBuilder.DropColumn(
                name: "S3",
                table: "Outbounds");

            migrationBuilder.DropColumn(
                name: "S4",
                table: "Outbounds");


        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {



            migrationBuilder.AddColumn<string>(
                name: "Address",
                table: "Outbounds",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ContentPaddingAddition",
                table: "Outbounds",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "DisableCookies",
                table: "Outbounds",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "H1",
                table: "Outbounds",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "H2",
                table: "Outbounds",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "H3",
                table: "Outbounds",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "H4",
                table: "Outbounds",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "HeaderProtectionKey",
                table: "Outbounds",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Host",
                table: "Outbounds",
                type: "TEXT",
                maxLength: 255,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "I1",
                table: "Outbounds",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "I2",
                table: "Outbounds",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "I3",
                table: "Outbounds",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "I4",
                table: "Outbounds",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "I5",
                table: "Outbounds",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Jc",
                table: "Outbounds",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Jmax",
                table: "Outbounds",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Jmin",
                table: "Outbounds",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Keepalive",
                table: "Outbounds",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "KeepaliveTimeout",
                table: "Outbounds",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "MaxHandshakeAttempts",
                table: "Outbounds",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "Mtu",
                table: "Outbounds",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "PeerKey",
                table: "Outbounds",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "Port",
                table: "Outbounds",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "PresharedKey",
                table: "Outbounds",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "PrivateKey",
                table: "Outbounds",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Proxy",
                table: "Outbounds",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "PublicKey",
                table: "Outbounds",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "RandomTrailers",
                table: "Outbounds",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "RejectAfterTime",
                table: "Outbounds",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "RekeyAfterTime",
                table: "Outbounds",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "RekeyTimeout",
                table: "Outbounds",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "S1",
                table: "Outbounds",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "S2",
                table: "Outbounds",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "S3",
                table: "Outbounds",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "S4",
                table: "Outbounds",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
            migrationBuilder.Sql("""
                UPDATE "Outbounds" SET
                    "Address" = (SELECT "Address" FROM "AwgOutbounds" WHERE "AwgOutbounds"."Id" = "Outbounds"."Id"),
                    "ContentPaddingAddition" = (SELECT "ContentPaddingAddition" FROM "AwgOutbounds" WHERE "AwgOutbounds"."Id" = "Outbounds"."Id"),
                    "DisableCookies" = (SELECT "DisableCookies" FROM "AwgOutbounds" WHERE "AwgOutbounds"."Id" = "Outbounds"."Id"),
                    "H1" = (SELECT "H1" FROM "AwgOutbounds" WHERE "AwgOutbounds"."Id" = "Outbounds"."Id"),
                    "H2" = (SELECT "H2" FROM "AwgOutbounds" WHERE "AwgOutbounds"."Id" = "Outbounds"."Id"),
                    "H3" = (SELECT "H3" FROM "AwgOutbounds" WHERE "AwgOutbounds"."Id" = "Outbounds"."Id"),
                    "H4" = (SELECT "H4" FROM "AwgOutbounds" WHERE "AwgOutbounds"."Id" = "Outbounds"."Id"),
                    "HeaderProtectionKey" = (SELECT "HeaderProtectionKey" FROM "AwgOutbounds" WHERE "AwgOutbounds"."Id" = "Outbounds"."Id"),
                    "Host" = (SELECT "Host" FROM "AwgOutbounds" WHERE "AwgOutbounds"."Id" = "Outbounds"."Id"),
                    "I1" = (SELECT "I1" FROM "AwgOutbounds" WHERE "AwgOutbounds"."Id" = "Outbounds"."Id"),
                    "I2" = (SELECT "I2" FROM "AwgOutbounds" WHERE "AwgOutbounds"."Id" = "Outbounds"."Id"),
                    "I3" = (SELECT "I3" FROM "AwgOutbounds" WHERE "AwgOutbounds"."Id" = "Outbounds"."Id"),
                    "I4" = (SELECT "I4" FROM "AwgOutbounds" WHERE "AwgOutbounds"."Id" = "Outbounds"."Id"),
                    "I5" = (SELECT "I5" FROM "AwgOutbounds" WHERE "AwgOutbounds"."Id" = "Outbounds"."Id"),
                    "Jc" = (SELECT "Jc" FROM "AwgOutbounds" WHERE "AwgOutbounds"."Id" = "Outbounds"."Id"),
                    "Jmax" = (SELECT "Jmax" FROM "AwgOutbounds" WHERE "AwgOutbounds"."Id" = "Outbounds"."Id"),
                    "Jmin" = (SELECT "Jmin" FROM "AwgOutbounds" WHERE "AwgOutbounds"."Id" = "Outbounds"."Id"),
                    "Keepalive" = (SELECT "Keepalive" FROM "AwgOutbounds" WHERE "AwgOutbounds"."Id" = "Outbounds"."Id"),
                    "KeepaliveTimeout" = (SELECT "KeepaliveTimeout" FROM "AwgOutbounds" WHERE "AwgOutbounds"."Id" = "Outbounds"."Id"),
                    "MaxHandshakeAttempts" = (SELECT "MaxHandshakeAttempts" FROM "AwgOutbounds" WHERE "AwgOutbounds"."Id" = "Outbounds"."Id"),
                    "Mtu" = (SELECT "Mtu" FROM "AwgOutbounds" WHERE "AwgOutbounds"."Id" = "Outbounds"."Id"),
                    "PeerKey" = (SELECT "PeerKey" FROM "AwgOutbounds" WHERE "AwgOutbounds"."Id" = "Outbounds"."Id"),
                    "Port" = (SELECT "Port" FROM "AwgOutbounds" WHERE "AwgOutbounds"."Id" = "Outbounds"."Id"),
                    "PresharedKey" = (SELECT "PresharedKey" FROM "AwgOutbounds" WHERE "AwgOutbounds"."Id" = "Outbounds"."Id"),
                    "PrivateKey" = (SELECT "PrivateKey" FROM "AwgOutbounds" WHERE "AwgOutbounds"."Id" = "Outbounds"."Id"),
                    "Proxy" = (SELECT "Proxy" FROM "AwgOutbounds" WHERE "AwgOutbounds"."Id" = "Outbounds"."Id"),
                    "PublicKey" = (SELECT "PublicKey" FROM "AwgOutbounds" WHERE "AwgOutbounds"."Id" = "Outbounds"."Id"),
                    "RandomTrailers" = (SELECT "RandomTrailers" FROM "AwgOutbounds" WHERE "AwgOutbounds"."Id" = "Outbounds"."Id"),
                    "RejectAfterTime" = (SELECT "RejectAfterTime" FROM "AwgOutbounds" WHERE "AwgOutbounds"."Id" = "Outbounds"."Id"),
                    "RekeyAfterTime" = (SELECT "RekeyAfterTime" FROM "AwgOutbounds" WHERE "AwgOutbounds"."Id" = "Outbounds"."Id"),
                    "RekeyTimeout" = (SELECT "RekeyTimeout" FROM "AwgOutbounds" WHERE "AwgOutbounds"."Id" = "Outbounds"."Id"),
                    "S1" = (SELECT "S1" FROM "AwgOutbounds" WHERE "AwgOutbounds"."Id" = "Outbounds"."Id"),
                    "S2" = (SELECT "S2" FROM "AwgOutbounds" WHERE "AwgOutbounds"."Id" = "Outbounds"."Id"),
                    "S3" = (SELECT "S3" FROM "AwgOutbounds" WHERE "AwgOutbounds"."Id" = "Outbounds"."Id"),
                    "S4" = (SELECT "S4" FROM "AwgOutbounds" WHERE "AwgOutbounds"."Id" = "Outbounds"."Id")
                WHERE "Id" IN (SELECT "Id" FROM "AwgOutbounds");
                """);
            migrationBuilder.DropTable(
                name: "AwgOutbounds");
        }
    }
}
