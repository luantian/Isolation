using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IsolationLeakage.App.Models.Database;
using IsolationLeakage.App.Services;
using Microsoft.Win32;

namespace IsolationLeakage.App.ViewModels;

/// <summary>
/// 数据上传视图模型
/// </summary>
public sealed partial class DataUploadViewModel : ViewModelBase
{
    private readonly DataUploadService _dataUploadService;
    private string _selectedFilePath = string.Empty;
    private string _uploadStatus = "就绪";
    private string _uploadMessage = "请选择数据包文件进行上传";
    private bool _isUploading;
    private string _recordCode = string.Empty;
    private string _projectCode = string.Empty;
    private string _unitCode = string.Empty;
    private string _operatorName = string.Empty;

    // 配方相关
    private TestRecipe? _selectedRecipe;
    private string? _defaultRecipeSource;
    private bool _showRecipePanel;

    public DataUploadViewModel()
    {
        _dataUploadService = new DataUploadService(AppServices.TestRecordService);
        RecentUploads = new ObservableCollection<UploadHistoryItem>();
        AvailableRecipes = new ObservableCollection<TestRecipe>();

        SelectPackageCommand = new RelayCommand(ExecuteSelectPackage);
        UploadCommand = new AsyncRelayCommand(ExecuteUploadAsync, () => !IsUploading && !string.IsNullOrWhiteSpace(SelectedFilePath));
        ClearRecipeSelectionCommand = new RelayCommand(ExecuteClearRecipeSelection);
    }

    /// <summary>
    /// 选择数据包文件命令
    /// </summary>
    public ICommand SelectPackageCommand { get; }

    /// <summary>
    /// 上传命令
    /// </summary>
    public ICommand UploadCommand { get; }

    /// <summary>
    /// 选中的文件路径
    /// </summary>
    public string SelectedFilePath
    {
        get => _selectedFilePath;
        set
        {
            if (SetProperty(ref _selectedFilePath, value))
            {
                ((AsyncRelayCommand)UploadCommand).NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// 上传状态
    /// </summary>
    public string UploadStatus
    {
        get => _uploadStatus;
        set => SetProperty(ref _uploadStatus, value);
    }

    /// <summary>
    /// 上传消息
    /// </summary>
    public string UploadMessage
    {
        get => _uploadMessage;
        set => SetProperty(ref _uploadMessage, value);
    }

    /// <summary>
    /// 是否正在上传
    /// </summary>
    public bool IsUploading
    {
        get => _isUploading;
        set
        {
            if (SetProperty(ref _isUploading, value))
            {
                ((AsyncRelayCommand)UploadCommand).NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(CanUpload));
            }
        }
    }

    /// <summary>
    /// 是否可以上传
    /// </summary>
    public bool CanUpload => !IsUploading && !string.IsNullOrWhiteSpace(SelectedFilePath);

    /// <summary>
    /// 记录编号
    /// </summary>
    public string RecordCode
    {
        get => _recordCode;
        set => SetProperty(ref _recordCode, value);
    }

    /// <summary>
    /// 项目编码
    /// </summary>
    public string ProjectCode
    {
        get => _projectCode;
        set => SetProperty(ref _projectCode, value);
    }

    /// <summary>
    /// 机组编码
    /// </summary>
    public string UnitCode
    {
        get => _unitCode;
        set => SetProperty(ref _unitCode, value);
    }

    /// <summary>
    /// 操作员
    /// </summary>
    public string OperatorName
    {
        get => _operatorName;
        set => SetProperty(ref _operatorName, value);
    }

    /// <summary>
    /// 可选配方列表
    /// </summary>
    public ObservableCollection<TestRecipe> AvailableRecipes { get; }

    /// <summary>
    /// 用户选中的配方
    /// </summary>
    public TestRecipe? SelectedRecipe
    {
        get => _selectedRecipe;
        set
        {
            if (SetProperty(ref _selectedRecipe, value))
            {
                OnPropertyChanged(nameof(SelectedRecipeDisplay));
                OnPropertyChanged(nameof(IsRecipeSelected));
                OnPropertyChanged(nameof(IsRecipeFromDefault));
            }
        }
    }

    /// <summary>
    /// 默认配方来源说明
    /// </summary>
    public string? DefaultRecipeSource
    {
        get => _defaultRecipeSource;
        set => SetProperty(ref _defaultRecipeSource, value);
    }

    /// <summary>
    /// 是否显示配方选择面板
    /// </summary>
    public bool ShowRecipePanel
    {
        get => _showRecipePanel;
        set => SetProperty(ref _showRecipePanel, value);
    }

    /// <summary>
    /// 选中配方的显示文本
    /// </summary>
    public string SelectedRecipeDisplay
    {
        get
        {
            if (SelectedRecipe == null)
                return "未选择试验路径（将使用试验对象默认配置）";
            if (SelectedRecipe.Id == 0)
                return "（不使用试验路径）";
            return SelectedRecipe.RecipeName;
        }
    }

    /// <summary>
    /// 是否已选择配方
    /// </summary>
    public bool IsRecipeSelected => SelectedRecipe != null;

    /// <summary>
    /// 是否为默认配方
    /// </summary>
    public bool IsRecipeFromDefault => SelectedRecipe != null && SelectedRecipe.Id != 0 && DefaultRecipeSource != null;

    /// <summary>
    /// 清除配方选择命令
    /// </summary>
    public ICommand ClearRecipeSelectionCommand { get; }

    /// <summary>
    /// 预填充表单字段（由父页面传入当前项目/机组/操作人）
    /// </summary>
    public void PreFill(string? projectCode, string? unitCode, string? operatorName)
    {
        if (!string.IsNullOrWhiteSpace(projectCode) && string.IsNullOrWhiteSpace(ProjectCode))
            ProjectCode = projectCode;
        if (!string.IsNullOrWhiteSpace(unitCode) && string.IsNullOrWhiteSpace(UnitCode))
            UnitCode = unitCode;
        if (!string.IsNullOrWhiteSpace(operatorName) && string.IsNullOrWhiteSpace(OperatorName))
            OperatorName = operatorName;
    }

    /// <summary>
    /// 最近上传列表
    /// </summary>
    public ObservableCollection<UploadHistoryItem> RecentUploads { get; }

    #region Private Methods

    /// <summary>
    /// 执行选择文件
    /// </summary>
    private async void ExecuteSelectPackage()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "数据包文件 (*.csv;*.json;*.txt)|*.csv;*.json;*.txt|装置导出 CSV (*.csv)|*.csv|JSON 文件 (*.json)|*.json|文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
            Title = "选择数据包文件"
        };

        if (dialog.ShowDialog() == true)
        {
            SelectedFilePath = dialog.FileName;
            UploadMessage = $"已选择文件: {System.IO.Path.GetFileName(SelectedFilePath)}";

            // 异步加载配方列表和默认配方
            await LoadRecipeDataAsync();
        }
    }

    /// <summary>
    /// 加载配方数据（可选列表 + 默认配方）
    /// </summary>
    private async Task LoadRecipeDataAsync()
    {
        try
        {
            // 1. 加载所有启用的配方
            var recipes = await _dataUploadService.GetEnabledRecipesAsync();

            // 2. 添加"不使用试验路径"选项
            recipes.Insert(0, new TestRecipe { Id = 0, RecipeName = "不使用试验路径" });

            // 3. 更新列表
            AvailableRecipes.Clear();
            foreach (var recipe in recipes)
            {
                AvailableRecipes.Add(recipe);
            }

            // 4. 尝试解析数据包获取试验对象，然后查找默认配方
            if (!string.IsNullOrWhiteSpace(SelectedFilePath))
            {
                try
                {
                    var parsedData = await _dataUploadService.ParseDataPackageAsync(SelectedFilePath);
                    if (!string.IsNullOrWhiteSpace(parsedData.ObjectCode))
                    {
                        var defaultRecipe = await _dataUploadService.GetDefaultRecipeForObjectAsync(parsedData.ObjectCode);
                        if (defaultRecipe != null)
                        {
                            // 找到试验对象的默认配方，自动选中
                            SelectedRecipe = AvailableRecipes.FirstOrDefault(r => r.Id == defaultRecipe.Id);
                            DefaultRecipeSource = $"来自试验对象 [{parsedData.ObjectCode}] 的默认配置";
                        }
                        else
                        {
                            // 没有默认配方，提示用户选择
                            DefaultRecipeSource = null;
                            SelectedRecipe = null;
                        }
                    }
                }
                catch
                {
                    // 解析失败时不影响配方选择
                    DefaultRecipeSource = null;
                    SelectedRecipe = null;
                }
            }

            ShowRecipePanel = true;
        }
        catch
        {
            ShowRecipePanel = false;
        }
    }

    /// <summary>
    /// 清除配方选择
    /// </summary>
    private void ExecuteClearRecipeSelection()
    {
        SelectedRecipe = null;
        DefaultRecipeSource = null;
    }

    /// <summary>
    /// 执行上传
    /// </summary>
    private async Task ExecuteUploadAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedFilePath))
        {
            UploadStatus = "错误";
            UploadMessage = "请先选择数据包文件";
            return;
        }

        if (!ValidateInput())
        {
            return;
        }

        try
        {
            IsUploading = true;
            UploadStatus = "解析中...";
            UploadMessage = "正在解析数据包文件...";

            // 1. 解析数据包
            var parsedData = await _dataUploadService.ParseDataPackageAsync(SelectedFilePath);

            UploadStatus = "校验中...";
            UploadMessage = "正在校验数据...";

            // 2. 校验并上传（传递用户选择的配方）
            int? forceRecipeId = SelectedRecipe?.Id == 0 ? 0 : SelectedRecipe?.Id;
            var testRecord = await _dataUploadService.ValidateAndUploadAsync(
                parsedData,
                RecordCode.Trim(),
                ProjectCode.Trim(),
                UnitCode.Trim(),
                OperatorName.Trim(),
                forceRecipeId);

            // 3. 更新上传结果
            UploadStatus = "成功";
            UploadMessage = $"上传成功！记录编号: {testRecord.RecordCode}, 试验对象: {testRecord.ObjectCode}";

            // 4. 添加到最近上传列表
            AddToRecentUploads(testRecord);

            // 5. 清空表单
            ClearForm();
        }
        catch (FileNotFoundException ex)
        {
            UploadStatus = "错误";
            UploadMessage = $"文件不存在: {ex.Message}";
        }
        catch (FormatException ex)
        {
            UploadStatus = "格式错误";
            UploadMessage = $"数据格式错误: {ex.Message}";
        }
        catch (ArgumentException ex)
        {
            UploadStatus = "校验失败";
            UploadMessage = ex.Message;
        }
        catch (InvalidOperationException ex)
        {
            UploadStatus = "上传失败";
            UploadMessage = ex.Message;
        }
        catch (Exception ex)
        {
            UploadStatus = "异常";
            UploadMessage = $"上传过程中发生错误: {ex.Message}";
        }
        finally
        {
            IsUploading = false;
        }
    }

    /// <summary>
    /// 验证输入
    /// </summary>
    private bool ValidateInput()
    {
        if (string.IsNullOrWhiteSpace(RecordCode))
        {
            UploadStatus = "校验失败";
            UploadMessage = "记录编号不能为空";
            return false;
        }

        if (string.IsNullOrWhiteSpace(ProjectCode))
        {
            UploadStatus = "校验失败";
            UploadMessage = "项目编码不能为空";
            return false;
        }

        if (string.IsNullOrWhiteSpace(UnitCode))
        {
            UploadStatus = "校验失败";
            UploadMessage = "机组编码不能为空";
            return false;
        }

        if (string.IsNullOrWhiteSpace(OperatorName))
        {
            UploadStatus = "校验失败";
            UploadMessage = "操作员不能为空";
            return false;
        }

        return true;
    }

    /// <summary>
    /// 添加到最近上传列表
    /// </summary>
    private void AddToRecentUploads(TestRecord testRecord)
    {
        var item = new UploadHistoryItem
        {
            RecordCode = testRecord.RecordCode,
            ObjectCode = testRecord.ObjectCode,
            ProjectCode = testRecord.ProjectCode,
            UnitCode = testRecord.UnitCode,
            TestTime = testRecord.TestTime,
            ImportTime = testRecord.ImportTime,
            Result = testRecord.Result.ToString(),
            FilePath = SelectedFilePath
        };

        RecentUploads.Insert(0, item);

        // 最多保留 20 条记录
        if (RecentUploads.Count > 20)
        {
            RecentUploads.RemoveAt(RecentUploads.Count - 1);
        }
    }

    /// <summary>
    /// 清空表单
    /// </summary>
    private void ClearForm()
    {
        SelectedFilePath = string.Empty;
        RecordCode = string.Empty;
        ProjectCode = string.Empty;
        UnitCode = string.Empty;
        OperatorName = string.Empty;
        SelectedRecipe = null;
        DefaultRecipeSource = null;
        ShowRecipePanel = false;
        UploadMessage = "表单已清空，请选择新的数据包文件";
    }

    #endregion
}

/// <summary>
/// 上传历史记录项
/// </summary>
public sealed class UploadHistoryItem : ObservableObject
{
    private bool _isSelected;

    /// <summary>
    /// 记录编号
    /// </summary>
    public string RecordCode { get; init; } = string.Empty;

    /// <summary>
    /// 试验对象编码
    /// </summary>
    public string ObjectCode { get; init; } = string.Empty;

    /// <summary>
    /// 项目编码
    /// </summary>
    public string ProjectCode { get; init; } = string.Empty;

    /// <summary>
    /// 机组编码
    /// </summary>
    public string UnitCode { get; init; } = string.Empty;

    /// <summary>
    /// 试验时间
    /// </summary>
    public DateTime TestTime { get; init; }

    /// <summary>
    /// 导入时间
    /// </summary>
    public DateTime ImportTime { get; init; }

    /// <summary>
    /// 试验结果
    /// </summary>
    public string Result { get; init; } = string.Empty;

    /// <summary>
    /// 文件路径
    /// </summary>
    public string FilePath { get; init; } = string.Empty;

    /// <summary>
    /// 是否选中
    /// </summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    /// <summary>
    /// 显示用的试验结果文本
    /// </summary>
    public string ResultText => Result.ToLowerInvariant() switch
    {
        "pass" or "合格" => "合格",
        "fail" or "不合格" => "不合格",
        _ => "未知"
    };

    /// <summary>
    /// 显示用的时间文本
    /// </summary>
    public string TestTimeText => TestTime.ToString("yyyy-MM-dd HH:mm:ss");

    /// <summary>
    /// 显示用的导入时间文本
    /// </summary>
    public string ImportTimeText => ImportTime.ToString("yyyy-MM-dd HH:mm:ss");

    /// <summary>
    /// 文件名
    /// </summary>
    public string FileName => System.IO.Path.GetFileName(FilePath);
}
