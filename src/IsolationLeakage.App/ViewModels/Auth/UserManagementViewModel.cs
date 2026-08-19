using System.Collections.ObjectModel;
using System.ComponentModel;
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
    public sealed class RoleItem : INotifyPropertyChanged
    {
        private bool _isChecked;

        public RoleItem(Role role)
        {
            Role = role;
        }

        public Role Role { get; }
        public string RoleName => Role.RoleName;
        public int RoleId => Role.RoleId;

        public bool IsChecked
        {
            get => _isChecked;
            set
            {
                if (_isChecked != value)
                {
                    _isChecked = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
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

    /// <summary>加载代际号：搜索框每个按键触发一次加载，慢查询后返回的陈旧结果不得覆盖新结果</summary>
    private int _loadGeneration;

    private async Task LoadDataAsync()
    {
        var gen = ++_loadGeneration;
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

            var roles = await context.Roles
                .Where(r => r.Status == UserStatus.Enabled)
                .OrderBy(r => r.Sort)
                .ToListAsync();

            // 已有更新的加载发起（用户继续输入/点了刷新），丢弃本次陈旧结果
            if (gen != _loadGeneration) return;

            Users.Clear();
            foreach (var u in users) Users.Add(u);

            // 编辑态下不重建 RoleItems：重建会清空已勾选的角色，
            // 此时点保存会静默清掉该用户的全部角色
            if (!IsEditing)
            {
                RoleItems.Clear();
                foreach (var r in roles) RoleItems.Add(new RoleItem(r));
            }

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
        // 统一 Trim 后再校验/入库：带首尾空格的用户名可创建却永远无法登录（登录侧按 Trim 后比对）
        var userName = EditUserName.Trim();
        if (string.IsNullOrEmpty(userName))
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
            var currentUserId = Services.Security.UserSession.Current?.User.UserId;

            // 编辑/新增模式以 _editingUser 判定（不能用"用户名是否已存在"判定：
            // 编辑时改名会查不到而误走新增分支，创建新账号丢失原用户的修改；
            // 搜索过滤后查内存列表又会把本人改名误判为"已存在"——一律查库并按编辑对象排除自身）
            bool isEditing = _editingUser != null;

            // 检查用户名是否重复（按编辑对象排除自身）
            if (await context.Users.AnyAsync(u => u.UserName == userName
                    && (!isEditing || u.UserId != _editingUser!.UserId)))
            {
                Message = "用户名已存在";
                OnShowToast?.Invoke($"用户名【{userName}】已存在，请更换！", ToastType.Warning);
                return;
            }

            // 检查昵称是否重复（昵称非空时检查，同样按编辑对象排除自身）
            if (!string.IsNullOrWhiteSpace(EditNickName)
                && await context.Users.AnyAsync(u => u.NickName == EditNickName
                    && (!isEditing || u.UserId != _editingUser!.UserId)))
            {
                Message = "昵称已存在";
                OnShowToast?.Invoke($"昵称【{EditNickName}】已存在，请更换！", ToastType.Warning);
                return;
            }

            var roleIds = RoleItems.Where(ri => ri.IsChecked).Select(ri => ri.RoleId).ToList();
            bool willHaveAdmin = RoleItems.Any(ri => ri.IsChecked && ri.Role.RoleKey == "admin");

            if (isEditing)
            {
                // 按 UserId 取库中最新实体（不能用新用户名查——改名场景查不到目标或查到别人）
                var existing = await context.Users.FirstOrDefaultAsync(u => u.UserId == _editingUser!.UserId);
                if (existing == null)
                {
                    Message = "该用户已被其他客户端删除，请刷新后重试";
                    OnShowToast?.Invoke("该用户已被其他客户端删除！", ToastType.Warning);
                    return;
                }

                var hadAdmin = await (from ur in context.UserRoles
                                      join r in context.Roles on ur.RoleId equals r.RoleId
                                      where ur.UserId == existing.UserId && r.RoleKey == "admin"
                                      select ur.UserId).AnyAsync();
                bool losesAdmin = hadAdmin && !willHaveAdmin;
                bool disabling = EditStatus == UserStatus.Disabled && existing.Status == UserStatus.Enabled;

                // —— 管理员自锁保护：不能把自己停用/移除管理员角色，否则会话过期后无人能进系统管理 ——
                if (existing.UserId == currentUserId)
                {
                    if (EditStatus != UserStatus.Enabled)
                    {
                        Message = "不能停用当前登录的账户";
                        OnShowToast?.Invoke("不能停用当前登录的账户，否则将无法进入系统管理！", ToastType.Warning);
                        return;
                    }
                    if (hadAdmin && !willHaveAdmin)
                    {
                        Message = "不能移除自己的管理员角色";
                        OnShowToast?.Invoke("不能移除自己的管理员角色，请由其他管理员操作！", ToastType.Warning);
                        return;
                    }
                }

                // —— 最后一个管理员保护：降级/停用启用中的管理员前，确认还有其它启用的 admin ——
                if ((losesAdmin || disabling) && hadAdmin)
                {
                    var otherActiveAdminExists = await (from u in context.Users
                                                        join ur in context.UserRoles on u.UserId equals ur.UserId
                                                        join r in context.Roles on ur.RoleId equals r.RoleId
                                                        where r.RoleKey == "admin"
                                                              && u.Status == UserStatus.Enabled
                                                              && u.UserId != existing.UserId
                                                        select u.UserId).AnyAsync();
                    if (!otherActiveAdminExists)
                    {
                        Message = "系统必须保留至少一个启用的管理员";
                        OnShowToast?.Invoke("无法保存：这是系统最后一个启用的管理员，请先创建/启用其它管理员账户！", ToastType.Warning);
                        return;
                    }
                }

                var oldStatus = existing.Status;
                existing.UserName = userName;
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
                await userService.AssignRolesAsync(existing.UserId, roleIds);

                // 记录操作日志
                var roleNames = RoleItems.Where(ri => ri.IsChecked).Select(ri => ri.RoleName).ToList();
                await logService.LogAsync("修改用户", currentUser,
                    $"用户【{userName}】信息已更新，角色：{string.Join(", ", roleNames)}", "Success");

                _editingUser = null;
                CancelEdit();
                await LoadDataAsync();
                Message = $"✅ 已更新用户：{userName}";
                OnShowToast?.Invoke($"用户【{userName}】更新成功！", ToastType.Success);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(EditPassword))
                {
                    Message = "新增用户必须设置密码";
                    OnShowToast?.Invoke("请设置用户密码！", ToastType.Warning);
                    return;
                }

                var newUser = new User
                {
                    UserName = userName,
                    NickName = EditNickName,
                    PasswordHash = BCrypt.Net.BCrypt.HashPassword(EditPassword),
                    Status = EditStatus,
                    Remark = EditRemark,
                    CreatedAt = DateTime.Now
                };
                await context.Users.AddAsync(newUser);
                await context.SaveChangesAsync();

                // 保存角色分配
                if (roleIds.Count > 0)
                {
                    await userService.AssignRolesAsync(newUser.UserId, roleIds);
                }

                // 记录操作日志
                var roleNames = RoleItems.Where(ri => ri.IsChecked).Select(ri => ri.RoleName).ToList();
                await logService.LogAsync("创建用户", currentUser,
                    $"新增用户【{userName}】，角色：{string.Join(", ", roleNames)}", "Success");

                _editingUser = null;
                CancelEdit();
                await LoadDataAsync();
                Message = $"✅ 已新增用户：{userName}";
                OnShowToast?.Invoke($"用户【{userName}】新增成功！", ToastType.Success);
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
