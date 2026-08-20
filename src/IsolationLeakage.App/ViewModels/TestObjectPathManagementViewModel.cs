using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using IsolationLeakage.App.Data;
using IsolationLeakage.App.Models;
using IsolationLeakage.App.Models.Database;
using IsolationLeakage.App.Services;
using IsolationLeakage.App.Services.Security;
using IsolationLeakage.App.Views;
using Microsoft.EntityFrameworkCore;
using Microsoft.Win32;
using Serilog;

namespace IsolationLeakage.App.ViewModels;

/// <summary>
/// 试验对象树视图模型（简化版 - 仅统计概览，无完整历史列表）
/// </summary>
public sealed class TestObjectPathManagementViewModel : ViewModelBase, IRefreshable
{
    private int _componentSequence;
    private int _penetrationSequence;
    private string _searchText = string.Empty;
    private string _selectedProject = string.Empty;
    private string _selectedUnit = string.Empty;
    private int _systemSequence;
    private int _valveSequence;
    private TestObjectPathNode? _selectedNode;
    private string _message = string.Empty;
    private CancellationTokenSource? _loadStatsCts;  // 用于取消之前的统计数据加载
    // 路径树加载代际：快速切换项目/机组时，await 返回后只有仍是最新代际才写入，丢弃过期加载
    private int _pathTreeGeneration;
    private readonly RecipeService _recipeService = new();

    // 配方相关
    private ObservableCollection<TestRecipe> _availableRecipes = [];
    private TestRecipe? _selectedRecipeForNode;

    /// <summary>是否有消息需要显示</summary>
    public bool HasMessage => !string.IsNullOrWhiteSpace(_message);

    // 统计数据（轻量级）
    private int _totalTestCount;
    private int _passedTestCount;
    private int _failedTestCount;
    private int _unknownTestCount;
    private string _latestTestTime = "-";
    private string _latestLeakageRate = "-";
    private string _latestResult = "-";
    private string _latestDevice = "-";
    private string _passRate = "-";

    public TestObjectPathManagementViewModel()
    {
        Projects = new ObservableCollection<string>();
        Units = new ObservableCollection<string>();
        PathTree = new ObservableCollection<TestObjectPathNode>();

        // 子页面 ViewModel（任务下载面板已隐藏——现场未使用；保留绑定与实现，需要时
        // 去掉 XAML 的 Collapsed 并恢复 InitializeForSharedTreeAsync 即可）
        TaskDownloadPage = new TaskDownloadViewModel();
        // _ = TaskDownloadPage.InitializeForSharedTreeAsync();

        // 初始化命令（只创建一次实例）
        LocateCommand = new RelayCommand(() => _ = LocateFirstMatchAsync());
        CreateSystemCommand = new RelayCommand(() => _ = CreateNodeAsync(PathNodeType.System),
            () => IsolationLeakage.App.Services.Security.PermissionGuard.Can(IsolationLeakage.App.Services.Security.Perms.PathAdd));
        CreatePenetrationCommand = new RelayCommand(() => _ = CreateNodeAsync(PathNodeType.Penetration),
            () => IsolationLeakage.App.Services.Security.PermissionGuard.Can(IsolationLeakage.App.Services.Security.Perms.PathAdd));
        CreateValveCommand = new RelayCommand(() => _ = CreateNodeAsync(PathNodeType.Valve),
            () => IsolationLeakage.App.Services.Security.PermissionGuard.Can(IsolationLeakage.App.Services.Security.Perms.PathAdd));
        CreateOtherComponentCommand = new RelayCommand(() => _ = CreateNodeAsync(PathNodeType.OtherComponent),
            () => IsolationLeakage.App.Services.Security.PermissionGuard.Can(IsolationLeakage.App.Services.Security.Perms.PathAdd));
        EditNodeCommand = new RelayCommand(() => _ = EditSelectedNodeAsync(),
            () => HasSelectedNode && IsolationLeakage.App.Services.Security.PermissionGuard.Can(IsolationLeakage.App.Services.Security.Perms.PathAdd));
        DeleteNodeCommand = new RelayCommand(() => _ = DeleteSelectedNodeAsync(),
            () => CanDeleteNode && IsolationLeakage.App.Services.Security.PermissionGuard.Can(IsolationLeakage.App.Services.Security.Perms.PathDelete));

        _ = SafeLoadAsync();

        async Task SafeLoadAsync()
        {
            try
            {
                await LoadDataAsync();
                await LoadAvailableRecipesAsync();
            }
            catch (Exception ex)
            {
                Message = $"初始化加载失败：{ex.Message}";
            }
        }
    }

    public ObservableCollection<string> Projects { get; }
    public ObservableCollection<string> Units { get; }
    public ObservableCollection<TestObjectPathNode> PathTree { get; }

    /// <summary>可用配方列表（启用的配方）</summary>
    public ObservableCollection<TestRecipe> AvailableRecipes
    {
        get => _availableRecipes;
        private set => SetProperty(ref _availableRecipes, value);
    }

    /// <summary>当前选中节点关联的配方</summary>
    public TestRecipe? SelectedRecipeForNode
    {
        get => _selectedRecipeForNode;
        private set
        {
            if (SetProperty(ref _selectedRecipeForNode, value))
            {
                OnPropertyChanged(nameof(HasRecipe));
                OnPropertyChanged(nameof(RecipeNameText));
                OnPropertyChanged(nameof(RecipeSystemText));
                OnPropertyChanged(nameof(RecipeLeakageLimitText));
                OnPropertyChanged(nameof(RecipePrechargeP2Text));
                OnPropertyChanged(nameof(RecipeRemarkText));
            }
        }
    }

    /// <summary>是否有配方关联</summary>
    public bool HasRecipe => SelectedRecipeForNode != null;

    /// <summary>配方名称显示文本</summary>
    public string RecipeNameText => SelectedRecipeForNode?.RecipeName ?? "未关联试验路径";

    /// <summary>配方系统显示文本</summary>
    public string RecipeSystemText => SelectedRecipeForNode?.System ?? "-";

    /// <summary>配方泄漏率限值显示文本</summary>
    public string RecipeLeakageLimitText => SelectedRecipeForNode == null
        ? "-"
        : $"{SelectedRecipeForNode.LeakageLimit:F4}";

    /// <summary>配方预充压显示文本（库存 MPa，显示 kPa）</summary>
    public string RecipePrechargeP2Text => SelectedRecipeForNode == null
        ? "-"
        : $"{Helpers.PressureUnitConverter.ToDisplay(SelectedRecipeForNode.PrechargePressureP2):F1} kPa";

    /// <summary>配方备注显示文本</summary>
    public string RecipeRemarkText => SelectedRecipeForNode?.Remark ?? "无";

    /// <summary>任务下载子页面</summary>
    public TaskDownloadViewModel TaskDownloadPage { get; }

    /// <summary>累计试验次数</summary>
    public int TotalTestCount
    {
        get => _totalTestCount;
        private set => SetProperty(ref _totalTestCount, value);
    }

    /// <summary>合格次数</summary>
    public int PassedTestCount
    {
        get => _passedTestCount;
        private set => SetProperty(ref _passedTestCount, value);
    }

    /// <summary>不合格次数</summary>
    public int FailedTestCount
    {
        get => _failedTestCount;
        private set => SetProperty(ref _failedTestCount, value);
    }

    /// <summary>未判定次数</summary>
    public int UnknownTestCount
    {
        get => _unknownTestCount;
        private set => SetProperty(ref _unknownTestCount, value);
    }

    /// <summary>最近试验时间</summary>
    public string LatestTestTime
    {
        get => _latestTestTime;
        private set => SetProperty(ref _latestTestTime, value);
    }

    /// <summary>最近泄漏率</summary>
    public string LatestLeakageRate
    {
        get => _latestLeakageRate;
        private set => SetProperty(ref _latestLeakageRate, value);
    }

    /// <summary>最近判定结果</summary>
    public string LatestResult
    {
        get => _latestResult;
        private set => SetProperty(ref _latestResult, value);
    }

    /// <summary>最近结果颜色（合格=绿，不合格=红）</summary>
    public System.Windows.Media.Brush LatestResultBrush => _latestResult switch
    {
        "不合格" => System.Windows.Media.Brushes.Crimson,
        "合格" => System.Windows.Media.Brushes.ForestGreen,
        _ => System.Windows.Media.Brushes.Gray
    };

    /// <summary>最近导入装置</summary>
    public string LatestDevice
    {
        get => _latestDevice;
        private set => SetProperty(ref _latestDevice, value);
    }

    /// <summary>合格率</summary>
    public string PassRate
    {
        get => _passRate;
        private set => SetProperty(ref _passRate, value);
    }

    /// <summary>是否有历史数据（用于删除保护提示）</summary>
    public bool HasHistoricalData => TotalTestCount > 0;

    public string SelectedProject
    {
        get => _selectedProject;
        set
        {
            if (SetProperty(ref _selectedProject, value))
            {
                _ = RefreshUnitsAsync();
                _ = LoadPathTreeAsync();
            }
        }
    }

    public string SelectedUnit
    {
        get => _selectedUnit;
        set
        {
            if (SetProperty(ref _selectedUnit, value))
            {
                _ = LoadPathTreeAsync();
            }
        }
    }

    public string SearchText
    {
        get => _searchText;
        set => SetProperty(ref _searchText, value);
    }

    public string Message
    {
        get => _message;
        private set
        {
            if (SetProperty(ref _message, value))
                OnPropertyChanged(nameof(HasMessage));
        }
    }

    /// <summary>设置消息（带自动清除）</summary>
    private void SetMessage(string message, int type = 0)
    {
        // 取消之前的清除定时器
        _messageClearCts?.Cancel();
        _messageClearCts?.Dispose();

        Message = message;
        MessageType = type;

        // 3秒后自动清除
        if (!string.IsNullOrEmpty(message))
        {
            _messageClearCts = new CancellationTokenSource();
            _ = Task.Delay(3000, _messageClearCts.Token).ContinueWith(t =>
            {
                if (!t.IsCanceled)
                {
                    Message = string.Empty;
                    MessageType = 0;
                }
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
    }

    public TestObjectPathNode? SelectedNode
    {
        get => _selectedNode;
        set
        {
            if (_selectedNode == value) return;

            // 取消之前正在进行的统计数据加载
            _loadStatsCts?.Cancel();
            _loadStatsCts?.Dispose();
            _loadStatsCts = new CancellationTokenSource();

            _selectedNode = value;
            // 关键：先立即重置统计数据，避免用上一个节点的数据计算CanDeleteNode
            ResetStatistics();
            OnPropertyChanged();
            NotifySelectionChanged();
            _ = LoadSelectedNodeStatisticsAsync(_loadStatsCts.Token);
            _ = LoadRecipeInfoAsync();  // 加载关联的配方信息

            // 通知任务下载子页面更新选中节点
            TaskDownloadPage.SelectedNode = value;
        }
    }

    public string DetailTitle => SelectedNode == null ? "未选择路径" : $"{NodeTypeText}详情";

    public string AvailableCreateText => SelectedNode?.NodeType switch
    {
        PathNodeType.System => "贯穿件、阀门、其他密封性部件",
        PathNodeType.Penetration => "阀门、其他密封性部件",
        PathNodeType.Valve => "无",
        PathNodeType.OtherComponent => "无",
        _ => "系统、贯穿件、其他密封性部件"
    };

    public string NodeOperationDescription => SelectedNode?.NodeType switch
    {
        PathNodeType.System => "系统用于归集该系统下的试验对象",
        PathNodeType.Penetration => "贯穿件下可挂载阀门或其他密封性部件",
        PathNodeType.Valve => "阀门是试验对象叶子节点，不再继续挂载子节点",
        PathNodeType.OtherComponent => "其他密封性部件是试验对象叶子节点，不再继续挂载子节点",
        _ => "请先选择路径节点；阀门默认不直接挂在机组下"
    };

    public string NodeTypeText => SelectedNode?.NodeType switch
    {
        PathNodeType.System => "工艺系统",
        PathNodeType.Penetration => "贯穿件",
        PathNodeType.Valve => "阀门",
        PathNodeType.OtherComponent => "其他密封性部件",
        _ => "-"
    };

    public string LeakageLimitText => SelectedNode?.LeakageLimit == null ? "-" : $"{SelectedNode.LeakageLimit:0.###}";
    public string TestPressureText => SelectedNode?.TestPressure == null
        ? "-"
        : $"{Helpers.PressureUnitConverter.ToDisplay(SelectedNode.TestPressure.Value):0.#} kPa";

    public bool CanCreateSystem => true;
    public bool CanCreatePenetration => SelectedNode == null || SelectedNode.NodeType == PathNodeType.System;
    public bool CanCreateValve => SelectedNode?.NodeType is PathNodeType.System or PathNodeType.Penetration;
    public bool CanCreateOtherComponent => SelectedNode == null || SelectedNode.NodeType is PathNodeType.System or PathNodeType.Penetration;

    /// <summary>可新建子节点类型的合并文本（用于详情面板单行显示）</summary>
    public string CreatableNodeTypesText
    {
        get
        {
            var types = new List<string>();
            if (CanCreatePenetration) types.Add("贯穿件");
            if (CanCreateValve) types.Add("阀门");
            if (CanCreateOtherComponent) types.Add("其他部件");
            return types.Count > 0 ? string.Join("、", types) : "无";
        }
    }

    public IRelayCommand LocateCommand { get; }
    public IRelayCommand CreateSystemCommand { get; }
    public IRelayCommand CreatePenetrationCommand { get; }
    public IRelayCommand CreateValveCommand { get; }
    public IRelayCommand CreateOtherComponentCommand { get; }
    public IRelayCommand EditNodeCommand { get; }
    public RelayCommand DeleteNodeCommand { get; }

    /// <summary>选中节点是否为叶子节点（阀门/其他部件），用于控制导入/导出按钮</summary>
    public bool IsLeafNodeSelected => SelectedNode?.NodeType is PathNodeType.Valve or PathNodeType.OtherComponent;

    /// <summary>导入数据命令</summary>
    public IRelayCommand ImportDataCommand => _importDataCommand ??= new RelayCommand(
        () => _ = ImportDataAsync(),
        () => IsLeafNodeSelected && PermissionGuard.Can(Perms.RecordsUpload));
    private IRelayCommand? _importDataCommand;

    /// <summary>按文档导入命令：选择实验报表 CSV，逐行生成试验记录并按"系统→阀门"自动建路径</summary>
    public IRelayCommand ImportDocumentCommand => _importDocumentCommand ??= new RelayCommand(
        () => _ = ImportByDocumentAsync(),
        () => !IsImporting && PermissionGuard.Can(Perms.RecordsUpload));
    private IRelayCommand? _importDocumentCommand;

    /// <summary>导出数据命令</summary>
    public IRelayCommand ExportDataCommand => _exportDataCommand ??= new RelayCommand(
        () => _ = ExportDataAsync(),
        () => IsLeafNodeSelected && PermissionGuard.Can(Perms.ReportExport));
    private IRelayCommand? _exportDataCommand;

    public bool HasSelectedNode => SelectedNode != null;
    public bool HasNoSelection => SelectedNode == null;
    private bool _hasChildren;
    /// <summary>当前选中节点是否有子节点</summary>
    public bool HasChildren
    {
        get => _hasChildren;
        private set => SetProperty(ref _hasChildren, value);
    }
    private bool _hasDescendantRecords;
    /// <summary>当前节点或其子节点是否有历史试验记录（用于删除保护）</summary>
    public bool HasDescendantRecords
    {
        get => _hasDescendantRecords;
        private set => SetProperty(ref _hasDescendantRecords, value);
    }
    // 有子节点的节点仍不允许直接删（需先删子节点）；有历史记录的允许删除，
    // 但点击时会弹确认框提示将一并删除记录（见 DeleteSelectedNodeAsync）。
    public bool CanDeleteNode => SelectedNode != null && !HasChildren;

    /// <summary>是否正在导入数据（用于显示进度条+禁用按钮）</summary>
    private bool _isImporting;
    public bool IsImporting
    {
        get => _isImporting;
        private set => SetProperty(ref _isImporting, value);
    }

    /// <summary>删除按钮的禁用提示（鼠标悬停时显示）</summary>
    public string DeleteButtonToolTip =>
        HasChildren ? "该节点下有子节点，请先删除子节点" :
        HasDescendantRecords ? "该节点已有历史试验记录，删除时将一并删除这些记录" :
        "删除该节点";

    /// <summary>消息类型：0=普通，1=成功，2=错误</summary>
    private int _messageType;
    public int MessageType
    {
        get => _messageType;
        private set => SetProperty(ref _messageType, value);
    }

    // 用于自动清除消息的定时器
    private CancellationTokenSource? _messageClearCts;

    /// <summary>阀门类型 / 部件类型字段的值文本</summary>
    public string SelectedNodeTypeValue => SelectedNode?.NodeType switch
    {
        PathNodeType.Valve => SelectedNode.ValveType ?? "-",
        PathNodeType.OtherComponent => SelectedNode.ComponentType ?? "-",
        _ => "-"
    };

    /// <summary>阀门类型 / 部件类型的字段标签</summary>
    public string TypeFieldLabel => SelectedNode?.NodeType switch
    {
        PathNodeType.Valve => "阀门类型",
        PathNodeType.OtherComponent => "部件类型",
        _ => "类型"
    };

    /// <summary>是否有类型字段（阀门 / 其他部件才显示）</summary>
    public bool HasTypeField => SelectedNode?.NodeType is PathNodeType.Valve or PathNodeType.OtherComponent;

    /// <summary>是否有泄漏率限值</summary>
    public bool HasLeakageLimit => SelectedNode?.LeakageLimit != null;

    /// <summary>是否有试验压力</summary>
    public bool HasTestPressure => SelectedNode?.TestPressure != null;

    /// <summary>父节点显示文字</summary>
    public string ParentNodeDisplay
    {
        get
        {
            if (SelectedNode == null) return "-";
            if (SelectedNode.NodeType == PathNodeType.System) return "机组根路径";
            if (SelectedNode.ParentCode == null) return "机组根路径";
            var parent = Flatten(PathTree).FirstOrDefault(n => n.Code == SelectedNode.ParentCode);
            return parent?.DisplayName ?? $"[{SelectedNode.ParentCode}]";
        }
    }

    /// <summary>是否有试验数据</summary>
    public bool HasTestData => TotalTestCount > 0;

    /// <summary>是否无试验数据</summary>
    public bool HasNoTestData => TotalTestCount == 0;

    /// <summary>加载选中节点的试验统计数据（轻量级）</summary>
    private async Task LoadSelectedNodeStatisticsAsync(CancellationToken cancellationToken)
    {
        var currentNode = SelectedNode;
        if (currentNode == null)
        {
            // 选中节点为空时，才重置统计数据
            ResetStatistics();
            return;
        }

        var nodeCode = currentNode.Code;

        try
        {
            // 注意：不能用 Task.WhenAll 并行查询，同一个 DbContext 不能同时开多个 DataReader
            using var context = DbContextFactory.CreateDbContext();

            // 1. 查询当前节点的历史记录（顺序执行）
            var records = await context.TestRecords
                .Where(r => r.ObjectCode == nodeCode)
                .OrderByDescending(r => r.TestTime)
                .ToListAsync(cancellationToken);

            // 检查是否已切换到其他节点，如果是则不更新
            if (SelectedNode?.Code != nodeCode || cancellationToken.IsCancellationRequested)
                return;

            // 2. 查询是否有直接子节点（顺序执行）
            HasChildren = await context.TestObjectPathNodes
                .AnyAsync(n => n.ParentCode == nodeCode, cancellationToken);

            if (SelectedNode?.Code != nodeCode || cancellationToken.IsCancellationRequested)
                return;

            // 3. 递归查询当前节点及其所有子节点是否有历史记录（顺序执行）
            HasDescendantRecords = await CheckNodeAndDescendantsHaveRecordsAsync(context, nodeCode, cancellationToken);

            // 一次性更新所有统计数据
            TotalTestCount = records.Count;
            PassedTestCount = records.Count(r => r.Result == TestResult.Pass);
            FailedTestCount = records.Count(r => r.Result == TestResult.Fail);
            UnknownTestCount = records.Count(r => r.Result == TestResult.Unknown);

            if (TotalTestCount > 0)
            {
                var latest = records.First();
                LatestTestTime = latest.TestTime.ToString("yyyy-MM-dd HH:mm:ss");
                LatestLeakageRate = $"{latest.FinalLeakageRate:0.###}";
                LatestResult = latest.Result switch
                {
                    TestResult.Pass => "合格",
                    TestResult.Fail => "不合格",
                    _ => "未判定"
                };
                LatestDevice = latest.DeviceCode;
                // 合格率只计算已判定的记录（排除 Unknown）
                var judgedCount = PassedTestCount + FailedTestCount;
                PassRate = judgedCount > 0
                    ? $"{(decimal)PassedTestCount / judgedCount * 100:0.0}%"
                    : "-";
            }
            else
            {
                LatestTestTime = "-";
                LatestLeakageRate = "-";
                LatestResult = "-";
                LatestDevice = "-";
                PassRate = "-";
            }

            // 只通知一次属性变更
            NotifyStatisticsChanged();

            // 通知命令的 CanExecute 重新评估
            EditNodeCommand.NotifyCanExecuteChanged();
            DeleteNodeCommand.NotifyCanExecuteChanged();
            _importDataCommand?.NotifyCanExecuteChanged();
            _exportDataCommand?.NotifyCanExecuteChanged();

            // 选中节点时只做中性信息提示，不再弹“禁止删除”这类告警——
            // 删除是否允许、以及删除会连带删记录，统一在点击删除时用确认弹窗告知。
            if (records.Count > 0)
            {
                SetMessage($"已加载该对象的统计数据，累计 {records.Count} 条试验记录", 0);
            }
            else
            {
                SetMessage("该对象暂无历史试验记录", 0);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常取消，不报错
        }
        catch (Exception ex)
        {
            // 只有当前节点仍然匹配时才显示错误
            if (SelectedNode?.Code == nodeCode && !cancellationToken.IsCancellationRequested)
            {
                Message = $"加载统计数据失败：{ex.Message}";
            }
        }
    }

    /// <summary>递归检查节点及其所有子节点是否有历史试验记录</summary>
    private async Task<bool> CheckNodeAndDescendantsHaveRecordsAsync(AppDbContext context, string nodeCode, CancellationToken cancellationToken)
    {
        // 检查当前节点是否有历史记录
        var hasCurrentRecord = await context.TestRecords.AnyAsync(r => r.ObjectCode == nodeCode, cancellationToken);
        if (hasCurrentRecord)
            return true;

        // 获取所有子节点编码
        var childCodes = await context.TestObjectPathNodes
            .Where(n => n.ParentCode == nodeCode)
            .Select(n => n.Code)
            .ToListAsync(cancellationToken);

        // 递归检查每个子节点
        foreach (var childCode in childCodes)
        {
            if (await CheckNodeAndDescendantsHaveRecordsAsync(context, childCode, cancellationToken))
                return true;
        }

        return false;
    }

    /// <summary>重置统计数据（不频繁触发）</summary>
    private void ResetStatistics()
    {
        _hasChildren = false;
        _hasDescendantRecords = false;
        _totalTestCount = 0;
        _passedTestCount = 0;
        _failedTestCount = 0;
        _latestTestTime = "-";
        _latestLeakageRate = "-";
        _latestResult = "-";
        _latestDevice = "-";
        _passRate = "-";
        NotifyStatisticsChanged();
    }

    /// <summary>统一通知统计数据相关属性变更</summary>
    private void NotifyStatisticsChanged()
    {
        OnPropertyChanged(nameof(TotalTestCount));
        OnPropertyChanged(nameof(PassedTestCount));
        OnPropertyChanged(nameof(FailedTestCount));
        OnPropertyChanged(nameof(UnknownTestCount));
        OnPropertyChanged(nameof(LatestTestTime));
        OnPropertyChanged(nameof(LatestLeakageRate));
        OnPropertyChanged(nameof(LatestResult));
        OnPropertyChanged(nameof(LatestResultBrush));
        OnPropertyChanged(nameof(LatestDevice));
        OnPropertyChanged(nameof(PassRate));
        OnPropertyChanged(nameof(HasHistoricalData));
        OnPropertyChanged(nameof(HasTestData));
        OnPropertyChanged(nameof(HasNoTestData));
        OnPropertyChanged(nameof(HasChildren));
        OnPropertyChanged(nameof(HasDescendantRecords));
        // 关键：通知依赖属性更新
        OnPropertyChanged(nameof(CanDeleteNode));
        OnPropertyChanged(nameof(DeleteButtonToolTip));
    }

    Task IRefreshable.RefreshAsync() => LoadDataAsync();

    private async Task LoadDataAsync()
    {
        try
        {
            using var context = DbContextFactory.CreateDbContext();

            var projects = await context.Projects
                .Where(p => p.Status == EnabledStatus.Enabled)
                .Select(p => p.Name)
                .ToListAsync();

            Projects.Clear();
            foreach (var p in projects) Projects.Add(p);

            if (Projects.Any())
            {
                SelectedProject = Projects.First();
                await RefreshUnitsAsync();
            }
            else
            {
                // 关键修复：项目为空时，必须清空机组列表
                Units.Clear();
                SelectedUnit = string.Empty;
            }
        }
        catch (Exception ex)
        {
            Message = $"加载数据失败：{ex.Message}";
        }
    }

    /// <summary>加载所有启用的配方列表</summary>
    private async Task LoadAvailableRecipesAsync()
    {
        try
        {
            var recipes = await _recipeService.GetAllEnabledAsync();
            AvailableRecipes = new ObservableCollection<TestRecipe>(recipes);
        }
        catch (Exception ex)
        {
            Message = $"加载试验路径列表失败：{ex.Message}";
        }
    }

    /// <summary>加载选中节点关联的配方信息</summary>
    private async Task LoadRecipeInfoAsync()
    {
        if (SelectedNode == null)
        {
            SelectedRecipeForNode = null;
            return;
        }

        try
        {
            if (SelectedNode.DefaultRecipeId.HasValue && SelectedNode.DefaultRecipeId.Value > 0)
            {
                var recipe = await _recipeService.GetByIdAsync(SelectedNode.DefaultRecipeId.Value);
                SelectedRecipeForNode = recipe;
            }
            else
            {
                SelectedRecipeForNode = null;
            }
        }
        catch (Exception ex)
        {
            Message = $"加载试验路径信息失败：{ex.Message}";
        }
    }

    private async Task RefreshUnitsAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedProject)) return;
        try
        {
            using var context = DbContextFactory.CreateDbContext();
            var project = await context.Projects.FirstOrDefaultAsync(p => p.Name == SelectedProject);
            if (project != null)
            {
                var units = await context.Units
                    .Where(u => u.ProjectCode == project.Code && u.Status == EnabledStatus.Enabled)
                    .Select(u => u.Name)
                    .ToListAsync();

                Units.Clear();
                foreach (var u in units) Units.Add(u);

                SelectedUnit = Units.FirstOrDefault() ?? string.Empty;
            }
        }
        catch (Exception ex)
        {
            Message = $"加载机组失败：{ex.Message}";
        }
    }

    private async Task LoadPathTreeAsync()
    {
        var gen = ++_pathTreeGeneration;

        if (string.IsNullOrWhiteSpace(SelectedProject) || string.IsNullOrWhiteSpace(SelectedUnit))
        {
            PathTree.Clear();
            SelectedNode = null;
            return;
        }

        try
        {
            using var context = DbContextFactory.CreateDbContext();
            // 机组名在跨项目时可能重名，必须按当前所选项目的编号过滤，避免取到别的项目的同名机组
            var project = await context.Projects.FirstOrDefaultAsync(p => p.Name == SelectedProject);
            var unit = await context.Units.FirstOrDefaultAsync(u => u.Name == SelectedUnit
                && (project == null || u.ProjectCode == project.Code));
            if (unit == null) return;

            var rootNodes = await context.TestObjectPathNodes
                .Where(n => n.UnitCode == unit.Code && n.ParentCode == null)
                .Include(n => n.Children)
                .ThenInclude(c => c.Children)
                .OrderBy(n => n.Code)
                .ToListAsync();

            // 已有更新的加载发起（用户又切了项目/机组），丢弃本次陈旧结果
            if (gen != _pathTreeGeneration) return;

            PathTree.Clear();
            foreach (var node in rootNodes) PathTree.Add(node);

            SelectedNode = PathTree.FirstOrDefault();
            Message = $"已加载 {rootNodes.Count} 个路径根节点";

            _systemSequence = rootNodes.Count(n => n.NodeType == PathNodeType.System) + 1;
            _penetrationSequence = rootNodes.SelectMany(n => Flatten(new[] { n })).Count(n => n.NodeType == PathNodeType.Penetration) + 1;
            _valveSequence = rootNodes.SelectMany(n => Flatten(new[] { n })).Count(n => n.NodeType == PathNodeType.Valve) + 1;
            _componentSequence = rootNodes.SelectMany(n => Flatten(new[] { n })).Count(n => n.NodeType == PathNodeType.OtherComponent) + 1;
        }
        catch (Exception ex)
        {
            Message = $"加载试验对象树失败：{ex.Message}";
        }
    }

    private async Task CreateNodeAsync(PathNodeType nodeType)
    {
        if (!CanCreateNode(nodeType))
        {
            Message = $"无法在当前节点下创建{GetNodeTypeName(nodeType)}";
            return;
        }

        if (string.IsNullOrWhiteSpace(SelectedUnit))
        {
            SetMessage("请先选择机组", 2);
            return;
        }

        try
        {
            using var context = DbContextFactory.CreateDbContext();
            var logService = new OperationLogService(context);
            var currentUser = Services.Security.UserSession.Current?.User.UserName ?? "system";

            // 机组名在跨项目时可能重名，必须按当前所选项目的编号过滤，避免取到别的项目的同名机组
            var project = await context.Projects.FirstOrDefaultAsync(p => p.Name == SelectedProject);
            var unit = await context.Units.FirstOrDefaultAsync(u => u.Name == SelectedUnit
                && (project == null || u.ProjectCode == project.Code));
            if (unit == null) return;

            // 每次创建前都从数据库查询最新序列号，避免多实例并发产生重复编码
            var code = await GenerateUniqueCodeAsync(context, nodeType);

            // 弹出编辑对话框
            var dialog = new PathNodeEditorDialog(nodeType, code, SelectedNode)
            {
                Owner = Application.Current.MainWindow
            };

            if (dialog.ShowDialog() != true || dialog.ResultNode == null)
            {
                return;
            }

            var newNode = dialog.ResultNode;
            newNode.UnitCode = unit.Code;
            newNode.ParentCode = nodeType == PathNodeType.System || SelectedNode == null ? null : SelectedNode.Code;
            newNode.Status = EnabledStatus.Enabled;
            newNode.CreatedAt = DateTime.Now;

            context.TestObjectPathNodes.Add(newNode);
            await context.SaveChangesAsync();

            // 记录操作日志
            await logService.LogAsync("创建路径节点", currentUser,
                $"新增{GetNodeTypeName(nodeType)}【{newNode.DisplayName}】", "Success");

            // 直接添加到内存树，不重建整棵树
            AddNodeToTree(newNode);

            // 选中刚创建的节点
            var createdNode = Flatten(PathTree).FirstOrDefault(n => n.Code == code);
            if (createdNode != null)
            {
                SelectedNode = createdNode;
            }

            // 同步更新内存计数器
            IncrementSequence(nodeType);
            Message = $"✅ 已创建并保存到数据库：{newNode.DisplayName}";
        }
        catch (Exception ex)
        {
            Message = $"❌ 创建失败：{ex.Message}";
        }
    }

    /// <summary>将新节点添加到内存树中（不重建整棵树，保留展开状态）</summary>
    private void AddNodeToTree(TestObjectPathNode newNode)
    {
        if (string.IsNullOrEmpty(newNode.ParentCode))
        {
            // 根节点
            PathTree.Add(newNode);
        }
        else
        {
            // 找到父节点并添加到其 Children（ObservableCollection 自动通知 WPF 更新 HasItems）
            var parent = Flatten(PathTree).FirstOrDefault(n => n.Code == newNode.ParentCode);
            parent?.Children.Add(newNode);
        }
    }

    private async Task EditSelectedNodeAsync()
    {
        if (SelectedNode == null)
        {
            SetMessage("请先选择要修改的节点", 2);
            return;
        }

        try
        {
            // 弹出编辑对话框，复用现有节点数据
            var dialog = new PathNodeEditorDialog(SelectedNode.NodeType, SelectedNode.Code, SelectedNode.Parent)
            {
                Owner = Application.Current.MainWindow,
                Code = SelectedNode.Code,
                NodeName = SelectedNode.Name,
                SelectedType = SelectedNode.NodeType == PathNodeType.Valve ? SelectedNode.ValveType : SelectedNode.ComponentType,
                LeakageLimitText = SelectedNode.LeakageLimit?.ToString() ?? string.Empty,
                // 库存 MPa → 界面 kPa（对话框保存时 ÷1000 回存，回填必须 ×1000，否则每次编辑压力缩小 1000 倍）
                TestPressureText = SelectedNode.TestPressure.HasValue
                    ? Helpers.PressureUnitConverter.ToDisplay(SelectedNode.TestPressure.Value).ToString("0.####")
                    : string.Empty,
                SelectedRecipeId = SelectedNode.DefaultRecipeId ?? 0,
                Remark = SelectedNode.Remark ?? string.Empty
            };

            if (dialog.ShowDialog() != true || dialog.ResultNode == null)
            {
                return;
            }

            var updated = dialog.ResultNode;
            var oldName = SelectedNode.Name;

            // 直接更新内存中节点的属性（不重建树，保留展开状态）
            var editedCode = SelectedNode.Code;
            SelectedNode.Name = updated.Name.Trim();
            SelectedNode.ValveType = updated.ValveType;
            SelectedNode.ComponentType = updated.ComponentType;
            SelectedNode.LeakageLimit = updated.LeakageLimit;
            SelectedNode.TestPressure = updated.TestPressure;
            SelectedNode.DefaultRecipeId = updated.DefaultRecipeId;
            SelectedNode.Remark = updated.Remark?.Trim();
            SelectedNode.UpdatedAt = DateTime.Now;

            // 通知 UI 刷新 DisplayName（编号+名称组合显示）
            OnPropertyChanged(nameof(SelectedNode.DisplayName));

            // 同步到数据库
            using var context = DbContextFactory.CreateDbContext();
            var logService = new OperationLogService(context);
            var currentUser = Services.Security.UserSession.Current?.User.UserName ?? "system";

            // 只按 Code 取出目标节点并更新其字段，避免 Update(SelectedNode) 沿导航属性
            // （Parent/Children 已被 Include 填充）级联把整棵子树标记为 Modified、用内存旧值覆盖库中数据。
            var dbNode = await context.TestObjectPathNodes.FirstOrDefaultAsync(n => n.Code == editedCode);
            if (dbNode == null)
            {
                Message = "❌ 更新失败：节点在数据库中不存在";
                return;
            }

            // 捕获修改前的限值/默认配方（用于判断"有效限值是否真的变了"——没变就不动历史记录）
            var oldNodeLimit = dbNode.LeakageLimit;
            var oldNodeRecipeId = dbNode.DefaultRecipeId;

            dbNode.Name = SelectedNode.Name;
            dbNode.ValveType = SelectedNode.ValveType;
            dbNode.ComponentType = SelectedNode.ComponentType;
            dbNode.LeakageLimit = SelectedNode.LeakageLimit;
            dbNode.TestPressure = SelectedNode.TestPressure;
            dbNode.DefaultRecipeId = SelectedNode.DefaultRecipeId;
            dbNode.Remark = SelectedNode.Remark;
            dbNode.UpdatedAt = SelectedNode.UpdatedAt;
            await context.SaveChangesAsync();

            // ── 自动同步：仅当节点的有效限值实际发生变化时，才更新该节点下已有试验记录 ──
            // 保护：仅改名/备注等编辑绝不动历史记录；新有效限值<=0 时绝不清零记录已有限值
            // （记录的限值可能来自导入时的配方快照，清零会破坏验收依据）。
            try
            {
                // 修改前的有效限值：节点旧值 > 旧默认配方
                decimal oldEffectiveLimit = oldNodeLimit ?? 0;
                if (oldEffectiveLimit <= 0 && oldNodeRecipeId is > 0)
                {
                    var oldRecipe = await context.TestRecipes.FindAsync(oldNodeRecipeId.Value);
                    if (oldRecipe != null)
                        oldEffectiveLimit = oldRecipe.LeakageLimit;
                }

                // 修改后的有效限值：节点 > 配方 > 0
                decimal effectiveLimit = SelectedNode.LeakageLimit ?? 0;
                if (effectiveLimit <= 0 && SelectedNode.DefaultRecipeId is > 0)
                {
                    var recipe = await context.TestRecipes.FindAsync(SelectedNode.DefaultRecipeId.Value);
                    if (recipe != null)
                        effectiveLimit = recipe.LeakageLimit;
                }

                // 有效限值没变，或没有新的有效限值 → 不同步任何历史记录
                if (effectiveLimit <= 0 || oldEffectiveLimit == effectiveLimit)
                {
                    return;
                }

                var affectedRecords = await context.TestRecords
                    .Where(r => r.ObjectCode == editedCode)
                    .ToListAsync();

                bool anyUpdated = false;
                foreach (var record in affectedRecords)
                {
                    bool changed = false;

                    // 更新限值
                    if (record.LeakageLimit != effectiveLimit)
                    {
                        // 【保留旧值快照】覆盖前先保存原始数据
                        var previousValues = new
                        {
                            record.LeakageLimit,
                            Result = record.Result.ToString(),
                            ChangedAt = DateTime.Now,
                            ChangedBy = currentUser,
                            Reason = $"路径节点【{SelectedNode.DisplayName}】限值修改（{oldEffectiveLimit} → {effectiveLimit}）"
                        };
                        record.PreviousValuesJson = System.Text.Json.JsonSerializer.Serialize(previousValues);

                        record.LeakageLimit = effectiveLimit;
                        changed = true;
                    }

                    // 重算合格判定：仅限值变化的记录重算（Unknown 且限值已明确的补判）
                    if (changed || (record.Result == TestResult.Unknown && record.FinalLeakageRate > 0))
                    {
                        record.Result = record.FinalLeakageRate <= effectiveLimit
                            ? TestResult.Pass
                            : TestResult.Fail;
                    }

                    if (changed) anyUpdated = true;
                }

                if (anyUpdated)
                {
                    await context.SaveChangesAsync();
                    await logService.LogAsync("修改路径节点", currentUser,
                        $"修改{SelectedNode.NodeTypeText}【{SelectedNode.DisplayName}】有效限值 {oldEffectiveLimit} → {effectiveLimit}，同步更新 {affectedRecords.Count(r => r.LeakageLimit == effectiveLimit)} 条试验记录",
                        "Success");
                }
            }
            catch (Exception ex)
            {
                // 同步失败不影响节点修改本身，仅记录警告
                Log.Warning("[PathNode] 修改节点后同步试验记录限值失败: {Error}", ex.Message);
            }

            // 记录操作日志
            await logService.LogAsync("修改路径节点", currentUser,
                $"修改{SelectedNode.NodeTypeText}【{SelectedNode.DisplayName}】", "Success");

            // 刷新选中节点统计数据
            await LoadSelectedNodeStatisticsAsync(_loadStatsCts?.Token ?? default);
            // 刷新配方信息
            await LoadRecipeInfoAsync();

            Message = $"✅ 已更新节点：{SelectedNode.DisplayName}";
        }
        catch (Exception ex)
        {
            Message = $"❌ 修改失败：{ex.Message}";
        }
    }

    private async Task DeleteSelectedNodeAsync()
    {
        if (SelectedNode == null)
        {
            MessageBox.Show("请先选择要删除的节点。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            using var context = DbContextFactory.CreateDbContext();
            var logService = new OperationLogService(context);
            var currentUser = Services.Security.UserSession.Current?.User.UserName ?? "system";
            var codeToDelete = SelectedNode.Code;
            var nodeName = SelectedNode.DisplayName;
            var nodeType = SelectedNode.NodeTypeText;

            var node = await context.TestObjectPathNodes
                .FirstOrDefaultAsync(n => n.Code == codeToDelete);

            if (node == null)
            {
                MessageBox.Show("该节点在数据库中不存在（可能已被删除）。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                RemoveNodeFromTree(codeToDelete);
                return;
            }

            // 结构保护：有子节点的不允许直接删（避免误删整棵子树），请先删子节点
            var hasChildren = await context.TestObjectPathNodes.AnyAsync(n => n.ParentCode == codeToDelete);
            if (hasChildren)
            {
                MessageBox.Show(
                    $"【{nodeName}】下还有子节点，无法直接删除。\n请先删除其下的子节点后再删除本节点。",
                    "无法删除", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 实时采集保护：正在采集的试验对象不能删除
            if (Application.Current.MainWindow?.DataContext is MainViewModel mainVm)
            {
                var monitor = mainVm.RealtimeMonitorPage;
                if (monitor.IsMonitoring)
                {
                    var monitoringObjectCode = monitor.SelectedObject?.Code;
                    if (!string.IsNullOrEmpty(monitoringObjectCode))
                    {
                        // 检查正在采集的对象是否属于当前要删除的节点（或其子节点）
                        var isMonitoringThisNode = monitoringObjectCode == codeToDelete
                            || monitoringObjectCode.StartsWith(codeToDelete + "_");
                        if (isMonitoringThisNode)
                        {
                            MessageBox.Show(
                                $"【{nodeName}】或其子节点正在实时采集中，无法删除。\n\n请先停止实时采集后再删除。",
                                "无法删除", MessageBoxButton.OK, MessageBoxImage.Warning);
                            return;
                        }
                    }
                }
            }

            // 统计该节点名下的历史试验记录数（叶子节点，无子节点）
            var recordCount = await context.TestRecords.CountAsync(r => r.ObjectCode == codeToDelete);

            // 确认对话框：有记录时明确告知会一并删除记录及其过程数据
            var confirmText = recordCount > 0
                ? $"【{nodeName}】已有 {recordCount} 条试验记录。\n\n删除该节点将同时永久删除这 {recordCount} 条记录及其过程曲线数据，此操作不可恢复。\n\n确定要删除吗？"
                : $"确定要删除【{nodeName}】吗？\n\n此操作不可恢复。";

            var confirmResult = MessageBox.Show(
                confirmText, "确认删除", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (confirmResult != MessageBoxResult.OK) return;

            // 先删记录（TestRecords.ObjectCode → 节点 为 Restrict，必须先于节点删除；
            // TestProcessData.RecordCode → TestRecords 为 Cascade，随记录一并由数据库级联删除）。
            if (recordCount > 0)
            {
                var records = await context.TestRecords
                    .Where(r => r.ObjectCode == codeToDelete)
                    .ToListAsync();
                context.TestRecords.RemoveRange(records);
            }

            context.TestObjectPathNodes.Remove(node);
            await context.SaveChangesAsync();

            // 记录操作日志
            await logService.LogAsync("删除路径节点", currentUser,
                recordCount > 0
                    ? $"删除{nodeType}【{nodeName}】及其 {recordCount} 条试验记录"
                    : $"删除{nodeType}【{nodeName}】",
                "Success");

            // 直接从内存树中移除节点（不重建整棵树，保留展开状态）
            RemoveNodeFromTree(codeToDelete);

            var doneMsg = recordCount > 0
                ? $"已删除【{nodeName}】及其 {recordCount} 条试验记录。"
                : $"已删除【{nodeName}】。";
            SetMessage($"✅ {doneMsg}", 1);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"删除失败：{ex.Message}", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>从内存树中移除指定节点（不重建整棵树，保留展开状态）</summary>
    private void RemoveNodeFromTree(string nodeCode)
    {
        // 先尝试从根节点移除
        var rootNode = PathTree.FirstOrDefault(n => n.Code == nodeCode);
        if (rootNode != null)
        {
            PathTree.Remove(rootNode);
            if (SelectedNode?.Code == nodeCode)
                SelectedNode = PathTree.FirstOrDefault();
            return;
        }

        // 递归查找并移除
        foreach (var node in Flatten(PathTree))
        {
            var childToRemove = node.Children.FirstOrDefault(c => c.Code == nodeCode);
            if (childToRemove != null)
            {
                node.Children.Remove(childToRemove);
                if (SelectedNode?.Code == nodeCode)
                {
                    // 选中父节点
                    SelectedNode = node;
                }
                return;
            }
        }
    }

    public async Task LocateFirstMatchAsync()
    {
        var keyword = SearchText.Trim();
        if (string.IsNullOrWhiteSpace(keyword))
            return;

        try
        {
            using var context = DbContextFactory.CreateDbContext();
            // 机组名在跨项目时可能重名，必须按当前所选项目的编号过滤，避免定位到别的项目的同名机组
            var project = await context.Projects.FirstOrDefaultAsync(p => p.Name == SelectedProject);
            var unit = await context.Units.FirstOrDefaultAsync(u => u.Name == SelectedUnit
                && (project == null || u.ProjectCode == project.Code));
            if (unit == null) return;

            var matchedNode = await context.TestObjectPathNodes
                .FirstOrDefaultAsync(n => n.UnitCode == unit.Code &&
                    (n.Code.Contains(keyword) || n.Name.Contains(keyword)));

            if (matchedNode == null) return;

            var inMemoryNode = Flatten(PathTree).FirstOrDefault(n => n.Code == matchedNode.Code);
            if (inMemoryNode != null)
            {
                SelectedNode = inMemoryNode;
            }
        }
        catch { /* 搜索失败时静默处理 */ }
    }

    /// <summary>从内存计数器生成下一个编码</summary>
    public string GetNextCode(PathNodeType nodeType)
    {
        return nodeType switch
        {
            PathNodeType.System => $"SYS-{_systemSequence:000}",
            PathNodeType.Penetration => $"PEN{_penetrationSequence:00}",
            PathNodeType.Valve => $"VAL{_valveSequence:000}",
            PathNodeType.OtherComponent => $"COMP-{_componentSequence:000}",
            _ => string.Empty
        };
    }

    /// <summary>从数据库查询最新序列号并生成唯一编码</summary>
    private static async Task<string> GenerateUniqueCodeAsync(AppDbContext context, PathNodeType nodeType)
    {
        var allNodes = await context.TestObjectPathNodes.ToListAsync();
        int maxSeq = 0;

        foreach (var n in allNodes)
        {
            if (n.NodeType != nodeType) continue;

            int seq = ParseSequenceFromCode(n.Code, nodeType);
            if (seq > maxSeq) maxSeq = seq;
        }

        int nextSeq = maxSeq + 1;
        return nodeType switch
        {
            PathNodeType.System => $"SYS-{nextSeq:000}",
            PathNodeType.Penetration => $"PEN{nextSeq:00}",
            PathNodeType.Valve => $"VAL{nextSeq:000}",
            PathNodeType.OtherComponent => $"COMP-{nextSeq:000}",
            _ => throw new InvalidOperationException($"未知的节点类型：{nodeType}")
        };
    }

    /// <summary>从已有编码中解析出序列号</summary>
    private static int ParseSequenceFromCode(string code, PathNodeType nodeType)
    {
        if (string.IsNullOrEmpty(code)) return 0;

        var prefix = nodeType switch
        {
            PathNodeType.System => "SYS-",
            PathNodeType.Penetration => "PEN",
            PathNodeType.Valve => "VAL",
            PathNodeType.OtherComponent => "COMP-",
            _ => string.Empty
        };

        if (code.StartsWith(prefix) && code.Length > prefix.Length
            && int.TryParse(code.Substring(prefix.Length), out int seq))
        {
            return seq;
        }

        return 0;
    }

    private bool CanCreateNode(PathNodeType nodeType)
    {
        return nodeType switch
        {
            PathNodeType.System => CanCreateSystem,
            PathNodeType.Penetration => CanCreatePenetration,
            PathNodeType.Valve => CanCreateValve,
            PathNodeType.OtherComponent => CanCreateOtherComponent,
            _ => false
        };
    }

    private static IEnumerable<TestObjectPathNode> Flatten(IEnumerable<TestObjectPathNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in Flatten(node.Children)) yield return child;
        }
    }

    private static string GetNodeTypeName(PathNodeType nodeType)
    {
        return nodeType switch
        {
            PathNodeType.System => "系统",
            PathNodeType.Penetration => "贯穿件",
            PathNodeType.Valve => "阀门",
            PathNodeType.OtherComponent => "部件",
            _ => string.Empty
        };
    }

    private void IncrementSequence(PathNodeType nodeType)
    {
        switch (nodeType)
        {
            case PathNodeType.System: _systemSequence++; break;
            case PathNodeType.Penetration: _penetrationSequence++; break;
            case PathNodeType.Valve: _valveSequence++; break;
            case PathNodeType.OtherComponent: _componentSequence++; break;
        }
    }

    private void NotifySelectionChanged()
    {
        OnPropertyChanged(nameof(NodeTypeText));
        OnPropertyChanged(nameof(DetailTitle));
        OnPropertyChanged(nameof(AvailableCreateText));
        OnPropertyChanged(nameof(NodeOperationDescription));
        OnPropertyChanged(nameof(LeakageLimitText));
        OnPropertyChanged(nameof(TestPressureText));
        OnPropertyChanged(nameof(CanCreatePenetration));
        OnPropertyChanged(nameof(CanCreateValve));
        OnPropertyChanged(nameof(CanCreateOtherComponent));
        OnPropertyChanged(nameof(CreatableNodeTypesText));
        OnPropertyChanged(nameof(IsLeafNodeSelected));
        OnPropertyChanged(nameof(HasSelectedNode));
        OnPropertyChanged(nameof(HasNoSelection));
        OnPropertyChanged(nameof(HasChildren));
        OnPropertyChanged(nameof(HasDescendantRecords));
        OnPropertyChanged(nameof(CanDeleteNode));
        OnPropertyChanged(nameof(DeleteButtonToolTip));
        OnPropertyChanged(nameof(SelectedNodeTypeValue));
        OnPropertyChanged(nameof(TypeFieldLabel));
        OnPropertyChanged(nameof(HasTypeField));
        OnPropertyChanged(nameof(HasLeakageLimit));
        OnPropertyChanged(nameof(HasTestPressure));
        OnPropertyChanged(nameof(ParentNodeDisplay));

        // 通知命令的 CanExecute 重新评估
        EditNodeCommand.NotifyCanExecuteChanged();
        DeleteNodeCommand.NotifyCanExecuteChanged();
        _importDataCommand?.NotifyCanExecuteChanged();
        _importDocumentCommand?.NotifyCanExecuteChanged();
        _exportDataCommand?.NotifyCanExecuteChanged();
    }

    /// <summary>导入数据：选择数据包文件并导入到当前选中对象</summary>
    private async Task ImportDataAsync()
    {
        if (IsImporting) return; // 防止重复点击

        if (SelectedNode == null || !IsLeafNodeSelected)
        {
            SetMessage("请先选择一个阀门或其他部件节点", 2);
            return;
        }

        var dialog = new OpenFileDialog
        {
            Filter = "数据包文件 (*.json;*.txt;*.csv)|*.json;*.txt;*.csv|所有文件 (*.*)|*.*",
            Title = "选择试验数据包"
        };

        if (dialog.ShowDialog() != true)
        {
            SetMessage("已取消导入", 0);
            return;
        }

        try
        {
            IsImporting = true;
            SetMessage($"正在导入：{Path.GetFileName(dialog.FileName)} ...", 0);

            // 获取对象编码和所属机组
            string objectCode = SelectedNode.Code;
            string unitCode = SelectedNode.UnitCode;

            // 通过机组编码查找所属项目编码
            using var context = DbContextFactory.CreateDbContext();
            var unit = await context.Units.FirstOrDefaultAsync(u => u.Code == unitCode);
            if (unit == null)
            {
                SetMessage("❌ 无法找到该对象所属的机组信息", 2);
                return;
            }
            string projectCode = unit.ProjectCode;

            // 解析数据包并入库
            var testRecordService = new TestRecordService(context);
            var dataUploadService = new DataUploadService(testRecordService);

            var parsedData = await dataUploadService.ParseDataPackageAsync(dialog.FileName);

            // 【关键】用当前选中节点的编码覆盖数据包中的对象编码
            // （单文件导入以用户选择的节点为准，而非 CSV 里可能不匹配的编码）
            parsedData.ObjectCode = objectCode;

            // 如果数据包没有时间，用当前时间
            if (parsedData.TestTime == default)
                parsedData.TestTime = DateTime.Now;

            // 纯曲线/过程数据文件（如充压曲线）不含判定结果，缺结果时按"未知"入库，
            // 允许曲线单独导入生成一条记录并挂上过程曲线（判定与泄漏率可后续补录）。
            // 与批量上传中曲线单独导入的兜底逻辑一致。
            if (string.IsNullOrWhiteSpace(parsedData.Result))
                parsedData.Result = "Unknown";

            // 测量装置校验：文件缺装置编号（或编号未在台账登记）时，弹窗让用户手动选择一台。
            // 装置是 TestRecords 的外键（FK_TestRecords_MeasurementDevices_DeviceCode），
            // 直接入库会撞外键，所以在入库前补齐一个真实存在的装置编号。
            var fileDeviceCode = parsedData.DeviceCode?.Trim();
            bool deviceMissing = string.IsNullOrWhiteSpace(fileDeviceCode)
                || string.Equals(fileDeviceCode, "UNKNOWN", StringComparison.OrdinalIgnoreCase)
                || string.Equals(fileDeviceCode, "未指定", StringComparison.OrdinalIgnoreCase);
            bool deviceRegistered = !deviceMissing
                && await context.MeasurementDevices.AsNoTracking()
                    .AnyAsync(d => d.DeviceCode == fileDeviceCode);

            if (deviceMissing || !deviceRegistered)
            {
                // ✅ 过滤掉系统默认装置"未指定"，不让用户选到它
                var devices = await context.MeasurementDevices.AsNoTracking()
                    .Where(d => d.DeviceCode != "未指定")
                    .OrderBy(d => d.DeviceCode)
                    .ToListAsync();

                if (devices.Count == 0)
                {
                    SetMessage("❌ 测量装置台账为空，请先在\"测量装置台账\"中登记装置后再导入", 2);
                    return;
                }

                var hint = deviceMissing
                    ? "数据文件中未包含测量装置编号，请为本次导入选择一台装置。"
                    : $"数据文件中的装置编号\"{fileDeviceCode}\"未在台账登记，请改选一台已登记的装置。";

                var picker = new DevicePickerDialog(devices, hint)
                {
                    Owner = Application.Current?.MainWindow
                };

                if (picker.ShowDialog() != true || string.IsNullOrEmpty(picker.SelectedDeviceCode))
                {
                    SetMessage("已取消导入", 0);
                    return;
                }

                parsedData.DeviceCode = picker.SelectedDeviceCode;
            }

            // 自动生成记录编号
            string recordCode = $"R{projectCode}-{unitCode}-{objectCode}-{DateTime.Now:yyMMddHHmmss}";

            var currentUser = Services.Security.UserSession.Current?.User.UserName ?? "system";

            var testRecord = await dataUploadService.ValidateAndUploadAsync(
                parsedData,
                recordCode,
                projectCode,
                unitCode,
                currentUser);

            SetMessage($"✅ 导入完成：{Path.GetFileName(dialog.FileName)} → 记录 {testRecord.RecordCode}", 1);

            // 刷新统计面板
            await LoadSelectedNodeStatisticsAsync(CancellationToken.None);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("重复"))
        {
            SetMessage($"⚠️ 该记录已存在（重复）：{ex.Message}", 2);
        }
        catch (FormatException ex)
        {
            SetMessage($"❌ 数据格式错误：{ex.Message}", 2);
        }
        catch (ArgumentException ex)
        {
            SetMessage($"❌ 数据校验失败：{ex.Message}", 2);
        }
        catch (FileNotFoundException ex)
        {
            SetMessage($"❌ 文件不存在：{ex.Message}", 2);
        }
        catch (Exception ex)
        {
            SetMessage($"❌ 导入失败：{ex.Message}", 2);
        }
        finally
        {
            IsImporting = false;
        }
    }

    /// <summary>
    /// 按文档导入：选择实验报表格式 CSV（多行记录），逐行生成试验记录，
    /// 并按"系统→阀门"两级自动创建缺失的路径节点（归属当前所选项目/机组）。
    /// 行级容错：单行失败不影响其余行；装置缺失时统一弹一次选择器应用到全部行。
    /// </summary>
    private async Task ImportByDocumentAsync()
    {
        if (IsImporting) return; // 防止重复点击

        // 需要页面级的项目/机组（导入的记录与新建节点都归属它）
        string projectCode;
        string unitCode;
        try
        {
            using (var ctx = DbContextFactory.CreateDbContext())
            {
                var project = await ctx.Projects.FirstOrDefaultAsync(p => p.Name == SelectedProject);
                var unit = await ctx.Units.FirstOrDefaultAsync(u => u.Name == SelectedUnit
                    && (project == null || u.ProjectCode == project.Code));
                if (project == null || unit == null)
                {
                    SetMessage("请先选择有效的项目和机组", 2);
                    return;
                }
                projectCode = project.Code;
                unitCode = unit.Code;
            }
        }
        catch (Exception ex)
        {
            SetMessage($"❌ 查询项目/机组失败：{ex.Message}", 2);
            return;
        }

        var dialog = new OpenFileDialog
        {
            Filter = "实验报表文档 (*.csv;*.xlsx)|*.csv;*.xlsx|Excel 工作簿 (*.xlsx)|*.xlsx|CSV 文件 (*.csv)|*.csv|所有文件 (*.*)|*.*",
            Title = "选择实验报表文档"
        };

        if (dialog.ShowDialog() != true)
        {
            SetMessage("已取消导入", 0);
            return;
        }

        var filePath = dialog.FileName;
        List<ParsedDataPackage> rows;
        var testRecordService = new TestRecordService(DbContextFactory.CreateDbContext());
        var dataUploadService = new DataUploadService(testRecordService);

        try
        {
            IsImporting = true;
            SetMessage($"正在解析文档：{Path.GetFileName(filePath)} ...", 0);

            // 按扩展名分流：xlsx 实验记录表（含表头探测/合并单元格/单位换算）/ CSV 多行记录
            rows = Path.GetExtension(filePath).Equals(".xlsx", StringComparison.OrdinalIgnoreCase)
                ? await dataUploadService.ParseMultiRowRecordsXlsxAsync(filePath)
                : await dataUploadService.ParseMultiRowRecordsCsvFromFileAsync(filePath);
        }
        catch (FormatException ex)
        {
            SetMessage($"❌ {ex.Message}", 2);
            IsImporting = false;
            return;
        }
        catch (Exception ex)
        {
            SetMessage($"❌ 读取文档失败：{ex.Message}", 2);
            IsImporting = false;
            return;
        }

        // xlsx 文档标题自动识别机组归属（如"海南3机组"）：有对应机组则导入该机组——
        // 避免页面所选机组与文档不符造成错挂或被跨机组保护拦截；无对应机组则在
        // 当前所选项目下自动新建。CSV 无机组信息，沿用页面所选。
        string? unitResolveNote = null;
        if (rows.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.UnitName)) is { } docUnit)
        {
            var (resolvedProject, resolvedUnit, note) =
                await ResolveUnitFromDocumentAsync(docUnit.UnitName!.Trim(), projectCode);
            projectCode = resolvedProject;
            unitCode = resolvedUnit;
            unitResolveNote = note;
            SetMessage(note, 0);
        }

        try
        {
            // ===== 预览阶段预检（坏数据不进上下文）=====
            // 1) 装置：缺失/UNKNOWN/未登记的行统一弹一次选择器补齐
            var deviceCodesToFix = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var ctx = DbContextFactory.CreateDbContext())
            {
                var ledgerCodes = await ctx.MeasurementDevices.AsNoTracking()
                    .Select(d => d.DeviceCode)
                    .ToListAsync();
                var ledger = new HashSet<string>(ledgerCodes, StringComparer.OrdinalIgnoreCase);

                foreach (var row in rows)
                {
                    var code = row.DeviceCode?.Trim();
                    bool missing = string.IsNullOrWhiteSpace(code)
                        || code.Equals("UNKNOWN", StringComparison.OrdinalIgnoreCase)
                        || code.Equals("未指定", StringComparison.OrdinalIgnoreCase);
                    if (missing || !ledger.Contains(code!))
                    {
                        deviceCodesToFix.Add(code ?? string.Empty);
                    }
                }
            }

            if (deviceCodesToFix.Count > 0)
            {
                using var ctx2 = DbContextFactory.CreateDbContext();
                var devices = await ctx2.MeasurementDevices.AsNoTracking()
                    .Where(d => d.DeviceCode != "未指定")
                    .OrderBy(d => d.DeviceCode)
                    .ToListAsync();

                if (devices.Count == 0)
                {
                    SetMessage("❌ 测量装置台账为空，请先在\"测量装置台账\"中登记装置后再导入", 2);
                    return;
                }

                var hint = deviceCodesToFix.Count == 1 && !string.IsNullOrEmpty(deviceCodesToFix.First())
                    ? $"文档中的装置编号\"{deviceCodesToFix.First()}\"未在台账登记，请为本次导入选择一台装置（应用到全部相关行）。"
                    : "文档中部分/全部行缺少有效装置编号，请为本次导入选择一台装置（应用到全部相关行）。";

                var picker = new DevicePickerDialog(devices, hint)
                {
                    Owner = Application.Current?.MainWindow
                };

                if (picker.ShowDialog() != true || string.IsNullOrEmpty(picker.SelectedDeviceCode))
                {
                    SetMessage("已取消导入", 0);
                    return;
                }

                var picked = picker.SelectedDeviceCode;
                foreach (var row in rows)
                {
                    var code = row.DeviceCode?.Trim();
                    bool missing = string.IsNullOrWhiteSpace(code)
                        || code.Equals("UNKNOWN", StringComparison.OrdinalIgnoreCase)
                        || code.Equals("未指定", StringComparison.OrdinalIgnoreCase);
                    if (missing || deviceCodesToFix.Contains(code!))
                    {
                        row.DeviceCode = picked;
                    }
                }
            }

            // ===== 逐行导入（行级容错）=====
            var currentUser = Services.Security.UserSession.Current?.User.UserName ?? "system";
            int successCount = 0;
            var failedRows = new List<(int RowNo, string Reason)>();
            var createdNodes = new HashSet<string>();

            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                int rowNo = i + 1;

                try
                {
                    if (row.TestTime == default)
                    {
                        failedRows.Add((rowNo, "实验日期缺失或无法解析"));
                        continue;
                    }

                    // 阀门编号为空或"空"/NULL 占位：无法定位试验对象，行级失败给出可读原因
                    //（此前 row.ObjectCode!.Trim() 会空引用，报英文 NRE 消息进失败列表）
                    if (string.IsNullOrWhiteSpace(row.ObjectCode))
                    {
                        failedRows.Add((rowNo, "试验阀门编号为空（或为\"空\"占位），无法导入"));
                        continue;
                    }

                    // 系统列默认值兜底（为空时归入"未分类系统"）
                    // 建"系统→阀门"路径（已存在则复用），返回阀门编码。
                    // xlsx 导入时 ValveDisplayName 带贯穿件编号后缀（如 3CAM003VA(PN217)）
                    var objectCode = await dataUploadService.EnsureCsvPathExistsAsync(
                        unitCode, row.SystemName, row.ObjectCode.Trim(),
                        row.LeakageLimit, row.TestPressure, row.ValveDisplayName);
                    createdNodes.Add(objectCode);

                    row.ObjectCode = objectCode;
                    if (string.IsNullOrWhiteSpace(row.Result))
                        row.Result = "Unknown";

                    var recordCode = BuildDocumentRecordCode(projectCode, unitCode, objectCode, row.TestTime, rowNo);
                    await dataUploadService.ValidateAndUploadAsync(row, recordCode, projectCode, unitCode, currentUser);
                    successCount++;
                }
                catch (InvalidOperationException ex) when (ex.Message.Contains("重复"))
                {
                    failedRows.Add((rowNo, $"重复记录：{ex.Message}"));
                }
                catch (FormatException ex)
                {
                    failedRows.Add((rowNo, $"格式错误：{ex.Message}"));
                }
                catch (ArgumentException ex)
                {
                    failedRows.Add((rowNo, $"校验失败：{ex.Message}"));
                }
                catch (Exception ex)
                {
                    failedRows.Add((rowNo, ex.Message));
                }

                if (rowNo % 10 == 0 || rowNo == rows.Count)
                {
                    SetMessage($"按文档导入进度：{rowNo}/{rows.Count}（成功 {successCount}，失败 {failedRows.Count}）", 0);
                }
            }

            // ===== 操作日志 =====
            try
            {
                using var logCtx = DbContextFactory.CreateDbContext();
                var logService = new OperationLogService(logCtx);
                await logService.LogAsync("按文档导入", currentUser,
                    $"导入 {successCount} 条试验记录，失败 {failedRows.Count} 条，新建/复用 {createdNodes.Count} 个对象节点",
                    failedRows.Count == 0 ? "Success" : "Warning");
            }
            catch (Exception logEx)
            {
                Log.Warning(logEx, "[按文档导入] 写操作日志失败");
            }

            // ===== 结果汇总 =====
            var summary = new StringBuilder();
            if (unitResolveNote != null)
                summary.AppendLine(unitResolveNote);
            summary.AppendLine($"文档导入完成：共 {rows.Count} 行，成功 {successCount} 条，失败 {failedRows.Count} 条。");
            if (createdNodes.Count > 0)
            {
                summary.AppendLine($"涉及对象节点 {createdNodes.Count} 个（缺失的已自动创建）。");
            }
            foreach (var (rowNo, reason) in failedRows.Take(5))
            {
                summary.AppendLine($"第 {rowNo} 行失败：{reason}");
            }
            if (failedRows.Count > 5)
            {
                summary.AppendLine($"...其余 {failedRows.Count - 5} 条失败详见日志。");
            }

            MessageBox.Show(summary.ToString(), "按文档导入",
                MessageBoxButton.OK, failedRows.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);

            SetMessage($"✅ 按文档导入完成：成功 {successCount}，失败 {failedRows.Count}", failedRows.Count == 0 ? 1 : 2);

            // 刷新路径树（可能新建了节点）
            await LoadPathTreeAsync();
        }
        finally
        {
            IsImporting = false;
        }
    }

    /// <summary>
    /// 按文档标题提取的机组名解析归属：匹配现有机组（名称归一化后互相包含，兼容"海南3机组"/"海南3号机组"），
    /// 命中多个时优先当前所选项目下的；无命中则在当前所选项目下新建机组。
    /// 返回 (项目编码, 机组编码, 提示文案)。
    /// </summary>
    private async Task<(string ProjectCode, string UnitCode, string Note)> ResolveUnitFromDocumentAsync(
        string documentUnitName, string currentProjectCode)
    {
        static string Norm(string s) => s.Replace("号", "").Replace(" ", "").Trim();

        using var context = DbContextFactory.CreateDbContext();
        var units = await context.Units
            .Where(u => u.Status == EnabledStatus.Enabled)
            .Select(u => new { u.Code, u.Name, u.ProjectCode })
            .ToListAsync();

        var target = Norm(documentUnitName);
        var hits = units
            .Where(u => !string.IsNullOrEmpty(u.Name)
                && (Norm(u.Name!).Contains(target, StringComparison.Ordinal)
                    || target.Contains(Norm(u.Name!), StringComparison.Ordinal)))
            .ToList();

        if (hits.Count > 0)
        {
            // 跨项目同名机组：优先当前所选项目下的，否则取第一个
            var pick = hits.FirstOrDefault(h => h.ProjectCode == currentProjectCode) ?? hits[0];
            var note = $"文档归属机组：{pick.Name}（自动识别自文档标题）";
            Log.Information("[按文档导入] {Note}（文档提取={Extract}，匹配 {Hits} 个候选）", note, documentUnitName, hits.Count);
            return (pick.ProjectCode, pick.Code, note);
        }

        // 无对应机组：在当前所选项目下新建
        var newUnit = new Unit
        {
            Code = $"U-{DateTime.Now:yyyyMMddHHmmss}",
            Name = documentUnitName,
            ProjectCode = currentProjectCode,
            Status = EnabledStatus.Enabled,
            CreatedAt = DateTime.Now,
            Remark = "按文档导入自动创建",
        };
        context.Units.Add(newUnit);
        await context.SaveChangesAsync();

        var created = $"文档归属机组：{documentUnitName}（系统中无此机组，已在当前所选项目下自动新建）";
        Log.Information("[按文档导入] {Note}", created);
        return (currentProjectCode, newUnit.Code, created);
    }

    /// <summary>构造按文档导入的记录编号：RecordCode 上限 50 字符，超长时截短对象编码段。
    /// 后缀 = 时间(12+百分秒2) + 完整行号，同文档内不同行必不相同。</summary>
    private static string BuildDocumentRecordCode(string projectCode, string unitCode, string objectCode, DateTime testTime, int rowIndex)
    {
        var suffix = $"{testTime:yyMMddHHmmff}{rowIndex}";
        var budget = 50 - (1 + projectCode.Length + 1 + unitCode.Length + 1 + 1 + suffix.Length);
        var obj = budget >= objectCode.Length ? objectCode : objectCode[..Math.Max(1, budget)];
        return $"R{projectCode}-{unitCode}-{obj}-{suffix}";
    }

    /// <summary>导出数据：导出当前选中对象的全部历史试验记录</summary>
    private async Task ExportDataAsync()
    {
        if (SelectedNode == null || !IsLeafNodeSelected)
        {
            SetMessage("请先选择一个阀门或其他部件节点", 2);
            return;
        }

        try
        {
            using var context = DbContextFactory.CreateDbContext();

            // 查询该对象的全部历史记录
            var records = await context.TestRecords
                .Include(r => r.Project)
                .Include(r => r.Unit)
                .Include(r => r.Device)
                .Where(r => r.ObjectCode == SelectedNode.Code)
                .OrderByDescending(r => r.TestTime)
                .ToListAsync();

            if (records.Count == 0)
            {
                Message = $"该对象（{SelectedNode.DisplayName}）暂无历史数据";
                return;
            }

            var dialog = new SaveFileDialog
            {
                Filter = "Excel 工作簿 (*.xlsx)|*.xlsx|所有文件 (*.*)|*.*",
                FileName = $"{SelectedNode.Code}_历史记录_{DateTime.Now:yyyyMMdd}.xlsx",
                Title = "选择导出文件保存位置"
            };

            if (dialog.ShowDialog() != true)
            {
                SetMessage("已取消导出", 0);
                return;
            }

            var exportService = new ReportExportService();
            var nodeCode = SelectedNode.Code;
            // ClosedXML 导出是 CPU 密集操作，放后台线程避免 UI 冻结
            await Task.Run(() => exportService.ExportObjectHistory(nodeCode, records, dialog.FileName));

            SetMessage($"✅ 已导出 {records.Count} 条记录", 1);
        }
        catch (Exception ex)
        {
            SetMessage($"❌ 导出失败：{ex.Message}", 2);
        }
    }
}
