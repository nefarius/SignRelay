using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SignRelay.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Jobs",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ExpiresUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    JobTokenHash = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ManifestJson = table.Column<string>(type: "TEXT", maxLength: 64000, nullable: false),
                    TotalUnsignedBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    LeaseAgentId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    LeasedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LeaseTokenHash = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    LeaseExpiresUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LeaseAttempts = table.Column<int>(type: "INTEGER", nullable: false),
                    ErrorMessage = table.Column<string>(type: "TEXT", maxLength: 16000, nullable: true),
                    CompletedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Jobs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_CreatedUtc",
                table: "Jobs",
                column: "CreatedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_JobTokenHash",
                table: "Jobs",
                column: "JobTokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_LeaseTokenHash",
                table: "Jobs",
                column: "LeaseTokenHash");

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_Status_ExpiresUtc",
                table: "Jobs",
                columns: new[] { "Status", "ExpiresUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Jobs");
        }
    }
}
