using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IsolationLeakage.App.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRecipeSnapshotAndVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RecipeSnapshotJson",
                table: "TestRecords",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RecipeVersionNumber",
                table: "TestRecords",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "RecipeVersions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RecipeId = table.Column<int>(type: "int", nullable: false),
                    VersionNumber = table.Column<int>(type: "int", nullable: false),
                    RecipeCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    RecipeName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    RecipeSnapshotJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ChangeDescription = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    IsCurrentVersion = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecipeVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RecipeVersions_TestRecipes_RecipeId",
                        column: x => x.RecipeId,
                        principalTable: "TestRecipes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RecipeVersions_IsCurrentVersion",
                table: "RecipeVersions",
                column: "IsCurrentVersion");

            migrationBuilder.CreateIndex(
                name: "IX_RecipeVersions_RecipeId_VersionNumber",
                table: "RecipeVersions",
                columns: new[] { "RecipeId", "VersionNumber" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RecipeVersions");

            migrationBuilder.DropColumn(
                name: "RecipeSnapshotJson",
                table: "TestRecords");

            migrationBuilder.DropColumn(
                name: "RecipeVersionNumber",
                table: "TestRecords");
        }
    }
}
