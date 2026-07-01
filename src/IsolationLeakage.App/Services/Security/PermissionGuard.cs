namespace IsolationLeakage.App.Services.Security;

/// <summary>
/// 权限标识常量
/// </summary>
public static class Perms
{
    // ── 页面级（控制导航栏显隐） ──
    public const string OverviewView = "overview:view";
    public const string MasterDataView = "masterdata:view";
    public const string RecipeView = "recipe:view";
    public const string RecordsView = "records:view";
    public const string RealtimeView = "realtime:view";
    public const string AnalysisView = "analysis:view";
    public const string SystemView = "system:view";

    // ── 基础台账：编辑（新增/修改） ──
    public const string ProjectAdd = "masterdata:project:add";
    public const string PathAdd = "masterdata:path:add";
    public const string DeviceAdd = "masterdata:device:add";

    // ── 基础台账：删除 ──
    public const string ProjectDelete = "masterdata:project:delete";
    public const string PathDelete = "masterdata:path:delete";
    public const string DeviceDelete = "masterdata:device:delete";

    // ── 配方管理 ──
    public const string RecipeEdit = "recipe:edit";
    public const string RecipeDelete = "recipe:delete";

    // ── 实时监视 ──
    public const string RealtimeEdit = "realtime:edit";
    public const string RealtimeDelete = "realtime:delete";

    // ── 试验记录操作 ──
    public const string TaskDownload = "records:task:download";
    public const string RecordsUpload = "records:data:upload";
    public const string RecordsDelete = "records:data:delete";
    public const string ReportExport = "records:report:export";

    // ── 系统管理操作 ──
    public const string UserManage = "system:user:add";
    public const string RoleManage = "system:role:add";
    public const string LogView = "system:log:view";
    public const string BackupView = "system:backup:view";
    public const string MigrateView = "system:migrate:view";
}

/// <summary>
/// 权限守卫（用于 ViewModel 命令的 CanExecute 和方法入口校验）
/// </summary>
public static class PermissionGuard
{
    /// <summary>命令 CanExecute 中使用：返回是否有权限</summary>
    public static bool Can(string perm) => UserSession.HasPermission(perm);

    /// <summary>方法入口中使用：无权限则抛异常</summary>
    public static void Require(string perm)
    {
        if (!UserSession.HasPermission(perm))
            throw new System.UnauthorizedAccessException($"当前用户无 [{perm}] 权限");
    }
}
