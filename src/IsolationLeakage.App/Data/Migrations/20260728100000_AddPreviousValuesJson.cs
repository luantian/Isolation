using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IsolationLeakage.App.Migrations
{
    /// <inheritdoc />
    public partial class AddPreviousValuesJson : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PreviousValuesJson",
                table: "TestRecords",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PreviousValuesJson",
                table: "TestRecords");
        }
    }
}
