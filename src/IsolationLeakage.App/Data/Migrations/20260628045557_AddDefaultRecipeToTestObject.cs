using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IsolationLeakage.App.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDefaultRecipeToTestObject : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DefaultRecipeId",
                table: "TestObjectPathNodes",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_TestObjectPathNodes_DefaultRecipeId",
                table: "TestObjectPathNodes",
                column: "DefaultRecipeId");

            migrationBuilder.AddForeignKey(
                name: "FK_TestObjectPathNodes_TestRecipes_DefaultRecipeId",
                table: "TestObjectPathNodes",
                column: "DefaultRecipeId",
                principalTable: "TestRecipes",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_TestObjectPathNodes_TestRecipes_DefaultRecipeId",
                table: "TestObjectPathNodes");

            migrationBuilder.DropIndex(
                name: "IX_TestObjectPathNodes_DefaultRecipeId",
                table: "TestObjectPathNodes");

            migrationBuilder.DropColumn(
                name: "DefaultRecipeId",
                table: "TestObjectPathNodes");
        }
    }
}
