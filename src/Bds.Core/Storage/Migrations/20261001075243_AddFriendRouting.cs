using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bds.Core.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddFriendRouting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FriendRoutes",
                columns: table => new
                {
                    Xuid = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    ServerId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FriendRoutes", x => x.Xuid);
                    table.ForeignKey(
                        name: "FK_FriendRoutes_Servers_ServerId",
                        column: x => x.ServerId,
                        principalTable: "Servers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FriendRoutes_ServerId",
                table: "FriendRoutes",
                column: "ServerId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FriendRoutes");
        }
    }
}
