using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IsolationLeakage.App.Migrations
{
    /// <inheritdoc />
    public partial class AddMonitorVariableConfig : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MonitorVariableConfig",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    VariableName = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    RegisterAddress = table.Column<int>(type: "int", nullable: false),
                    SiemensAddress = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    DataType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Unit = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CurveChannel = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    MinDisplay = table.Column<double>(type: "float", nullable: false),
                    MaxDisplay = table.Column<double>(type: "float", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    Remark = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MonitorVariableConfig", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MonitorVariableConfig_SortOrder",
                table: "MonitorVariableConfig",
                column: "SortOrder");

            migrationBuilder.CreateIndex(
                name: "IX_MonitorVariableConfig_VariableName",
                table: "MonitorVariableConfig",
                column: "VariableName",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MonitorVariableConfig");
        }
    }
}
