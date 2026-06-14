using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IsolationLeakage.App.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLoginLogs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LoginLogs",
                columns: table => new
                {
                    LogId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserName = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    IsSuccess = table.Column<bool>(type: "bit", nullable: false),
                    FailReason = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ClientIp = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    LoginTime = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UserAgent = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LoginLogs", x => x.LogId);
                });

            migrationBuilder.CreateTable(
                name: "TaskDownloadRecords",
                columns: table => new
                {
                    TaskId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    DeviceCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ObjectCodes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    ObjectCount = table.Column<int>(type: "int", nullable: false),
                    PayloadJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    SentAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    TotalObjects = table.Column<int>(type: "int", nullable: true),
                    SentCount = table.Column<int>(type: "int", nullable: true),
                    FailedCount = table.Column<int>(type: "int", nullable: true),
                    FailedObjects = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Detail = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    DeviceCode1 = table.Column<string>(type: "nvarchar(50)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskDownloadRecords", x => x.TaskId);
                    table.ForeignKey(
                        name: "FK_TaskDownloadRecords_MeasurementDevices_DeviceCode1",
                        column: x => x.DeviceCode1,
                        principalTable: "MeasurementDevices",
                        principalColumn: "DeviceCode");
                });

            migrationBuilder.CreateIndex(
                name: "IX_TaskDownloadRecords_DeviceCode1",
                table: "TaskDownloadRecords",
                column: "DeviceCode1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LoginLogs");

            migrationBuilder.DropTable(
                name: "TaskDownloadRecords");
        }
    }
}
