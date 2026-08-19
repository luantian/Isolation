using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IsolationLeakage.App.Migrations
{
    /// <inheritdoc />
    public partial class AddDeviceValidUntil : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ValidUntil",
                table: "MeasurementDevices",
                type: "datetime2",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ValidUntil",
                table: "MeasurementDevices");
        }
    }
}
