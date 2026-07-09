using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IsolationLeakage.App.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
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
                name: "MeasurementDevices",
                columns: table => new
                {
                    DeviceCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    DeviceName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Ip = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    SerialNumber = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    PrimaryCommunication = table.Column<int>(type: "int", nullable: false),
                    EnabledStatus = table.Column<int>(type: "int", nullable: false),
                    ConnectionStatus = table.Column<int>(type: "int", nullable: false),
                    LastSyncTime = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastUploadTime = table.Column<DateTime>(type: "datetime2", nullable: true),
                    UploadCount = table.Column<int>(type: "int", nullable: false),
                    LastUploadResult = table.Column<int>(type: "int", nullable: true),
                    Remark = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MeasurementDevices", x => x.DeviceCode);
                });

            migrationBuilder.CreateTable(
                name: "Menus",
                columns: table => new
                {
                    MenuId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    MenuName = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ParentId = table.Column<int>(type: "int", nullable: false),
                    Sort = table.Column<int>(type: "int", nullable: false),
                    Path = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Component = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Type = table.Column<int>(type: "int", nullable: false),
                    Visible = table.Column<bool>(type: "bit", nullable: false),
                    Perms = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Icon = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Remark = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Menus", x => x.MenuId);
                    table.ForeignKey(
                        name: "FK_Menus_Menus_ParentId",
                        column: x => x.ParentId,
                        principalTable: "Menus",
                        principalColumn: "MenuId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OperationLogs",
                columns: table => new
                {
                    LogId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OperationType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    UserName = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Details = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Result = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    IpAddress = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    OperationTime = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OperationLogs", x => x.LogId);
                });

            migrationBuilder.CreateTable(
                name: "Projects",
                columns: table => new
                {
                    Code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Remark = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Projects", x => x.Code);
                });

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

            migrationBuilder.CreateTable(
                name: "Roles",
                columns: table => new
                {
                    RoleId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RoleName = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    RoleKey = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Sort = table.Column<int>(type: "int", nullable: false),
                    DataScope = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Remark = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Roles", x => x.RoleId);
                });

            migrationBuilder.CreateTable(
                name: "TestRecipes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RecipeName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    SequenceNo = table.Column<int>(type: "int", nullable: false),
                    System = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    PenetrationDiameter = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    ValveNo = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ValveNominalDiameter = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    LeakageLimit = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    PrechargePressureP2 = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    Remark = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    UpdatedBy = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TestRecipes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    UserId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserName = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    NickName = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    PasswordHash = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Avatar = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Email = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Phone = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    Dept = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    LastLoginTime = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LoginCount = table.Column<int>(type: "int", nullable: false),
                    FailedLoginAttempts = table.Column<int>(type: "int", nullable: false),
                    LockoutEnd = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Remark = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.UserId);
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

            migrationBuilder.CreateTable(
                name: "Units",
                columns: table => new
                {
                    Code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ProjectCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Remark = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Units", x => x.Code);
                    table.ForeignKey(
                        name: "FK_Units_Projects_ProjectCode",
                        column: x => x.ProjectCode,
                        principalTable: "Projects",
                        principalColumn: "Code",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RoleMenus",
                columns: table => new
                {
                    RoleId = table.Column<int>(type: "int", nullable: false),
                    MenuId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoleMenus", x => new { x.RoleId, x.MenuId });
                    table.ForeignKey(
                        name: "FK_RoleMenus_Menus_MenuId",
                        column: x => x.MenuId,
                        principalTable: "Menus",
                        principalColumn: "MenuId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RoleMenus_Roles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "Roles",
                        principalColumn: "RoleId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RecipeVersions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RecipeId = table.Column<int>(type: "int", nullable: false),
                    VersionNumber = table.Column<int>(type: "int", nullable: false),
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

            migrationBuilder.CreateTable(
                name: "UserRoles",
                columns: table => new
                {
                    UserId = table.Column<int>(type: "int", nullable: false),
                    RoleId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserRoles", x => new { x.UserId, x.RoleId });
                    table.ForeignKey(
                        name: "FK_UserRoles_Roles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "Roles",
                        principalColumn: "RoleId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UserRoles_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "UserId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TestObjectPathNodes",
                columns: table => new
                {
                    Code = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    NodeType = table.Column<int>(type: "int", nullable: false),
                    UnitCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ParentCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ValveType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ComponentType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    LeakageLimit = table.Column<decimal>(type: "decimal(18,6)", nullable: true),
                    TestPressure = table.Column<decimal>(type: "decimal(18,6)", nullable: true),
                    DefaultRecipeId = table.Column<int>(type: "int", nullable: true),
                    Remark = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TestObjectPathNodes", x => x.Code);
                    table.ForeignKey(
                        name: "FK_TestObjectPathNodes_TestObjectPathNodes_ParentCode",
                        column: x => x.ParentCode,
                        principalTable: "TestObjectPathNodes",
                        principalColumn: "Code",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TestObjectPathNodes_TestRecipes_DefaultRecipeId",
                        column: x => x.DefaultRecipeId,
                        principalTable: "TestRecipes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_TestObjectPathNodes_Units_UnitCode",
                        column: x => x.UnitCode,
                        principalTable: "Units",
                        principalColumn: "Code",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TestRecords",
                columns: table => new
                {
                    RecordCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ProjectCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    UnitCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ObjectCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ObjectName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ObjectType = table.Column<int>(type: "int", nullable: false),
                    DeviceCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    TestRecipeId = table.Column<int>(type: "int", nullable: true),
                    RecipeSnapshotJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RecipeVersionNumber = table.Column<int>(type: "int", nullable: true),
                    DataPackageName = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    TestTime = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ImportTime = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Operator = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    TestPressure = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    LeakageLimit = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    FinalLeakageRate = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    Result = table.Column<int>(type: "int", nullable: false),
                    Remark = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    StepSummary = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ResultFieldSummary = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ProcessChannelSummary = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TestRecords", x => x.RecordCode);
                    table.ForeignKey(
                        name: "FK_TestRecords_MeasurementDevices_DeviceCode",
                        column: x => x.DeviceCode,
                        principalTable: "MeasurementDevices",
                        principalColumn: "DeviceCode",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TestRecords_Projects_ProjectCode",
                        column: x => x.ProjectCode,
                        principalTable: "Projects",
                        principalColumn: "Code",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TestRecords_TestObjectPathNodes_ObjectCode",
                        column: x => x.ObjectCode,
                        principalTable: "TestObjectPathNodes",
                        principalColumn: "Code",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TestRecords_TestRecipes_TestRecipeId",
                        column: x => x.TestRecipeId,
                        principalTable: "TestRecipes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_TestRecords_Units_UnitCode",
                        column: x => x.UnitCode,
                        principalTable: "Units",
                        principalColumn: "Code",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TestProcessData",
                columns: table => new
                {
                    RecordCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    PressureCurveJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    FlowCurveJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Flow2CurveJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TempCurveJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Pressure2CurveJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ChannelsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TimeAxisJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PressureMin = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    PressureMax = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    FlowMin = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    FlowMax = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    Flow2Min = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    Flow2Max = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    TempMin = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    TempMax = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    Pressure2Min = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    Pressure2Max = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TestProcessData", x => x.RecordCode);
                    table.ForeignKey(
                        name: "FK_TestProcessData_TestRecords_RecordCode",
                        column: x => x.RecordCode,
                        principalTable: "TestRecords",
                        principalColumn: "RecordCode",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MeasurementDevices_DeviceCode",
                table: "MeasurementDevices",
                column: "DeviceCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Menus_ParentId",
                table: "Menus",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_Projects_Name",
                table: "Projects",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RealtimeCurveData_SessionCode",
                table: "RealtimeCurveData",
                column: "SessionCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RecipeVersions_IsCurrentVersion",
                table: "RecipeVersions",
                column: "IsCurrentVersion");

            migrationBuilder.CreateIndex(
                name: "IX_RecipeVersions_RecipeId_VersionNumber",
                table: "RecipeVersions",
                columns: new[] { "RecipeId", "VersionNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RoleMenus_MenuId",
                table: "RoleMenus",
                column: "MenuId");

            migrationBuilder.CreateIndex(
                name: "IX_Roles_RoleKey",
                table: "Roles",
                column: "RoleKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaskDownloadRecords_DeviceCode1",
                table: "TaskDownloadRecords",
                column: "DeviceCode1");

            migrationBuilder.CreateIndex(
                name: "IX_TestObjectPathNodes_Code",
                table: "TestObjectPathNodes",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TestObjectPathNodes_DefaultRecipeId",
                table: "TestObjectPathNodes",
                column: "DefaultRecipeId");

            migrationBuilder.CreateIndex(
                name: "IX_TestObjectPathNodes_ParentCode",
                table: "TestObjectPathNodes",
                column: "ParentCode");

            migrationBuilder.CreateIndex(
                name: "IX_TestObjectPathNodes_UnitCode",
                table: "TestObjectPathNodes",
                column: "UnitCode");

            migrationBuilder.CreateIndex(
                name: "IX_TestRecipes_RecipeName",
                table: "TestRecipes",
                column: "RecipeName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TestRecords_DeviceCode",
                table: "TestRecords",
                column: "DeviceCode");

            migrationBuilder.CreateIndex(
                name: "IX_TestRecords_ObjectCode",
                table: "TestRecords",
                column: "ObjectCode");

            migrationBuilder.CreateIndex(
                name: "IX_TestRecords_ProjectCode_UnitCode_ObjectCode_TestTime",
                table: "TestRecords",
                columns: new[] { "ProjectCode", "UnitCode", "ObjectCode", "TestTime" });

            migrationBuilder.CreateIndex(
                name: "IX_TestRecords_TestRecipeId",
                table: "TestRecords",
                column: "TestRecipeId");

            migrationBuilder.CreateIndex(
                name: "IX_TestRecords_TestTime",
                table: "TestRecords",
                column: "TestTime");

            migrationBuilder.CreateIndex(
                name: "IX_TestRecords_UnitCode",
                table: "TestRecords",
                column: "UnitCode");

            migrationBuilder.CreateIndex(
                name: "IX_Units_ProjectCode_Name",
                table: "Units",
                columns: new[] { "ProjectCode", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserRoles_RoleId",
                table: "UserRoles",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_Users_UserName",
                table: "Users",
                column: "UserName",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LoginLogs");

            migrationBuilder.DropTable(
                name: "OperationLogs");

            migrationBuilder.DropTable(
                name: "RealtimeCurveData");

            migrationBuilder.DropTable(
                name: "RecipeVersions");

            migrationBuilder.DropTable(
                name: "RoleMenus");

            migrationBuilder.DropTable(
                name: "TaskDownloadRecords");

            migrationBuilder.DropTable(
                name: "TestProcessData");

            migrationBuilder.DropTable(
                name: "UserRoles");

            migrationBuilder.DropTable(
                name: "Menus");

            migrationBuilder.DropTable(
                name: "TestRecords");

            migrationBuilder.DropTable(
                name: "Roles");

            migrationBuilder.DropTable(
                name: "Users");

            migrationBuilder.DropTable(
                name: "MeasurementDevices");

            migrationBuilder.DropTable(
                name: "TestObjectPathNodes");

            migrationBuilder.DropTable(
                name: "TestRecipes");

            migrationBuilder.DropTable(
                name: "Units");

            migrationBuilder.DropTable(
                name: "Projects");
        }
    }
}
