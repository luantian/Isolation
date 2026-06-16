using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IsolationLeakage.App.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRealtimeCurveData : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_TestObjectPathNodes_Units_UnitCode",
                table: "TestObjectPathNodes");

            migrationBuilder.CreateTable(
                name: "RealtimeCurveData",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SessionCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ProjectCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    UnitCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    ObjectCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    RecordCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    PressureCurveJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    FlowCurveJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TempCurveJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PressureMin = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    PressureMax = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    FlowMin = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    FlowMax = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    TempMin = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    TempMax = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    SampleIntervalMs = table.Column<int>(type: "int", nullable: false),
                    PointCount = table.Column<int>(type: "int", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EndedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Operator = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RealtimeCurveData", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RealtimeCurveData_SessionCode",
                table: "RealtimeCurveData",
                column: "SessionCode",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_TestObjectPathNodes_Units_UnitCode",
                table: "TestObjectPathNodes",
                column: "UnitCode",
                principalTable: "Units",
                principalColumn: "Code",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_TestObjectPathNodes_Units_UnitCode",
                table: "TestObjectPathNodes");

            migrationBuilder.DropTable(
                name: "RealtimeCurveData");

            migrationBuilder.AddForeignKey(
                name: "FK_TestObjectPathNodes_Units_UnitCode",
                table: "TestObjectPathNodes",
                column: "UnitCode",
                principalTable: "Units",
                principalColumn: "Code",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
