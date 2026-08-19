using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IsolationLeakage.App.Data;
using IsolationLeakage.App.Models.Security;
using IsolationLeakage.App.Services;
using IsolationLeakage.App.Services.Security;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace IsolationLeakage.App.ViewModels.Auth;

/// <summary>
/// 角色管理视图模型
/// 权限由 RolePermissions 按角色 key 硬编码分配，此处只管理角色基本信息
/// </summary>
public partial class RoleManagementViewModel : IsolationLeakage.App.ViewModels.ViewModelBase
{
    private string _message = string.Empty;

    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private int? _editingRoleId;
    [ObservableProperty] private string _editRoleName = string.Empty;
    [ObservableProperty] private string _editRoleKey = string.Empty;
    [ObservableProperty] private int _editSort;
    [ObservableProperty] private UserStatus _editStatus = UserStatus.Enabled;
    [ObservableProperty] private string _editRemark = string.Empty;
    [ObservableProperty] private string _editPanelStatus = "编辑中";

    public RoleManagementViewModel()
    {
        Roles = new ObservableCollection<Role>();
        _ = LoadDataAsync();
    }

    public ObservableCollection<Role> Roles { get; }

    public string Message
    {
        get => _message;
        set => SetProperty(ref _message, value);
    }

    public ICommand RefreshCommand => new RelayCommand(async () => await LoadDataAsync());
    public ICommand AddRoleCommand => new RelayCommand(StartAdd, () => PermissionGuard.Can(Perms.RoleManage));
    public ICommand SaveCommand => new RelayCommand(async () => await SaveAsync(), () => PermissionGuard.Can(Perms.RoleManage));
    public ICommand CancelEditCommand => new RelayCommand(CancelEdit);

    private async Task LoadDataAsync()
    {
        try
        {
            using var context = DbContextFactory.CreateDbContext();
            var roles = await context.Roles.OrderBy(r => r.Sort).ToListAsync();
            Roles.Clear();
            foreach (var r in roles) Roles.Add(r);

            if (!IsEditing)
                Message = $"共 {Roles.Count} 个角色";
        }
        catch (Exception ex)
        {
            Message = $"加载失败：{ex.Message}";
        }
    }

    private void StartAdd()
    {
        IsEditing = true;
        EditingRoleId = null;
        EditRoleName = string.Empty;
        EditRoleKey = string.Empty;
        EditSort = Roles.Count + 1;
        EditStatus = UserStatus.Enabled;
        EditRemark = string.Empty;
        Message = "请填写新角色信息";
    }

    public void SelectRole(Role role)
    {
        if (role == null) return;
        IsEditing = true;
        EditingRoleId = role.RoleId;
        EditRoleName = role.RoleName;
        EditRoleKey = role.RoleKey;
        EditSort = role.Sort;
        EditStatus = role.Status;
        EditRemark = role.Remark ?? string.Empty;
        Message = $"正在编辑角色：{role.RoleName}";
    }

    /// <summary>内置角色标识：权限按 RolePermissions 以 key 硬编码映射，其 key 与状态不可变更</summary>
    private static readonly string[] BuiltInRoleKeys = ["admin", "operator", "viewer"];

    private async Task SaveAsync()
    {
        var roleName = EditRoleName.Trim();
        var roleKey = EditRoleKey.Trim();
        if (string.IsNullOrEmpty(roleName) || string.IsNullOrEmpty(roleKey))
        {
            Message = "角色名称和角色标识不能为空";
            return;
        }

        try
        {
            using var context = DbContextFactory.CreateDbContext();
            var logService = new OperationLogService(context);
            var currentUser = UserSession.Current?.User.UserName ?? "system";

            // 编辑/新增以 EditingRoleId 判定（不能用"按表单 RoleKey 查库"判定：
            // 编辑时把标识改成 admin 会查到内置 admin 并把表单值整写到它头上——
            // 可把 admin 改名甚至停用；改成不存在的 key 又静默新建，原角色修改全部丢失）
            bool isEditing = EditingRoleId.HasValue;
            Role? target = isEditing
                ? await context.Roles.FirstOrDefaultAsync(r => r.RoleId == EditingRoleId!.Value)
                : null;
            if (isEditing && target == null)
            {
                Message = "该角色已被其他客户端删除，请刷新后重试";
                return;
            }

            // 内置角色保护：改内置角色的 key 会让权限映射失配（未知 key 按只读处理）；
            // 停用内置角色（尤其 admin）会导致无人能管理系统
            if (target != null && BuiltInRoleKeys.Contains(target.RoleKey))
            {
                if (!string.Equals(target.RoleKey, roleKey, StringComparison.OrdinalIgnoreCase))
                {
                    Message = $"内置角色【{target.RoleKey}】的标识不允许修改（权限按标识硬编码映射）";
                    return;
                }
                if (EditStatus != UserStatus.Enabled)
                {
                    Message = $"内置角色【{target.RoleKey}】不允许停用";
                    return;
                }
            }

            // 标识查重（RoleKey 有唯一索引，按编辑对象排除自身）
            if (await context.Roles.AnyAsync(r => r.RoleKey == roleKey
                    && (!isEditing || r.RoleId != EditingRoleId!.Value)))
            {
                Message = $"角色标识【{roleKey}】已存在";
                return;
            }

            if (isEditing)
            {
                var oldKey = target!.RoleKey;
                target.RoleName = roleName;
                target.RoleKey = roleKey;
                target.Sort = EditSort;
                target.Status = EditStatus;
                target.Remark = EditRemark;
                await context.SaveChangesAsync();

                await logService.LogAsync("修改角色", currentUser,
                    $"修改角色【{roleName}】({oldKey}{(oldKey != roleKey ? " → " + roleKey : "")})", "Success");
            }
            else
            {
                var newRole = new Role
                {
                    RoleName = roleName,
                    RoleKey = roleKey,
                    Sort = EditSort,
                    Status = EditStatus,
                    Remark = EditRemark,
                    CreatedAt = DateTime.Now
                };
                await context.Roles.AddAsync(newRole);
                await context.SaveChangesAsync();

                await logService.LogAsync("创建角色", currentUser,
                    $"新增角色【{roleName}】({roleKey})", "Success");
            }

            CancelEdit();
            await LoadDataAsync();
            Message = "✅ 角色信息已保存";
        }
        catch (Exception ex)
        {
            Message = $"保存失败：{ex.Message}";
        }
    }

    private void CancelEdit()
    {
        IsEditing = false;
        EditingRoleId = null;
    }
}
