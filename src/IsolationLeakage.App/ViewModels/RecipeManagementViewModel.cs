using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IsolationLeakage.App.Models;
using IsolationLeakage.App.Models.Database;
using IsolationLeakage.App.Services;
using IsolationLeakage.App.Services.Security;
using Microsoft.Win32;

namespace IsolationLeakage.App.ViewModels;

/// <summary>
/// 配方编辑 ViewModel（基于甲方配方组0.csv格式）
/// </summary>
public sealed partial class RecipeEditViewModel : ObservableObject
{
    [ObservableProperty]
    private int _id;

    [ObservableProperty]
    private string _recipeName = string.Empty;

    [ObservableProperty]
    private int _sequenceNo;

    [ObservableProperty]
    private string _system = string.Empty;

    [ObservableProperty]
    private decimal _penetrationDiameter;

    [ObservableProperty]
    private string _valveNo = string.Empty;

    [ObservableProperty]
    private decimal _valveNominalDiameter;

    [ObservableProperty]
    private decimal _leakageLimit;

    [ObservableProperty]
    private decimal _prechargePressureP2;

    /// <summary>
    /// 预充压压力 P2 的界面文本（单位 kPa，输入 ÷1000 存 MPa）。
    /// 数据库存 MPa；界面按千帕显示/输入。
    /// </summary>
    public string PrechargePressureP2Text
    {
        get => PrechargePressureP2 == 0 ? string.Empty : Helpers.PressureUnitConverter.ToDisplay(PrechargePressureP2).ToString("0.####");
        set
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                PrechargePressureP2 = 0;
                return;
            }

            if (decimal.TryParse(value.Trim(), out var kpa))
            {
                PrechargePressureP2 = Helpers.PressureUnitConverter.ToStorage(kpa);
            }
        }
    }

    partial void OnPrechargePressureP2Changed(decimal value)
    {
        OnPropertyChanged(nameof(PrechargePressureP2Text));
    }

    [ObservableProperty]
    private bool _isEnabled = true;

    [ObservableProperty]
    private int _sortOrder;

    [ObservableProperty]
    private string? _remark;

    /// <summary>
    /// 是否为编辑模式（false 为新增）
    /// </summary>
    public bool IsEditMode => Id > 0;

    public string Title => IsEditMode ? "编辑试验路径" : "新增试验路径";

    /// <summary>
    /// 从实体加载数据
    /// </summary>
    public void LoadFromEntity(TestRecipe recipe)
    {
        Id = recipe.Id;
        RecipeName = recipe.RecipeName;
        SequenceNo = recipe.SequenceNo;
        System = recipe.System;
        PenetrationDiameter = recipe.PenetrationDiameter;
        ValveNo = recipe.ValveNo;
        ValveNominalDiameter = recipe.ValveNominalDiameter;
        LeakageLimit = recipe.LeakageLimit;
        PrechargePressureP2 = recipe.PrechargePressureP2;
        IsEnabled = recipe.IsEnabled;
        SortOrder = recipe.SortOrder;
        Remark = recipe.Remark;
    }

    /// <summary>
    /// 转换为实体
    /// </summary>
    public TestRecipe ToEntity()
    {
        return new TestRecipe
        {
            Id = Id,
            RecipeName = RecipeName.Trim(),
            SequenceNo = SequenceNo,
            System = System.Trim(),
            PenetrationDiameter = PenetrationDiameter,
            ValveNo = ValveNo.Trim(),
            ValveNominalDiameter = ValveNominalDiameter,
            LeakageLimit = LeakageLimit,
            PrechargePressureP2 = PrechargePressureP2,
            IsEnabled = IsEnabled,
            SortOrder = SortOrder,
            Remark = Remark?.Trim()
        };
    }

    /// <summary>
    /// 验证数据
    /// </summary>
    public bool Validate()
    {
        if (string.IsNullOrWhiteSpace(RecipeName))
        {
            MessageBox.Show("试验路径名称不能为空", "验证失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        if (LeakageLimit < 0)
        {
            MessageBox.Show("泄漏率限值不能为负数", "验证失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        return true;
    }
}

/// <summary>
/// 配方列表项 ViewModel
/// </summary>
public sealed partial class RecipeItemViewModel : ObservableObject
{
    [ObservableProperty]
    private int _id;

    [ObservableProperty]
    private string _recipeName = string.Empty;

    [ObservableProperty]
    private int _sequenceNo;

    [ObservableProperty]
    private string _system = string.Empty;

    [ObservableProperty]
    private decimal _penetrationDiameter;

    [ObservableProperty]
    private string _valveNo = string.Empty;

    [ObservableProperty]
    private decimal _leakageLimit;

    [ObservableProperty]
    private decimal _prechargePressureP2;

    private bool _isEnabled;
    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (SetProperty(ref _isEnabled, value))
            {
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    [ObservableProperty]
    private int _sortOrder;

    [ObservableProperty]
    private DateTime _createdAt;

    public string StatusText => IsEnabled ? "启用" : "禁用";

    public static RecipeItemViewModel FromEntity(TestRecipe recipe)
    {
        return new RecipeItemViewModel
        {
            Id = recipe.Id,
            RecipeName = recipe.RecipeName,
            SequenceNo = recipe.SequenceNo,
            System = recipe.System,
            PenetrationDiameter = recipe.PenetrationDiameter,
            ValveNo = recipe.ValveNo,
            LeakageLimit = recipe.LeakageLimit,
            PrechargePressureP2 = recipe.PrechargePressureP2,
            IsEnabled = recipe.IsEnabled,
            SortOrder = recipe.SortOrder,
            CreatedAt = recipe.CreatedAt
        };
    }
}

/// <summary>
/// 配方管理主 ViewModel
/// </summary>
public sealed partial class RecipeManagementViewModel : ViewModelBase, IRefreshable
{
    [ObservableProperty]
    private ObservableCollection<RecipeItemViewModel> _recipes = [];

    [ObservableProperty]
    private RecipeItemViewModel? _selectedRecipe;

    [ObservableProperty]
    private string? _searchKeyword;

    [ObservableProperty]
    private bool _showOnlyEnabled = false;

    [ObservableProperty]
    private bool _isLoading;

    /// <summary>
    /// 刷新数据
    /// </summary>
    public async Task RefreshAsync()
    {
        IsLoading = true;
        try
        {
            var allRecipes = await AppServices.RecipeService.GetAllAsync();
            var filtered = allRecipes.AsEnumerable();

            // 过滤启用状态
            if (ShowOnlyEnabled)
            {
                filtered = filtered.Where(r => r.IsEnabled);
            }

            // 搜索过滤
            if (!string.IsNullOrWhiteSpace(SearchKeyword))
            {
                var keyword = SearchKeyword.Trim().ToLower();
                filtered = filtered.Where(r =>
                    (r.RecipeName?.ToLower().Contains(keyword) ?? false) ||
                    (r.System?.ToLower().Contains(keyword) ?? false) ||
                    (r.ValveNo?.ToLower().Contains(keyword) ?? false) ||
                    (r.Remark?.ToLower().Contains(keyword) ?? false));
            }

            Recipes = new ObservableCollection<RecipeItemViewModel>(
                filtered.Select(RecipeItemViewModel.FromEntity));
        }
        catch (Exception ex)
        {
            MessageBox.Show($"加载试验路径列表失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// 搜索命令
    /// </summary>
    public ICommand SearchCommand => new AsyncRelayCommand(RefreshAsync);

    /// <summary>
    /// 新增配方
    /// </summary>
    public ICommand AddRecipeCommand => new RelayCommand(() =>
    {
        var editVm = new RecipeEditViewModel
        {
            SortOrder = Recipes.Count + 1
        };

        var dialog = new Views.RecipeEditDialog(editVm);
        if (dialog.ShowDialog() == true)
        {
            _ = SaveRecipeAsync(editVm);
        }
    }, () => PermissionGuard.Can(Perms.RecipeEdit));

    /// <summary>
    /// 编辑配方（核心方法，可被其他命令调用）
    /// </summary>
    private async Task EditRecipeCoreAsync()
    {
        if (SelectedRecipe == null)
        {
            MessageBox.Show("请先选择要编辑的试验路径", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var recipe = await AppServices.RecipeService.GetByIdAsync(SelectedRecipe.Id);
        if (recipe == null)
        {
            MessageBox.Show("试验路径不存在或已被删除", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            await RefreshAsync();
            return;
        }

        var editVm = new RecipeEditViewModel();
        editVm.LoadFromEntity(recipe);

        var dialog = new Views.RecipeEditDialog(editVm);
        if (dialog.ShowDialog() == true)
        {
            await SaveRecipeAsync(editVm);
        }
    }

    /// <summary>
    /// 编辑配方
    /// </summary>
    public ICommand EditRecipeCommand => new RelayCommand(async () =>
    {
        await EditRecipeCoreAsync();
    }, () => PermissionGuard.Can(Perms.RecipeEdit));

    /// <summary>
    /// 删除配方
    /// </summary>
    public ICommand DeleteRecipeCommand => new RelayCommand(async () =>
    {
        if (SelectedRecipe == null)
        {
            MessageBox.Show("请先选择要删除的试验路径", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var result = MessageBox.Show(
            $"确定要删除试验路径「{SelectedRecipe.RecipeName}」吗？\n注意：如果有试验记录使用此试验路径，将仅禁用而不删除。",
            "确认删除",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes) return;

        try
        {
            var success = await AppServices.RecipeService.DeleteAsync(SelectedRecipe.Id);
            if (success)
            {
                await RefreshAsync();
            }
            else
            {
                MessageBox.Show("操作失败", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"删除失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }, () => PermissionGuard.Can(Perms.RecipeDelete));

    /// <summary>
    /// 导出配方CSV（按甲方配方组0.csv格式）
    /// </summary>
    public ICommand ExportCsvCommand => new RelayCommand(async () =>
    {
        try
        {
            var saveDialog = new SaveFileDialog
            {
                Filter = "CSV 文件 (*.csv)|*.csv|所有文件 (*.*)|*.*",
                FileName = $"试验路径_{DateTime.Now:yyyyMMdd}.csv",
                Title = "导出试验路径"
            };

            if (saveDialog.ShowDialog() != true) return;

            var csvContent = await AppServices.RecipeService.ExportToCsvAsync();
            var encoding = System.Text.Encoding.GetEncoding("GBK");
            await File.WriteAllTextAsync(saveDialog.FileName, csvContent, encoding);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"导出失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }, () => PermissionGuard.Can(Perms.RecipeEdit)); // 导出全部配方数据，与导入同级权限（此前无守卫）

    /// <summary>
    /// 导入配方CSV（支持甲方原始格式和扩展格式，基于表头自动识别列）
    /// </summary>
    public ICommand ImportCsvCommand => new RelayCommand(async () =>
    {
        try
        {
            var openDialog = new OpenFileDialog
            {
                Filter = "CSV 文件 (*.csv)|*.csv|所有文件 (*.*)|*.*",
                Title = "导入试验路径"
            };

            if (openDialog.ShowDialog() != true) return;

            // 探测编码：先按严格 UTF-8 解码，失败（典型为 GBK 中文内容）则回退 GBK。
            // 与 DataUploadService.DecodeBytes 同款策略，避免 GBK 文件被按 UTF-8 读出乱码表头导致整批导入失配。
            string csvContent;
            var bytes = await File.ReadAllBytesAsync(openDialog.FileName);
            try
            {
                var strictUtf8 = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
                csvContent = strictUtf8.GetString(bytes);
            }
            catch (System.Text.DecoderFallbackException)
            {
                // GBK（中文 Windows 默认，代码页 936）。需注册 CodePagesEncodingProvider。
                System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
                csvContent = System.Text.Encoding.GetEncoding(936).GetString(bytes);
            }

            // 先预览：解析后显示统计，让用户确认
            var operatorName = UserSession.Current?.User?.UserName;
            var result = await AppServices.RecipeService.ImportFromCsvAsync(csvContent, operatorName);

            // 构建详细结果信息
            var msgParts = new System.Collections.Generic.List<string>();
            if (result.Created > 0) msgParts.Add($"新建 {result.Created} 个试验路径");
            if (result.Updated > 0) msgParts.Add($"更新 {result.Updated} 个试验路径");
            if (result.Skipped > 0) msgParts.Add($"跳过 {result.Skipped} 行无效数据");

            string mainMsg = msgParts.Count > 0
                ? string.Join("，", msgParts)
                : "未导入任何数据";

            if (result.Errors.Count > 0)
            {
                // 最多显示10条警告
                var warnings = result.Errors.Take(10);
                string warningText = string.Join("\n", warnings);
                if (result.Errors.Count > 10)
                    warningText += $"\n……及其他 {result.Errors.Count - 10} 条";
                mainMsg += $"\n\n警告信息（{result.Errors.Count} 条）：\n{warningText}";
            }

            var icon = result.Errors.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning;
            MessageBox.Show(mainMsg, "导入结果", MessageBoxButton.OK, icon);

            if (result.TotalProcessed > 0)
                await RefreshAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"导入失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }, () => PermissionGuard.Can(Perms.RecipeEdit));

    /// <summary>
    /// 查看版本历史
    /// </summary>
    public ICommand ViewVersionHistoryCommand => new RelayCommand(async () =>
    {
        if (SelectedRecipe == null)
        {
            MessageBox.Show("请先选择试验路径", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var versions = await AppServices.RecipeService.GetVersionHistoryAsync(SelectedRecipe.Id);
        var message = string.Join("\n", versions.Select(v =>
            $"版本 {v.VersionNumber} - {v.CreatedAt:yyyy-MM-dd HH:mm:ss} - {v.CreatedBy ?? "系统"}\n{v.ChangeDescription}"));

        MessageBox.Show(message, $"试验路径 {SelectedRecipe.RecipeName} 版本历史", MessageBoxButton.OK, MessageBoxImage.Information);
    });

    /// <summary>
    /// 双击编辑
    /// </summary>
    public ICommand DoubleClickEditCommand => new RelayCommand(async () =>
    {
        if (SelectedRecipe != null)
        {
            await EditRecipeCoreAsync();
        }
    }, () => PermissionGuard.Can(Perms.RecipeEdit));

    /// <summary>
    /// 保存配方
    /// </summary>
    private async Task SaveRecipeAsync(RecipeEditViewModel editVm)
    {
        if (!editVm.Validate()) return;

        try
        {
            // 检查名称重复
            var nameExists = await AppServices.RecipeService.NameExistsAsync(
                editVm.RecipeName,
                editVm.IsEditMode ? editVm.Id : null);

            if (nameExists)
            {
                MessageBox.Show("试验路径名称已存在，请使用其他名称", "验证失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var operatorName = UserSession.Current?.User?.UserName;

            if (editVm.IsEditMode)
            {
                var entity = editVm.ToEntity();
                var success = await AppServices.RecipeService.UpdateAsync(entity, "参数修改", operatorName);
                if (success)
                {
                    await RefreshAsync();
                }
                else
                {
                    MessageBox.Show("更新失败，试验路径可能已被删除", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            else
            {
                var entity = editVm.ToEntity();
                await AppServices.RecipeService.CreateAsync(entity, "新建", operatorName);
                await RefreshAsync();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"保存失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
