using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IsolationLeakage.App.Data;
using IsolationLeakage.App.Models.Security;
using IsolationLeakage.App.Services.Security;
using Microsoft.EntityFrameworkCore;

namespace IsolationLeakage.App.ViewModels.Auth;

/// <summary>
/// 菜单管理视图模型
/// </summary>
public partial class MenuManagementViewModel : IsolationLeakage.App.ViewModels.ViewModelBase
{
    private string _message = string.Empty;

    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private string _editMenuName = string.Empty;
    [ObservableProperty] private int _editParentId;
    [ObservableProperty] private int _editSort;
    [ObservableProperty] private SysMenuType _editType;
    [ObservableProperty] private string _editPerms = string.Empty;
    [ObservableProperty] private string _editPath = string.Empty;
    [ObservableProperty] private string _editComponent = string.Empty;
    [ObservableProperty] private string _editIcon = string.Empty;
    [ObservableProperty] private bool _editVisible = true;
    [ObservableProperty] private string _editRemark = string.Empty;

    public MenuManagementViewModel()
    {
        Menus = new ObservableCollection<Menu>();
        ParentOptions = new ObservableCollection<Menu> { new() { MenuId = 0, MenuName = "顶级菜单" } };
        _ = LoadDataAsync();
    }

    public ObservableCollection<Menu> Menus { get; }
    public ObservableCollection<Menu> ParentOptions { get; }

    public string Message
    {
        get => _message;
        set => SetProperty(ref _message, value);
    }

    public ICommand RefreshCommand => new RelayCommand(async () => await LoadDataAsync());
    public ICommand AddMenuCommand => new RelayCommand(StartAdd);
    public ICommand SaveCommand => new RelayCommand(async () => await SaveAsync());
    public ICommand CancelEditCommand => new RelayCommand(CancelEdit);

    private async Task LoadDataAsync()
    {
        try
        {
            using var context = DbContextFactory.CreateDbContext();
            var menuService = new MenuService(context);

            var menus = await menuService.GetAllAsync();
            Menus.Clear();
            foreach (var m in menus) Menus.Add(m);

            ParentOptions.Clear();
            ParentOptions.Add(new Menu { MenuId = 0, MenuName = "顶级菜单" });
            foreach (var m in menus.Where(m => m.Type == SysMenuType.Directory))
            {
                ParentOptions.Add(m);
            }

            if (!IsEditing)
                Message = $"共 {Menus.Count} 个菜单/按钮权限";
        }
        catch (Exception ex)
        {
            Message = $"加载失败：{ex.Message}";
        }
    }

    private void StartAdd()
    {
        IsEditing = true;
        EditMenuName = string.Empty;
        EditParentId = 0;
        EditSort = Menus.Count + 1;
        EditType = SysMenuType.Menu;
        EditPerms = string.Empty;
        EditPath = string.Empty;
        EditComponent = string.Empty;
        EditIcon = string.Empty;
        EditVisible = true;
        EditRemark = string.Empty;
        Message = "请填写新菜单信息";
    }

    public void SelectMenu(Menu menu)
    {
        if (menu == null) return;
        IsEditing = true;
        EditMenuName = menu.MenuName;
        EditParentId = menu.ParentId;
        EditSort = menu.Sort;
        EditType = menu.Type;
        EditPerms = menu.Perms ?? string.Empty;
        EditPath = menu.Path ?? string.Empty;
        EditComponent = menu.Component ?? string.Empty;
        EditIcon = menu.Icon ?? string.Empty;
        EditVisible = menu.Visible;
        EditRemark = menu.Remark ?? string.Empty;
        Message = $"正在编辑：{menu.MenuName}";
    }

    private async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(EditMenuName))
        {
            Message = "菜单名称不能为空";
            return;
        }

        try
        {
            using var context = DbContextFactory.CreateDbContext();
            var menuService = new MenuService(context);

            var newMenu = new Menu
            {
                MenuName = EditMenuName,
                ParentId = EditParentId,
                Sort = EditSort,
                Type = EditType,
                Perms = EditPerms,
                Path = EditPath,
                Component = EditComponent,
                Icon = EditIcon,
                Visible = EditVisible,
                Remark = EditRemark,
                CreatedAt = DateTime.Now
            };

            await menuService.AddAsync(newMenu);

            CancelEdit();
            await LoadDataAsync();
            Message = "✅ 菜单信息已保存";
        }
        catch (Exception ex)
        {
            Message = $"保存失败：{ex.Message}";
        }
    }

    private void CancelEdit()
    {
        IsEditing = false;
    }
}
