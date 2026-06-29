using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IsolationLeakage.App.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddProcessData5Channels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Flow2CurveJson",
                table: "TestProcessData",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "Flow2Max",
                table: "TestProcessData",
                type: "decimal(18,6)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "Flow2Min",
                table: "TestProcessData",
                type: "decimal(18,6)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "Pressure2CurveJson",
                table: "TestProcessData",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "Pressure2Max",
                table: "TestProcessData",
                type: "decimal(18,6)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "Pressure2Min",
                table: "TestProcessData",
                type: "decimal(18,6)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "TimeAxisJson",
                table: "TestProcessData",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Flow2CurveJson",
                table: "TestProcessData");

            migrationBuilder.DropColumn(
                name: "Flow2Max",
                table: "TestProcessData");

            migrationBuilder.DropColumn(
                name: "Flow2Min",
                table: "TestProcessData");

            migrationBuilder.DropColumn(
                name: "Pressure2CurveJson",
                table: "TestProcessData");

            migrationBuilder.DropColumn(
                name: "Pressure2Max",
                table: "TestProcessData");

            migrationBuilder.DropColumn(
                name: "Pressure2Min",
                table: "TestProcessData");

            migrationBuilder.DropColumn(
                name: "TimeAxisJson",
                table: "TestProcessData");
        }
    }
}
