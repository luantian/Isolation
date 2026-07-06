using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IsolationLeakage.App.Models.Database;
using IsolationLeakage.App.Services;

namespace IsolationLeakage.App.ViewModels;

/// <summary>
/// 批量上传视图模型
/// </summary>
public sealed partial class BatchUploadViewModel : ViewModelBase
{
    private readonly DataUploadService _dataUploadService;
    private string _selectedFolder = string.Empty;
    private bool _isParsing;
    private bool _isUploading;
    private int _uploadProgress;
    private string _uploadStatus = string.Empty;

    /// <summary>上传成功后触发（携带最后导入的项目/机组编码，父页面据此刷新并跳转路径树）</summary>
    public event EventHandler<ImportNavigateEventArgs>? UploadCompleted;

    // 命令字段（用于在属性变化时通知 CanExecute）
    private readonly RelayCommand _selectFolderCommand;
    private readonly AsyncRelayCommand _startUploadCommand;
    private readonly RelayCommand<Project> _setProjectForSelectedCommand;
    private readonly RelayCommand<Unit> _setUnitForSelectedCommand;
    private readonly RelayCommand<TestRecipe> _setRecipeForSelectedCommand;
    private readonly AsyncRelayCommand _reparseAllCommand;

    public BatchUploadViewModel()
    {
        _dataUploadService = new DataUploadService(AppServices.TestRecordService);
        ParsedItems = new ObservableCollection<ParsedPathInfo>();
        AvailableRecipes = new ObservableCollection<TestRecipe>();

        // 监听集合变化，更新统计数据
        ParsedItems.CollectionChanged += (s, e) => UpdateStatistics();

        // 在构造函数初始化命令
        _selectFolderCommand = new RelayCommand(ExecuteSelectFolder, () => !IsUploading);
        _startUploadCommand = new AsyncRelayCommand(ExecuteStartUploadAsync, () => CanStartUpload);
        _setProjectForSelectedCommand = new RelayCommand<Project>(ExecuteSetProjectForSelected, p => !IsUploading);
        _setUnitForSelectedCommand = new RelayCommand<Unit>(ExecuteSetUnitForSelected, u => !IsUploading);
        _setRecipeForSelectedCommand = new RelayCommand<TestRecipe>(ExecuteSetRecipeForSelected, r => !IsUploading);
        _reparseAllCommand = new AsyncRelayCommand(ExecuteReparseAllAsync, () => !IsUploading);

        // 初始化统计值
        UpdateStatistics();
    }

    /// <summary>
    /// 选中的文件夹路径
    /// </summary>
    public string SelectedFolder
    {
        get => _selectedFolder;
        private set => SetProperty(ref _selectedFolder, value);
    }

    /// <summary>
    /// 解析后的文件列表
    /// </summary>
    public ObservableCollection<ParsedPathInfo> ParsedItems { get; }

    /// <summary>
    /// 可选配方列表（用于批量设置）
    /// </summary>
    public ObservableCollection<TestRecipe> AvailableRecipes { get; }

    /// <summary>
    /// 是否正在解析中
    /// </summary>
    public bool IsParsing
    {
        get => _isParsing;
        private set
        {
            if (SetProperty(ref _isParsing, value))
            {
                // 状态变化时更新统计和命令状态
                UpdateStatistics();
                NotifyCommandCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// 是否正在上传中
    /// </summary>
    public bool IsUploading
    {
        get => _isUploading;
        private set
        {
            if (SetProperty(ref _isUploading, value))
            {
                // 状态变化时更新统计和命令状态
                UpdateStatistics();
                NotifyCommandCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// 通知所有命令重新检查 CanExecute
    /// </summary>
    private void NotifyCommandCanExecuteChanged()
    {
        _selectFolderCommand.NotifyCanExecuteChanged();
        _startUploadCommand.NotifyCanExecuteChanged();
        _setProjectForSelectedCommand.NotifyCanExecuteChanged();
        _setUnitForSelectedCommand.NotifyCanExecuteChanged();
        _setRecipeForSelectedCommand.NotifyCanExecuteChanged();
        _reparseAllCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// 上传进度百分比
    /// </summary>
    public int UploadProgress
    {
        get => _uploadProgress;
        private set => SetProperty(ref _uploadProgress, value);
    }

    /// <summary>
    /// 上传状态信息
    /// </summary>
    public string UploadStatus
    {
        get => _uploadStatus;
        private set => SetProperty(ref _uploadStatus, value);
    }

    // ✅ 改为带后台字段的属性，避免计算属性绑定问题
    private int _readyCount;
    private int _needAttentionCount;
    private int _totalCount;
    private bool _canStartUpload;

    /// <summary>
    /// 就绪文件数量
    /// </summary>
    public int ReadyCount
    {
        get => _readyCount;
        private set => SetProperty(ref _readyCount, value);
    }

    /// <summary>
    /// 待补充信息的文件数量
    /// </summary>
    public int NeedAttentionCount
    {
        get => _needAttentionCount;
        private set => SetProperty(ref _needAttentionCount, value);
    }

    /// <summary>
    /// 总文件数
    /// </summary>
    public int TotalCount
    {
        get => _totalCount;
        private set => SetProperty(ref _totalCount, value);
    }

    /// <summary>
    /// 可以开始上传
    /// </summary>
    public bool CanStartUpload
    {
        get => _canStartUpload;
        private set => SetProperty(ref _canStartUpload, value);
    }

    /// <summary>
    /// 更新统计数据
    /// </summary>
    private void UpdateStatistics()
    {
        ReadyCount = ParsedItems.Count(i => i.IsReady && !i.IsSkipped);
        NeedAttentionCount = ParsedItems.Count(i => !i.IsReady && !i.IsSkipped);
        TotalCount = ParsedItems.Count;
        CanStartUpload = !IsUploading && !IsParsing && ReadyCount > 0;
    }

    /// <summary>
    /// 选择文件夹命令（上传期间禁用）
    /// </summary>
    public ICommand SelectFolderCommand => _selectFolderCommand;

    /// <summary>
    /// 开始上传命令
    /// </summary>
    public ICommand StartUploadCommand => _startUploadCommand;

    /// <summary>
    /// 批量设置项目命令（上传期间禁用）
    /// </summary>
    public ICommand SetProjectForSelectedCommand => _setProjectForSelectedCommand;

    /// <summary>
    /// 批量设置机组命令（上传期间禁用）
    /// </summary>
    public ICommand SetUnitForSelectedCommand => _setUnitForSelectedCommand;

    /// <summary>
    /// 批量设置配方命令（上传期间禁用）
    /// </summary>
    public ICommand SetRecipeForSelectedCommand => _setRecipeForSelectedCommand;

    /// <summary>
    /// 重新解析所有文件命令（上传期间禁用）
    /// </summary>
    public ICommand ReparseAllCommand => _reparseAllCommand;

    /// <summary>
    /// 选择文件夹
    /// </summary>
    private void ExecuteSelectFolder()
    {
        var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "选择包含试验数据的根文件夹",
            ShowNewFolderButton = false
        };

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            SelectedFolder = dialog.SelectedPath;
            _ = ParseFolderAsync(dialog.SelectedPath);
        }
    }

    /// <summary>
    /// 解析文件夹
    /// </summary>
    private async Task ParseFolderAsync(string folderPath)
    {
        IsParsing = true;
        UploadStatus = "正在扫描文件夹...";
        ParsedItems.Clear();

        try
        {
            // 加载配方列表
            await LoadRecipesAsync();

            // 批量解析
            var items = await _dataUploadService.BatchParseFolderAsync(folderPath);

            foreach (var item in items)
            {
                ParsedItems.Add(item);
            }

            // 统计错误信息
            var failedCount = items.Count(i => !string.IsNullOrEmpty(i.ErrorMessage));
            if (failedCount > 0)
            {
                UploadStatus = $"共扫描到 {items.Count} 个文件，{ReadyCount} 个已就绪，{NeedAttentionCount} 个需补充信息，{failedCount} 个解析失败";

                // ✅ 只弹一个窗，汇总显示错误
                var errorFiles = items.Where(i => !string.IsNullOrEmpty(i.ErrorMessage)).Take(10).ToList();
                var errorSummary = string.Join("\n", errorFiles.Select(f => $"  • {f.FileName}: {f.ErrorMessage}"));
                if (failedCount > 10)
                    errorSummary += $"\n  ... 还有 {failedCount - 10} 个文件解析失败";

                MessageBox.Show(
                    $"解析完成！\n总计: {items.Count} 个\n就绪: {ReadyCount} 个\n需补充: {NeedAttentionCount} 个\n失败: {failedCount} 个\n\n失败详情:\n{errorSummary}",
                    "解析完成",
                    MessageBoxButton.OK,
                    failedCount > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
            }
            else
            {
                UploadStatus = $"共扫描到 {items.Count} 个文件，{ReadyCount} 个已就绪，{NeedAttentionCount} 个需补充信息";
            }
        }
        catch (Exception ex)
        {
            UploadStatus = $"解析失败: {ex.Message}";
            MessageBox.Show($"解析文件夹失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsParsing = false;
        }
    }

    /// <summary>
    /// 加载配方列表
    /// </summary>
    private async Task LoadRecipesAsync()
    {
        try
        {
            var recipes = await _dataUploadService.GetEnabledRecipesAsync();
            AvailableRecipes.Clear();
            foreach (var recipe in recipes)
            {
                AvailableRecipes.Add(recipe);
            }
        }
        catch
        {
            // 忽略加载失败
        }
    }

    /// <summary>
    /// 开始批量上传
    /// </summary>
    private async Task ExecuteStartUploadAsync()
    {
        if (!CanStartUpload) return;

        IsUploading = true;
        UploadProgress = 0;
        UploadStatus = "准备上传...";

        try
        {
            var operatorName = Services.Security.UserSession.Current?.User.UserName ?? "system";
            var progress = new Progress<BatchUploadProgress>(p =>
            {
                UploadProgress = p.Total > 0 ? p.Current * 100 / p.Total : 0;
                UploadStatus = $"正在上传: {p.CurrentFileName} ({p.Current}/{p.Total})";
            });

            var result = await _dataUploadService.BatchUploadAsync(
                ParsedItems.ToList(),
                operatorName,
                progress);

            UploadProgress = 100;
            UploadStatus = $"上传完成！成功 {result.SuccessCount} 个，失败 {result.FailedCount} 个";

            // 通知父页面刷新项目/机组下拉与路径树（无论成功失败都刷新，避免重复导入时下拉不更新）
            var lastItem = ParsedItems.LastOrDefault(i => !string.IsNullOrWhiteSpace(i.ParsedProjectCode));
            UploadCompleted?.Invoke(this, new ImportNavigateEventArgs
            {
                ProjectCode = lastItem?.ParsedProjectCode,
                UnitCode = lastItem?.ParsedUnitCode,
            });

            if (result.FailedCount > 0)
            {
                // 按错误类型分组统计
                var errorGroups = result.FailedItems
                    .Where(f => !string.IsNullOrEmpty(f.ErrorMessage))
                    .GroupBy(f =>
                    {
                        var msg = f.ErrorMessage;
                        if (msg.Contains("测量装置不存在"))
                            return "测量装置不存在";
                        if (msg.Contains("数据校验失败"))
                            return "数据校验失败";
                        if (msg.Contains("重复记录"))
                            return "重复记录";
                        if (msg.Contains("路径信息不完整"))
                            return "路径信息不完整";
                        return "其他错误";
                    })
                    .Select(g => new { Reason = g.Key, Count = g.Count(), Files = g.Take(5).Select(f => f.FileName).ToList() })
                    .OrderByDescending(g => g.Count)
                    .ToList();

                var errorSummary = string.Join("\n\n", errorGroups.Select(g =>
                {
                    var files = string.Join("\n    ", g.Files);
                    var more = g.Files.Count < g.Count ? $"\n    ... 还有 {g.Count - g.Files.Count} 个" : "";
                    return $"【{g.Reason}】{g.Count} 个\n    {files}{more}";
                }));

                MessageBox.Show(
                    $"上传完成！\n成功: {result.SuccessCount} 个\n失败: {result.FailedCount} 个\n\n失败原因汇总:\n{errorSummary}",
                    "上传完成",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            else
            {
                MessageBox.Show(
                    $"全部上传成功！共 {result.SuccessCount} 个文件",
                    "上传完成",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            UploadStatus = $"上传失败: {ex.Message}";
            MessageBox.Show($"批量上传失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsUploading = false;
            OnPropertyChanged(nameof(CanStartUpload));
        }
    }

    /// <summary>
    /// 批量设置选中项的项目
    /// </summary>
    private void ExecuteSetProjectForSelected(Project? project)
    {
        if (project == null) return;

        // 这里需要知道哪些是"选中的"项，简单起见先更新所有未匹配的
        foreach (var item in ParsedItems.Where(i => i.MatchedProject == null && !i.IsSkipped))
        {
            item.MatchedProject = project;
            item.IsReady = item.MatchedProject != null &&
                          item.MatchedUnit != null &&
                          item.MatchedObjectNode != null;
        }

        OnPropertyChanged(nameof(ReadyCount));
        OnPropertyChanged(nameof(NeedAttentionCount));
        OnPropertyChanged(nameof(CanStartUpload));
    }

    /// <summary>
    /// 批量设置选中项的机组
    /// </summary>
    private void ExecuteSetUnitForSelected(Unit? unit)
    {
        if (unit == null) return;

        foreach (var item in ParsedItems.Where(i => i.MatchedUnit == null && !i.IsSkipped))
        {
            item.MatchedUnit = unit;
            item.IsReady = item.MatchedProject != null &&
                          item.MatchedUnit != null &&
                          item.MatchedObjectNode != null;
        }

        OnPropertyChanged(nameof(ReadyCount));
        OnPropertyChanged(nameof(NeedAttentionCount));
        OnPropertyChanged(nameof(CanStartUpload));
    }

    /// <summary>
    /// 批量设置选中项的配方
    /// </summary>
    private void ExecuteSetRecipeForSelected(TestRecipe? recipe)
    {
        if (recipe == null) return;

        foreach (var item in ParsedItems.Where(i => i.IsReady && !i.IsSkipped))
        {
            item.SelectedRecipeId = recipe.Id;
        }
    }

    /// <summary>
    /// 重新解析所有文件
    /// </summary>
    private async Task ExecuteReparseAllAsync()
    {
        if (!string.IsNullOrEmpty(SelectedFolder))
        {
            await ParseFolderAsync(SelectedFolder);
        }
    }
}

/// <summary>
/// 导入完成后跳转事件参数（携带最后导入的项目/机组编码）
/// </summary>
public sealed class ImportNavigateEventArgs : EventArgs
{
    public string? ProjectCode { get; init; }
    public string? UnitCode { get; init; }
}
