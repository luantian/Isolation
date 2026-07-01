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

    private async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(EditRoleName) || string.IsNullOrWhiteSpace(EditRoleKey))
        {
            Message = "角色名称和角色标识不能为空";
            return;
        }

        try
        {
            using var context = DbContextFactory.CreateDbContext();
            var roleService = new RoleService(context);
            var logService = new OperationLogService(context);
            var currentUser = UserSession.Current?.User.UserName ?? "system";

            var existing = await roleService.GetByKeyAsync(EditRoleKey);
            if (existing != null)
            {
                existing.RoleName = EditRoleName;
                existing.Sort = EditSort;
                existing.Status = EditStatus;
                existing.Remark = EditRemark;
                await context.SaveChangesAsync();

                await logService.LogAsync("修改角色", currentUser,
                    $"修改角色【{existing.RoleName}】({existing.RoleKey})", "Success");
            }
            else
            {
                var newRole = new Role
                {
                    RoleName = EditRoleName,
                    RoleKey = EditRoleKey,
                    Sort = EditSort,
                    Status = EditStatus,
                    Remark = EditRemark,
                    CreatedAt = DateTime.Now
                };
                await context.Roles.AddAsync(newRole);
                await context.SaveChangesAsync();

                await logService.LogAsync("创建角色", currentUser,
                    $"新增角色【{newRole.RoleName}】({newRole.RoleKey})", "Success");
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
