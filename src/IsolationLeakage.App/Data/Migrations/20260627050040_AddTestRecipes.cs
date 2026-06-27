using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IsolationLeakage.App.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTestRecipes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "TestRecipeId",
                table: "TestRecords",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "TestRecipes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RecipeCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    RecipeName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    AirtightTargetPressureP1 = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    AirtightAllowDropValue = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    FineBlowTargetPressureP1 = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    PurgeReleasePressure = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    NormalExpectedLeakFlow = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    SmallPrechargeTargetP1 = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    SmallPrechargeTargetP2 = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    MediumPrechargeTargetP1 = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    MediumPrechargeTargetP2 = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    LargePrechargeTargetP1 = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    LargePrechargeTargetP2 = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    UpdatedBy = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TestRecipes", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TestRecords_TestRecipeId",
                table: "TestRecords",
                column: "TestRecipeId");

            migrationBuilder.CreateIndex(
                name: "IX_TestRecipes_RecipeCode",
                table: "TestRecipes",
                column: "RecipeCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TestRecipes_RecipeName",
                table: "TestRecipes",
                column: "RecipeName");

            migrationBuilder.AddForeignKey(
                name: "FK_TestRecords_TestRecipes_TestRecipeId",
                table: "TestRecords",
                column: "TestRecipeId",
                principalTable: "TestRecipes",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_TestRecords_TestRecipes_TestRecipeId",
                table: "TestRecords");

            migrationBuilder.DropTable(
                name: "TestRecipes");

            migrationBuilder.DropIndex(
                name: "IX_TestRecords_TestRecipeId",
                table: "TestRecords");

            migrationBuilder.DropColumn(
                name: "TestRecipeId",
                table: "TestRecords");
        }
    }
}
