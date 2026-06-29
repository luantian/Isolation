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
/// 配方编辑 ViewModel
/// </summary>
public sealed partial class RecipeEditViewModel : ObservableObject
{
    [ObservableProperty]
    private int _id;

    [ObservableProperty]
    private string _recipeCode = string.Empty;

    [ObservableProperty]
    private string _recipeName = string.Empty;

    [ObservableProperty]
    private string? _description;

    [ObservableProperty]
    private decimal _airtightTargetPressureP1;

    [ObservableProperty]
    private decimal _airtightAllowDropValue;

    [ObservableProperty]
    private decimal _fineBlowTargetPressureP1;

    [ObservableProperty]
    private decimal _purgeReleasePressure;

    [ObservableProperty]
    private decimal _normalExpectedLeakFlow;

    [ObservableProperty]
    private decimal _smallPrechargeTargetP1;

    [ObservableProperty]
    private decimal _smallPrechargeTargetP2;

    [ObservableProperty]
    private decimal _mediumPrechargeTargetP1;

    [ObservableProperty]
    private decimal _mediumPrechargeTargetP2;

    [ObservableProperty]
    private decimal _largePrechargeTargetP1;

    [ObservableProperty]
    private decimal _largePrechargeTargetP2;

    [ObservableProperty]
    private bool _isEnabled = true;

    [ObservableProperty]
    private int _sortOrder;

    /// <summary>
    /// 是否为编辑模式（false 为新增）
    /// </summary>
    public bool IsEditMode => Id > 0;

    public string Title => IsEditMode ? "编辑配方" : "新增配方";

    /// <summary>
    /// 从实体加载数据
    /// </summary>
    public void LoadFromEntity(TestRecipe recipe)
    {
        Id = recipe.Id;
        RecipeCode = recipe.RecipeCode;
        RecipeName = recipe.RecipeName;
        Description = recipe.Description;
        AirtightTargetPressureP1 = recipe.AirtightTargetPressureP1;
        AirtightAllowDropValue = recipe.AirtightAllowDropValue;
        FineBlowTargetPressureP1 = recipe.FineBlowTargetPressureP1;
        PurgeReleasePressure = recipe.PurgeReleasePressure;
        NormalExpectedLeakFlow = recipe.NormalExpectedLeakFlow;
        SmallPrechargeTargetP1 = recipe.SmallPrechargeTargetP1;
        SmallPrechargeTargetP2 = recipe.SmallPrechargeTargetP2;
        MediumPrechargeTargetP1 = recipe.MediumPrechargeTargetP1;
        MediumPrechargeTargetP2 = recipe.MediumPrechargeTargetP2;
        LargePrechargeTargetP1 = recipe.LargePrechargeTargetP1;
        LargePrechargeTargetP2 = recipe.LargePrechargeTargetP2;
        IsEnabled = recipe.IsEnabled;
        SortOrder = recipe.SortOrder;
    }

    /// <summary>
    /// 转换为实体
    /// </summary>
    public TestRecipe ToEntity()
    {
        return new TestRecipe
        {
            Id = Id,
            RecipeCode = RecipeCode.Trim(),
            RecipeName = RecipeName.Trim(),
            Description = Description?.Trim(),
            AirtightTargetPressureP1 = AirtightTargetPressureP1,
            AirtightAllowDropValue = AirtightAllowDropValue,
            FineBlowTargetPressureP1 = FineBlowTargetPressureP1,
            PurgeReleasePressure = PurgeReleasePressure,
            NormalExpectedLeakFlow = NormalExpectedLeakFlow,
            SmallPrechargeTargetP1 = SmallPrechargeTargetP1,
            SmallPrechargeTargetP2 = SmallPrechargeTargetP2,
            MediumPrechargeTargetP1 = MediumPrechargeTargetP1,
            MediumPrechargeTargetP2 = MediumPrechargeTargetP2,
            LargePrechargeTargetP1 = LargePrechargeTargetP1,
            LargePrechargeTargetP2 = LargePrechargeTargetP2,
            IsEnabled = IsEnabled,
            SortOrder = SortOrder
        };
    }

    /// <summary>
    /// 验证数据
    /// </summary>
    public bool Validate()
    {
        if (string.IsNullOrWhiteSpace(RecipeCode))
        {
            MessageBox.Show("配方编码不能为空", "验证失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        if (string.IsNullOrWhiteSpace(RecipeName))
        {
            MessageBox.Show("配方名称不能为空", "验证失败", MessageBoxButton.OK, MessageBoxImage.Warning);
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
    private string _recipeCode = string.Empty;

    [ObservableProperty]
    private string _recipeName = string.Empty;

    [ObservableProperty]
    private string? _description;

    [ObservableProperty]
    private decimal _airtightTargetPressureP1;

    [ObservableProperty]
    private decimal _fineBlowTargetPressureP1;

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

    public string StatusText => IsEnabled ? "启用" : "禁用";

    public static RecipeItemViewModel FromEntity(TestRecipe recipe)
    {
        return new RecipeItemViewModel
        {
            Id = recipe.Id,
            RecipeCode = recipe.RecipeCode,
            RecipeName = recipe.RecipeName,
            Description = recipe.Description,
            AirtightTargetPressureP1 = recipe.AirtightTargetPressureP1,
            FineBlowTargetPressureP1 = recipe.FineBlowTargetPressureP1,
            IsEnabled = recipe.IsEnabled,
            SortOrder = recipe.SortOrder
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
    private bool _showOnlyEnabled = true;

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
                    r.RecipeCode.ToLower().Contains(keyword) ||
                    r.RecipeName.ToLower().Contains(keyword) ||
                    (r.Description != null && r.Description.ToLower().Contains(keyword)));
            }

            Recipes = new ObservableCollection<RecipeItemViewModel>(
                filtered.Select(RecipeItemViewModel.FromEntity));
        }
        catch (Exception ex)
        {
            MessageBox.Show($"加载配方列表失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// 搜索命令
    /// </summary>
    public ICommand SearchCommand => new RelayCommand(async () => await RefreshAsync());

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
    });

    /// <summary>
    /// 编辑配方（核心方法，可被其他命令调用）
    /// </summary>
    private async Task EditRecipeCoreAsync()
    {
        if (SelectedRecipe == null)
        {
            MessageBox.Show("请先选择要编辑的配方", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var recipe = await AppServices.RecipeService.GetByIdAsync(SelectedRecipe.Id);
        if (recipe == null)
        {
            MessageBox.Show("配方不存在或已被删除", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
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
    });

    /// <summary>
    /// 删除配方
    /// </summary>
    public ICommand DeleteRecipeCommand => new RelayCommand(async () =>
    {
        if (SelectedRecipe == null)
        {
            MessageBox.Show("请先选择要删除的配方", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var result = MessageBox.Show(
            $"确定要删除配方「{SelectedRecipe.RecipeName}」吗？\n注意：如果有试验记录使用此配方，将仅禁用而不删除。",
            "确认删除",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes) return;

        try
        {
            var success = await AppServices.RecipeService.DeleteAsync(SelectedRecipe.Id);
            if (success)
            {
                MessageBox.Show("操作成功", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
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
    });

    /// <summary>
    /// 导出配方CSV
    /// </summary>
    public ICommand ExportCsvCommand => new RelayCommand(async () =>
    {
        try
        {
            var saveDialog = new SaveFileDialog
            {
                Filter = "CSV 文件 (*.csv)|*.csv|所有文件 (*.*)|*.*",
                FileName = $"配方组_{DateTime.Now:yyyyMMdd}.csv",
                Title = "导出配方"
            };

            if (saveDialog.ShowDialog() != true) return;

            var csvContent = await AppServices.RecipeService.ExportToCsvAsync();
            await File.WriteAllTextAsync(saveDialog.FileName, csvContent, System.Text.Encoding.UTF8);

            MessageBox.Show($"成功导出 {Recipes.Count} 个配方", "导出成功", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"导出失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    });

    /// <summary>
    /// 导入配方CSV
    /// </summary>
    public ICommand ImportCsvCommand => new RelayCommand(async () =>
    {
        try
        {
            var openDialog = new OpenFileDialog
            {
                Filter = "CSV 文件 (*.csv)|*.csv|所有文件 (*.*)|*.*",
                Title = "导入配方"
            };

            if (openDialog.ShowDialog() != true) return;

            var csvContent = await File.ReadAllTextAsync(openDialog.FileName, System.Text.Encoding.UTF8);
            var operatorName = UserSession.Current?.User?.UserName;

            var count = await AppServices.RecipeService.ImportFromCsvAsync(csvContent, operatorName);
            MessageBox.Show($"成功导入 {count} 个配方", "导入成功", MessageBoxButton.OK, MessageBoxImage.Information);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"导入失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    });

    /// <summary>
    /// 查看版本历史
    /// </summary>
    public ICommand ViewVersionHistoryCommand => new RelayCommand(async () =>
    {
        if (SelectedRecipe == null)
        {
            MessageBox.Show("请先选择配方", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var versions = await AppServices.RecipeService.GetVersionHistoryAsync(SelectedRecipe.Id);
        var message = string.Join("\n", versions.Select(v =>
            $"版本 {v.VersionNumber} - {v.CreatedAt:yyyy-MM-dd HH:mm} - {v.CreatedBy ?? "系统"}\n{v.ChangeDescription}"));

        MessageBox.Show(message, $"配方 {SelectedRecipe.RecipeName} 版本历史", MessageBoxButton.OK, MessageBoxImage.Information);
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
    });

    /// <summary>
    /// 保存配方
    /// </summary>
    private async Task SaveRecipeAsync(RecipeEditViewModel editVm)
    {
        if (!editVm.Validate()) return;

        try
        {
            // 检查编码重复
            var codeExists = await AppServices.RecipeService.CodeExistsAsync(
                editVm.RecipeCode,
                editVm.IsEditMode ? editVm.Id : null);

            if (codeExists)
            {
                MessageBox.Show("配方编码已存在，请使用其他编码", "验证失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var operatorName = UserSession.Current?.User?.UserName;

            if (editVm.IsEditMode)
            {
                var entity = editVm.ToEntity();
                var success = await AppServices.RecipeService.UpdateAsync(entity, "参数修改", operatorName);
                if (success)
                {
                    MessageBox.Show("更新成功", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                    await RefreshAsync();
                }
                else
                {
                    MessageBox.Show("更新失败，配方可能已被删除", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            else
            {
                var entity = editVm.ToEntity();
                await AppServices.RecipeService.CreateAsync(entity, "新建", operatorName);
                MessageBox.Show("创建成功", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                await RefreshAsync();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"保存失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
