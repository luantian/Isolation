using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IsolationLeakage.App.Controls;
using IsolationLeakage.App.Data;
using IsolationLeakage.App.Models.Security;
using IsolationLeakage.App.Services;
using IsolationLeakage.App.Services.Security;
using Microsoft.EntityFrameworkCore;

namespace IsolationLeakage.App.ViewModels.Auth;

/// <summary>
/// 用户管理视图模型
/// </summary>
public partial class UserManagementViewModel : IsolationLeakage.App.ViewModels.ViewModelBase
{
    private string _message = string.Empty;
    private string _searchText = string.Empty;
    private int _totalUsers;
    private int _enabledUsers;

    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private string _editUserName = string.Empty;
    [ObservableProperty] private string _editNickName = string.Empty;
    [ObservableProperty] private string _editPassword = string.Empty;
    [ObservableProperty] private UserStatus _editStatus;
    [ObservableProperty] private string _editRemark = string.Empty;
    [ObservableProperty] private string _editPanelStatus = "编辑中";

    public UserManagementViewModel()
    {
        Users = [];
        _ = LoadDataAsync();
    }

    public ObservableCollection<User> Users { get; }

    /// <summary>带选中状态的角色列表（用于 UI CheckBox 绑定）</summary>
    public ObservableCollection<RoleItem> RoleItems { get; } = [];

    /// <summary>当前编辑的用户（用于识别编辑模式）</summary>
    private User? _editingUser;

    /// <summary>角色包装类（支持 IsChecked 绑定）</summary>
    public sealed class RoleItem(Role role)
    {
        public Role Role { get; } = role;
        public string RoleName => role.RoleName;
        public int RoleId => role.RoleId;
        public bool IsChecked { get; set; }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
                _ = LoadDataAsync();
        }
    }

    public string Message
    {
        get => _message;
        set => SetProperty(ref _message, value);
    }

    public int TotalUsers
    {
        get => _totalUsers;
        set => SetProperty(ref _totalUsers, value);
    }

    public int EnabledUsers
    {
        get => _enabledUsers;
        set => SetProperty(ref _enabledUsers, value);
    }

    public ICommand RefreshCommand => new RelayCommand(async () => await LoadDataAsync());
    public ICommand AddUserCommand => new RelayCommand(StartAdd, () => PermissionGuard.Can(Perms.UserManage));
    public ICommand SaveCommand => new RelayCommand(async () => await SaveAsync(), () => PermissionGuard.Can(Perms.UserManage));
    public ICommand CancelEditCommand => new RelayCommand(CancelEdit);
    public ICommand ToggleStatusCommand => new RelayCommand<User>(async user => await ToggleStatusAsync(user), user => PermissionGuard.Can(Perms.UserManage));

    /// <summary>Toast 通知事件（由 View 层订阅）</summary>
    public event Action<string, ToastType>? OnShowToast;

    private async Task LoadDataAsync()
    {
        try
        {
            using var context = DbContextFactory.CreateDbContext();

            var users = await context.Users
                .Include(u => u.UserRoles)
                .OrderBy(u => u.UserName)
                .ToListAsync();

            if (!string.IsNullOrWhiteSpace(SearchText))
            {
                var keyword = SearchText.Trim().ToLower();
                users = users.Where(u =>
                    u.UserName.ToLower().Contains(keyword) ||
                    (u.NickName != null && u.NickName.ToLower().Contains(keyword)) ||
                    (u.Email != null && u.Email.ToLower().Contains(keyword)) ||
                    (u.Phone != null && u.Phone.Contains(keyword))).ToList();
            }

            Users.Clear();
            foreach (var u in users) Users.Add(u);

            var roles = await context.Roles
                .Where(r => r.Status == UserStatus.Enabled)
                .OrderBy(r => r.Sort)
                .ToListAsync();

            RoleItems.Clear();
            foreach (var r in roles) RoleItems.Add(new RoleItem(r));

            TotalUsers = users.Count;
            EnabledUsers = users.Count(u => u.Status == UserStatus.Enabled);

            if (!IsEditing)
                Message = $"共 {TotalUsers} 个用户，{EnabledUsers} 个已启用";
        }
        catch (Exception ex)
        {
            Message = $"加载失败：{ex.Message}";
        }
    }

    private void StartAdd()
    {
        IsEditing = true;
        _editingUser = null;
        EditUserName = string.Empty;
        EditNickName = string.Empty;
        EditPassword = string.Empty;
        EditStatus = UserStatus.Enabled;
        EditRemark = string.Empty;
        foreach (var ri in RoleItems) ri.IsChecked = false;
        Message = "请填写新用户信息，选择角色后点击保存完成创建";
    }

    public void SelectUser(User user)
    {
        if (user == null) return;
        _editingUser = user;
        IsEditing = true;
        EditUserName = user.UserName;
        EditNickName = user.NickName ?? string.Empty;
        EditPassword = string.Empty; // 不显示原密码
        EditStatus = user.Status;
        EditRemark = user.Remark ?? string.Empty;

        // 设置角色选中状态
        var userRoleIds = user.UserRoles.Select(ur => ur.RoleId).ToHashSet();
        foreach (var ri in RoleItems)
        {
            ri.IsChecked = userRoleIds.Contains(ri.RoleId);
        }

        Message = $"正在编辑用户：{user.UserName}";
    }

    private async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(EditUserName))
        {
            Message = "用户名不能为空";
            OnShowToast?.Invoke("用户名不能为空！", ToastType.Warning);
            return;
        }

        try
        {
            using var context = DbContextFactory.CreateDbContext();
            var userService = new UserService(context);
            var logService = new OperationLogService(context);
            var currentUser = Services.Security.UserSession.Current?.User.UserName ?? "system";

            // 检查用户名是否重复
            var existingByUserName = await context.Users
                .FirstOrDefaultAsync(u => u.UserName == EditUserName);

            // 如果是编辑模式，排除当前编辑的用户本身
            if (existingByUserName != null)
            {
                var editingUser = Users.FirstOrDefault(u => u.UserName == EditUserName);
                if (editingUser == null || editingUser.UserId != existingByUserName.UserId)
                {
                    Message = "用户名已存在";
                    OnShowToast?.Invoke($"用户名【{EditUserName}】已存在，请更换！", ToastType.Warning);
                    return;
                }
            }

            // 检查昵称是否重复（昵称非空时检查）
            if (!string.IsNullOrWhiteSpace(EditNickName))
            {
                var existingByNickName = await context.Users
                    .FirstOrDefaultAsync(u => u.NickName == EditNickName);

                if (existingByNickName != null)
                {
                    var editingUser = Users.FirstOrDefault(u => u.UserName == EditUserName);
                    if (editingUser == null || editingUser.UserId != existingByNickName.UserId)
                    {
                        Message = "昵称已存在";
                        OnShowToast?.Invoke($"昵称【{EditNickName}】已存在，请更换！", ToastType.Warning);
                        return;
                    }
                }
            }

            if (existingByUserName != null)
            {
                // 已存在 → 编辑模式
                var existing = existingByUserName;
                var oldStatus = existing.Status;
                existing.NickName = EditNickName;
                existing.Status = EditStatus;
                existing.Remark = EditRemark;
                existing.UpdatedAt = DateTime.Now;

                // 密码留空不改，有值则更新
                if (!string.IsNullOrWhiteSpace(EditPassword))
                {
                    existing.PasswordHash = BCrypt.Net.BCrypt.HashPassword(EditPassword);
                }

                await context.SaveChangesAsync();

                // 保存角色分配
                var roleIds = RoleItems.Where(ri => ri.IsChecked).Select(ri => ri.RoleId).ToList();
                await userService.AssignRolesAsync(existing.UserId, roleIds);

                // 记录操作日志
                await logService.LogAsync("修改用户", currentUser,
                    $"用户【{EditUserName}】信息已更新，角色：{string.Join(", ", roleIds)}", "Success");

                _editingUser = null;
                CancelEdit();
                await LoadDataAsync();
                Message = $"✅ 已更新用户：{EditUserName}";
                OnShowToast?.Invoke($"用户【{EditUserName}】更新成功！", ToastType.Success);
            }
            else
            {
                // 不存在 → 新增模式
                if (string.IsNullOrWhiteSpace(EditPassword))
                {
                    Message = "新增用户必须设置密码";
                    OnShowToast?.Invoke("请设置用户密码！", ToastType.Warning);
                    return;
                }

                var newUser = new User
                {
                    UserName = EditUserName,
                    NickName = EditNickName,
                    PasswordHash = BCrypt.Net.BCrypt.HashPassword(EditPassword),
                    Status = EditStatus,
                    Remark = EditRemark,
                    CreatedAt = DateTime.Now
                };
                await context.Users.AddAsync(newUser);
                await context.SaveChangesAsync();

                // 保存角色分配
                var roleIds = RoleItems.Where(ri => ri.IsChecked).Select(ri => ri.RoleId).ToList();
                if (roleIds.Count > 0)
                {
                    await userService.AssignRolesAsync(newUser.UserId, roleIds);
                }

                // 记录操作日志
                await logService.LogAsync("创建用户", currentUser,
                    $"新增用户【{EditUserName}】，角色：{string.Join(", ", roleIds)}", "Success");

                _editingUser = null;
                CancelEdit();
                await LoadDataAsync();
                Message = $"✅ 已新增用户：{EditUserName}";
                OnShowToast?.Invoke($"用户【{EditUserName}】新增成功！", ToastType.Success);
            }
        }
        catch (Exception ex)
        {
            Message = $"保存失败：{ex.Message}";
            OnShowToast?.Invoke($"保存失败：{ex.Message}", ToastType.Error);
        }
    }

    private void CancelEdit()
    {
        IsEditing = false;
        Message = "已取消编辑";
    }

    private async Task ToggleStatusAsync(User user)
    {
        if (user == null) return;

        try
        {
            using var context = DbContextFactory.CreateDbContext();
            var dbUser = await context.Users.FindAsync(user.UserId);
            if (dbUser == null) return;

            var oldStatus = dbUser.Status.ToText();
            dbUser.Status = dbUser.Status == UserStatus.Enabled ? UserStatus.Disabled : UserStatus.Enabled;
            var newStatus = dbUser.Status.ToText();
            dbUser.UpdatedAt = DateTime.Now;
            await context.SaveChangesAsync();

            // 记录操作日志
            var logService = new OperationLogService(context);
            var currentUser = Services.Security.UserSession.Current?.User.UserName ?? "system";
            await logService.LogAsync("修改用户", currentUser,
                $"用户【{user.UserName}】状态从{oldStatus}切换为{newStatus}", "Success");

            await LoadDataAsync();
            Message = $"✅ 用户 {user.UserName} 已{newStatus}";
            OnShowToast?.Invoke($"用户【{user.UserName}】已{newStatus}！", ToastType.Success);
        }
        catch (Exception ex)
        {
            Message = $"操作失败：{ex.Message}";
        }
    }
}
