using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IsolationLeakage.App.Data;
using IsolationLeakage.App.Models.Security;
using IsolationLeakage.App.Services;
using IsolationLeakage.App.Services.Security;
using Microsoft.EntityFrameworkCore;

namespace IsolationLeakage.App.ViewModels.Auth;

/// <summary>
/// 角色管理视图模型
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
    [ObservableProperty] private List<int> _selectedMenuIds = [];

    public RoleManagementViewModel()
    {
        Roles = new ObservableCollection<Role>();
        CheckableMenuTree = new ObservableCollection<CheckableMenu>();
        _ = LoadDataAsync();
    }

    public ObservableCollection<Role> Roles { get; }
    public ObservableCollection<CheckableMenu> CheckableMenuTree { get; }

    public string Message
    {
        get => _message;
        set => SetProperty(ref _message, value);
    }

    public ICommand RefreshCommand => new RelayCommand(async () => await LoadDataAsync());
    public ICommand AddRoleCommand => new RelayCommand(StartAdd);
    public ICommand SaveCommand => new RelayCommand(async () => await SaveAsync());
    public ICommand CancelEditCommand => new RelayCommand(CancelEdit);

    private async Task LoadDataAsync()
    {
        try
        {
            using var context = DbContextFactory.CreateDbContext();

            var roles = await context.Roles.OrderBy(r => r.Sort).ToListAsync();
            Roles.Clear();
            foreach (var r in roles) Roles.Add(r);

            var menuService = new MenuService(context);
            var menus = await menuService.GetTreeAsync();
            CheckableMenuTree.Clear();
            foreach (var m in menus) CheckableMenuTree.Add(BuildCheckableMenu(m));

            if (!IsEditing)
                Message = $"共 {Roles.Count} 个角色";
        }
        catch (Exception ex)
        {
            Message = $"加载失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 递归构建可勾选菜单树
    /// </summary>
    private static CheckableMenu BuildCheckableMenu(Menu menu)
    {
        var node = new CheckableMenu
        {
            MenuId = menu.MenuId,
            MenuName = menu.MenuName,
            TypeText = menu.TypeText,
        };
        foreach (var child in menu.Children.OrderBy(c => c.Sort))
        {
            node.Children.Add(BuildCheckableMenu(child));
        }
        return node;
    }

    /// <summary>
    /// 递归设置菜单勾选状态
    /// </summary>
    private static void SetMenuChecked(IEnumerable<CheckableMenu> nodes, HashSet<int> checkedIds)
    {
        foreach (var node in nodes)
        {
            node.IsChecked = checkedIds.Contains(node.MenuId);
            SetMenuChecked(node.Children, checkedIds);
        }
    }

    /// <summary>
    /// 递归收集所有已勾选的菜单 ID
    /// </summary>
    private static void CollectCheckedMenuIds(IEnumerable<CheckableMenu> nodes, List<int> ids)
    {
        foreach (var node in nodes)
        {
            if (node.IsChecked) ids.Add(node.MenuId);
            CollectCheckedMenuIds(node.Children, ids);
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
        SelectedMenuIds.Clear();
        // 清除所有菜单勾选
        SetMenuChecked(CheckableMenuTree, []);
        Message = "请填写新角色信息";
    }

    public async void SelectRole(Role role)
    {
        if (role == null) return;
        IsEditing = true;
        EditingRoleId = role.RoleId;
        EditRoleName = role.RoleName;
        EditRoleKey = role.RoleKey;
        EditSort = role.Sort;
        EditStatus = role.Status;
        EditRemark = role.Remark ?? string.Empty;

        // 加载该角色已分配的菜单
        try
        {
            using var context = DbContextFactory.CreateDbContext();
            var roleService = new RoleService(context);
            var menuIds = await roleService.GetRoleMenuIdsAsync(role.RoleId);
            var menuIdSet = new HashSet<int>(menuIds);
            SetMenuChecked(CheckableMenuTree, menuIdSet);
            SelectedMenuIds = menuIds;
        }
        catch
        {
            SetMenuChecked(CheckableMenuTree, []);
            SelectedMenuIds.Clear();
        }

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
            var currentUser = Services.Security.UserSession.Current?.User.UserName ?? "system";

            int savedRoleId;

            var existing = await roleService.GetByKeyAsync(EditRoleKey);
            if (existing != null)
            {
                existing.RoleName = EditRoleName;
                existing.Sort = EditSort;
                existing.Status = EditStatus;
                existing.Remark = EditRemark;
                await context.SaveChangesAsync();
                savedRoleId = existing.RoleId;

                // 记录操作日志
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
                savedRoleId = newRole.RoleId;

                // 记录操作日志
                await logService.LogAsync("创建角色", currentUser,
                    $"新增角色【{newRole.RoleName}】({newRole.RoleKey})", "Success");
            }

            // 保存菜单权限分配
            var checkedMenuIds = new List<int>();
            CollectCheckedMenuIds(CheckableMenuTree, checkedMenuIds);
            await roleService.AssignMenusAsync(savedRoleId, checkedMenuIds);

            CancelEdit();
            await LoadDataAsync();
            Message = $"✅ 角色信息已保存（含 {checkedMenuIds.Count} 个菜单权限）";
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

/// <summary>
/// 可勾选的菜单树节点
/// </summary>
public sealed class CheckableMenu : ObservableObject
{
    public int MenuId { get; set; }
    public string MenuName { get; set; } = string.Empty;
    public string TypeText { get; set; } = string.Empty;
    public ObservableCollection<CheckableMenu> Children { get; } = [];

    private bool _isChecked;
    public bool IsChecked
    {
        get => _isChecked;
        set => SetProperty(ref _isChecked, value);
    }
}
