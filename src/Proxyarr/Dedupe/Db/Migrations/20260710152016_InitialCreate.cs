using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Proxyarr.Dedupe.Db.Migrations
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
                    Id = table
                        .Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GroupKey = table.Column<string>(type: "TEXT", nullable: false),
                    ContentKey = table.Column<string>(type: "TEXT", nullable: false),
                    NzoId = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Jobs", x => x.Id);
                }
            );

            migrationBuilder.CreateTable(
                name: "Claims",
                columns: table => new
                {
                    JobId = table.Column<int>(type: "INTEGER", nullable: false),
                    Instance = table.Column<string>(type: "TEXT", nullable: false),
                    Category = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Claims", x => new { x.JobId, x.Instance });
                    table.ForeignKey(
                        name: "FK_Claims_Jobs_JobId",
                        column: x => x.JobId,
                        principalTable: "Jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_GroupKey_ContentKey",
                table: "Jobs",
                columns: new[] { "GroupKey", "ContentKey" },
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_GroupKey_NzoId",
                table: "Jobs",
                columns: new[] { "GroupKey", "NzoId" },
                unique: true
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "Claims");

            migrationBuilder.DropTable(name: "Jobs");
        }
    }
}
