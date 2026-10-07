using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AnonymousBot.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAnonymousMessageAttempts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AnonymousMessageAttempts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TelegramUserId = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AnonymousMessageAttempts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AnonymousMessageAttempts_TelegramUserId_CreatedAt",
                table: "AnonymousMessageAttempts",
                columns: new[] { "TelegramUserId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AnonymousMessageAttempts");
        }
    }
}
