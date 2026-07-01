namespace IsolationLeakage.App.Services.Security;

/// <summary>
/// 角色权限定义（硬编码，无需手动配置）
/// <para>
/// admin  — 超级管理员：全部权限，可编辑、可删除、可管理用户/角色/系统
/// operator — 试验工程师：可查看、可编辑数据、可导出报告，但不能删除、不能进系统设置
/// viewer — 只读用户：只能查看和导出报告，不能编辑、不能删除
/// </para>
/// </summary>
public static class RolePermissions
{
    private static readonly string[] AdminPerms =
    [
        // 页面
        Perms.OverviewView, Perms.MasterDataView, Perms.RecipeView,
        Perms.RecordsView, Perms.RealtimeView, Perms.AnalysisView, Perms.SystemView,
        // 基础台账 - 编辑
        Perms.ProjectAdd, Perms.PathAdd, Perms.DeviceAdd,
        // 基础台账 - 删除
        Perms.ProjectDelete, Perms.PathDelete, Perms.DeviceDelete,
        // 配方
        Perms.RecipeEdit, Perms.RecipeDelete,
        // 实时监视
        Perms.RealtimeEdit, Perms.RealtimeDelete,
        // 试验记录
        Perms.TaskDownload, Perms.RecordsUpload, Perms.RecordsDelete, Perms.ReportExport,
        // 系统管理
        Perms.UserManage, Perms.RoleManage, Perms.LogView, Perms.BackupView, Perms.MigrateView,
    ];

    private static readonly string[] OperatorPerms =
    [
        // 页面（不含系统设置）
        Perms.OverviewView, Perms.MasterDataView, Perms.RecipeView,
        Perms.RecordsView, Perms.RealtimeView, Perms.AnalysisView,
        // 基础台账 - 可以编辑
        Perms.ProjectAdd, Perms.PathAdd, Perms.DeviceAdd,
        // 配方 - 可以编辑
        Perms.RecipeEdit,
        // 实时监视 - 可以编辑变量，但不能删除
        Perms.RealtimeEdit,
        // 试验记录 - 可以上传/导出，但不能删除
        Perms.TaskDownload, Perms.RecordsUpload, Perms.ReportExport,
    ];

    private static readonly string[] ViewerPerms =
    [
        // 页面（不含系统设置）
        Perms.OverviewView, Perms.MasterDataView, Perms.RecipeView,
        Perms.RecordsView, Perms.RealtimeView, Perms.AnalysisView,
        // 只能导出报告
        Perms.ReportExport,
    ];

    /// <summary>
    /// 根据角色标识获取该角色拥有的所有权限
    /// </summary>
    public static HashSet<string> GetPermissions(IEnumerable<string> roleKeys)
    {
        var perms = new HashSet<string>();
        foreach (var key in roleKeys)
        {
            var keyPerms = key switch
            {
                "admin" => AdminPerms,
                "operator" => OperatorPerms,
                "viewer" => ViewerPerms,
                _ => ViewerPerms, // 未知角色按只读处理
            };
            foreach (var p in keyPerms)
                perms.Add(p);
        }
        return perms;
    }
}
