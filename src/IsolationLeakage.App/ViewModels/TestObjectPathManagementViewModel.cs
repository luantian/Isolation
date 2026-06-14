using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using IsolationLeakage.App.Data;
using IsolationLeakage.App.Models;
using IsolationLeakage.App.Models.Database;
using IsolationLeakage.App.Services;
using IsolationLeakage.App.Views;
using Microsoft.EntityFrameworkCore;
using Microsoft.Win32;

namespace IsolationLeakage.App.ViewModels;

/// <summary>
/// 试验对象路径管理视图模型（简化版 - 仅统计概览，无完整历史列表）
/// </summary>
public sealed class TestObjectPathManagementViewModel : ViewModelBase
{
    private int _componentSequence;
    private string _locateMessage = "输入编号或名称后点击定位";
    private int _penetrationSequence;
    private string _searchText = string.Empty;
    private string _selectedProject = string.Empty;
    private string _selectedUnit = string.Empty;
    private int _systemSequence;
    private int _valveSequence;
    private TestObjectPathNode? _selectedNode;
    private string _message = string.Empty;

    // 统计数据（轻量级）
    private int _totalTestCount;
    private int _passedTestCount;
    private int _failedTestCount;
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

        _ = SafeLoadAsync();

        async Task SafeLoadAsync()
        {
            try
            {
                await LoadDataAsync();
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
                OnPropertyChanged(nameof(CurrentScopeText));
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
                OnPropertyChanged(nameof(CurrentScopeText));
            }
        }
    }

    public string SearchText
    {
        get => _searchText;
        set => SetProperty(ref _searchText, value);
    }

    public string LocateMessage
    {
        get => _locateMessage;
        set
        {
            if (SetProperty(ref _locateMessage, value))
                OnPropertyChanged(nameof(HasLocateMessage));
        }
    }

    public string Message
    {
        get => _message;
        set => SetProperty(ref _message, value);
    }

    public TestObjectPathNode? SelectedNode
    {
        get => _selectedNode;
        set
        {
            if (_selectedNode == value) return;
            _selectedNode = value;
            OnPropertyChanged();
            NotifySelectionChanged();
            _ = LoadSelectedNodeStatisticsAsync();
        }
    }

    public string CurrentScopeText => string.IsNullOrWhiteSpace(SelectedProject) || string.IsNullOrWhiteSpace(SelectedUnit)
        ? "当前范围：未选择项目/机组"
        : $"当前范围：{SelectedProject} / {SelectedUnit}";

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
        PathNodeType.System => "系统用于归集该系统下的试验对象路径",
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

    public string LeakageLimitText => SelectedNode?.LeakageLimit == null ? "-" : $"{SelectedNode.LeakageLimit:0.###} L/min";
    public string TestPressureText => SelectedNode?.TestPressure == null ? "-" : $"{SelectedNode.TestPressure:0.###} MPa";

    public bool CanCreateSystem => true;
    public bool CanCreatePenetration => SelectedNode == null || SelectedNode.NodeType == PathNodeType.System;
    public bool CanCreateValve => SelectedNode?.NodeType is PathNodeType.System or PathNodeType.Penetration;
    public bool CanCreateOtherComponent => SelectedNode == null || SelectedNode.NodeType is PathNodeType.System or PathNodeType.Penetration;

    public IRelayCommand LocateCommand => new RelayCommand(() => _ = LocateFirstMatchAsync());
    public IRelayCommand CreateSystemCommand => new RelayCommand(() => _ = CreateNodeAsync(PathNodeType.System));
    public IRelayCommand CreatePenetrationCommand => new RelayCommand(() => _ = CreateNodeAsync(PathNodeType.Penetration));
    public IRelayCommand CreateValveCommand => new RelayCommand(() => _ = CreateNodeAsync(PathNodeType.Valve));
    public IRelayCommand CreateOtherComponentCommand => new RelayCommand(() => _ = CreateNodeAsync(PathNodeType.OtherComponent));
    public IRelayCommand EditNodeCommand => new RelayCommand(() => _ = EditSelectedNodeAsync());
    public IRelayCommand DeleteNodeCommand => new RelayCommand(() => _ = DeleteSelectedNodeAsync());

    /// <summary>选中节点是否为叶子节点（阀门/其他部件），用于控制导入/导出按钮</summary>
    public bool IsLeafNodeSelected => SelectedNode?.NodeType is PathNodeType.Valve or PathNodeType.OtherComponent;

    /// <summary>导入数据命令</summary>
    public IRelayCommand ImportDataCommand => new RelayCommand(() => _ = ImportDataAsync());

    /// <summary>导出数据命令</summary>
    public IRelayCommand ExportDataCommand => new RelayCommand(() => _ = ExportDataAsync());

    public bool HasSelectedNode => SelectedNode != null;
    public bool HasNoSelection => SelectedNode == null;
    public bool CanDeleteNode => SelectedNode != null && !HasHistoricalData;
    public string DeleteWarningText => HasHistoricalData ? "已有历史数据，不允许删除" : string.Empty;
    public bool HasDeleteWarning => HasHistoricalData;

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

    /// <summary>定位消息是否非空</summary>
    public bool HasLocateMessage => !string.IsNullOrWhiteSpace(LocateMessage) && LocateMessage != "输入编号或名称后点击定位";

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
    private async Task LoadSelectedNodeStatisticsAsync()
    {
        // 重置统计数据
        TotalTestCount = 0;
        PassedTestCount = 0;
        FailedTestCount = 0;
        LatestTestTime = "-";
        LatestLeakageRate = "-";
        LatestResult = "-";
        LatestDevice = "-";
        PassRate = "-";
        OnPropertyChanged(nameof(HasHistoricalData));
        OnPropertyChanged(nameof(LatestResultBrush));
        OnPropertyChanged(nameof(HasTestData));
        OnPropertyChanged(nameof(HasNoTestData));

        if (SelectedNode == null) return;

        var nodeCode = SelectedNode.Code;

        try
        {
            using var context = DbContextFactory.CreateDbContext();

            // 查询该对象的所有试验记录
            var records = await context.TestRecords
                .Where(r => r.ObjectCode == nodeCode)
                .OrderByDescending(r => r.TestTime)
                .ToListAsync();

            // 更新统计
            TotalTestCount = records.Count;
            PassedTestCount = records.Count(r => r.Result == TestResult.Pass);
            FailedTestCount = records.Count(r => r.Result == TestResult.Fail);

            if (TotalTestCount > 0)
            {
                var latest = records.First();
                LatestTestTime = latest.TestTime.ToString("yyyy-MM-dd HH:mm");
                LatestLeakageRate = $"{latest.FinalLeakageRate:0.###} L/min";
                LatestResult = latest.Result == TestResult.Pass ? "合格" : "不合格";
                LatestDevice = latest.DeviceCode;
                PassRate = $"{(decimal)PassedTestCount / TotalTestCount * 100:0.0}%";
                OnPropertyChanged(nameof(LatestResultBrush));
            }

            OnPropertyChanged(nameof(HasHistoricalData));
            OnPropertyChanged(nameof(HasTestData));
            OnPropertyChanged(nameof(HasNoTestData));

            if (records.Count > 0)
            {
                Message = $"已加载该对象的统计数据";
            }
            else
            {
                Message = "该对象暂无历史试验记录";
            }
        }
        catch (Exception ex)
        {
            Message = $"加载统计数据失败：{ex.Message}";
        }
    }

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
        }
        catch (Exception ex)
        {
            Message = $"加载数据失败：{ex.Message}";
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
        if (string.IsNullOrWhiteSpace(SelectedProject) || string.IsNullOrWhiteSpace(SelectedUnit))
        {
            PathTree.Clear();
            SelectedNode = null;
            return;
        }

        try
        {
            using var context = DbContextFactory.CreateDbContext();
            var unit = await context.Units.FirstOrDefaultAsync(u => u.Name == SelectedUnit);
            if (unit == null) return;

            var rootNodes = await context.TestObjectPathNodes
                .Where(n => n.UnitCode == unit.Code && n.ParentCode == null)
                .Include(n => n.Children)
                .ThenInclude(c => c.Children)
                .OrderBy(n => n.Code)
                .ToListAsync();

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
            Message = $"加载路径树失败：{ex.Message}";
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
            Message = "请先选择机组";
            return;
        }

        try
        {
            using var context = DbContextFactory.CreateDbContext();
            var logService = new OperationLogService(context);
            var currentUser = Services.Security.UserSession.Current?.User.UserName ?? "system";

            var unit = await context.Units.FirstOrDefaultAsync(u => u.Name == SelectedUnit);
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
            Message = "请先选择要修改的节点";
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
                TestPressureText = SelectedNode.TestPressure?.ToString() ?? string.Empty,
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
            SelectedNode.Remark = updated.Remark?.Trim();
            SelectedNode.UpdatedAt = DateTime.Now;

            // 通知 UI 刷新 DisplayName（编号+名称组合显示）
            OnPropertyChanged(nameof(SelectedNode.DisplayName));

            // 同步到数据库
            using var context = DbContextFactory.CreateDbContext();
            var logService = new OperationLogService(context);
            var currentUser = Services.Security.UserSession.Current?.User.UserName ?? "system";

            context.TestObjectPathNodes.Update(SelectedNode);
            await context.SaveChangesAsync();

            // 记录操作日志
            await logService.LogAsync("修改路径节点", currentUser,
                $"修改{SelectedNode.NodeTypeText}【{SelectedNode.DisplayName}】", "Success");

            // 刷新选中节点统计数据
            await LoadSelectedNodeStatisticsAsync();

            Message = $"✅ 已更新节点：{SelectedNode.DisplayName}";
        }
        catch (Exception ex)
        {
            Message = $"❌ 修改失败：{ex.Message}";
        }
    }

    private async Task DeleteSelectedNodeAsync()
    {
        if (SelectedNode == null) return;

        try
        {
            using var context = DbContextFactory.CreateDbContext();
            var logService = new OperationLogService(context);
            var currentUser = Services.Security.UserSession.Current?.User.UserName ?? "system";
            var codeToDelete = SelectedNode.Code;
            var nodeName = SelectedNode.DisplayName;
            var nodeType = SelectedNode.NodeTypeText;

            // 从当前 context 查询实体，确保被跟踪
            var node = await context.TestObjectPathNodes
                .FirstOrDefaultAsync(n => n.Code == codeToDelete);

            if (node == null)
            {
                Message = "❌ 该节点在数据库中不存在";
                return;
            }

            // 删除保护：有历史数据的节点不允许删除
            var hasRecords = await context.TestRecords.AnyAsync(r => r.ObjectCode == codeToDelete);
            if (hasRecords)
            {
                Message = "❌ 该节点已有历史试验记录，不允许删除";
                return;
            }

            // 删除保护：有子节点的不允许直接删除
            var hasChildren = await context.TestObjectPathNodes.AnyAsync(n => n.ParentCode == codeToDelete);
            if (hasChildren)
            {
                Message = "❌ 该节点下有子节点，不允许直接删除";
                return;
            }

            context.TestObjectPathNodes.Remove(node);
            await context.SaveChangesAsync();

            // 记录操作日志
            await logService.LogAsync("删除路径节点", currentUser,
                $"删除{nodeType}【{nodeName}】", "Success");

            // 直接从内存树中移除节点（不重建整棵树，保留展开状态）
            RemoveNodeFromTree(codeToDelete);

            Message = "✅ 已从数据库删除该节点";
        }
        catch (Exception ex)
        {
            Message = $"❌ 删除失败：{ex.Message}";
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
        {
            LocateMessage = "请先输入要定位的编号或名称";
            return;
        }

        try
        {
            using var context = DbContextFactory.CreateDbContext();
            var unit = await context.Units.FirstOrDefaultAsync(u => u.Name == SelectedUnit);
            if (unit == null) return;

            var matchedNode = await context.TestObjectPathNodes
                .FirstOrDefaultAsync(n => n.UnitCode == unit.Code &&
                    (n.Code.Contains(keyword) || n.Name.Contains(keyword)));

            if (matchedNode == null)
            {
                LocateMessage = $"未找到匹配路径：{keyword}";
                return;
            }

            var inMemoryNode = Flatten(PathTree).FirstOrDefault(n => n.Code == matchedNode.Code);
            if (inMemoryNode != null)
            {
                SelectedNode = inMemoryNode;
                LocateMessage = $"已定位：{inMemoryNode.DisplayName}";
            }
            else
            {
                LocateMessage = $"数据库中存在但未加载到树：{matchedNode.DisplayName}";
            }
        }
        catch (Exception ex)
        {
            LocateMessage = $"定位失败：{ex.Message}";
        }
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
        OnPropertyChanged(nameof(IsLeafNodeSelected));
        OnPropertyChanged(nameof(HasSelectedNode));
        OnPropertyChanged(nameof(HasNoSelection));
        OnPropertyChanged(nameof(CanDeleteNode));
        OnPropertyChanged(nameof(DeleteWarningText));
        OnPropertyChanged(nameof(HasDeleteWarning));
        OnPropertyChanged(nameof(SelectedNodeTypeValue));
        OnPropertyChanged(nameof(TypeFieldLabel));
        OnPropertyChanged(nameof(HasTypeField));
        OnPropertyChanged(nameof(HasLeakageLimit));
        OnPropertyChanged(nameof(HasTestPressure));
        OnPropertyChanged(nameof(ParentNodeDisplay));
    }

    /// <summary>导入数据：选择数据包文件并导入到当前选中对象</summary>
    private async Task ImportDataAsync()
    {
        if (SelectedNode == null || !IsLeafNodeSelected)
        {
            Message = "请先选择一个阀门或其他部件节点";
            return;
        }

        var dialog = new OpenFileDialog
        {
            Filter = "数据包文件 (*.json;*.txt;*.csv)|*.json;*.txt;*.csv|所有文件 (*.*)|*.*",
            Title = "选择试验数据包"
        };

        if (dialog.ShowDialog() != true)
        {
            Message = "已取消导入";
            return;
        }

        try
        {
            Message = $"正在导入：{Path.GetFileName(dialog.FileName)} ...";

            // TODO: 实际解析和入库逻辑
            // 当前仅展示流程，后续接 DataUploadService
            await Task.Delay(100);

            Message = $"✅ 导入完成（待接入实际解析逻辑）：{Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception ex)
        {
            Message = $"❌ 导入失败：{ex.Message}";
        }
    }

    /// <summary>导出数据：导出当前选中对象的全部历史试验记录</summary>
    private async Task ExportDataAsync()
    {
        if (SelectedNode == null || !IsLeafNodeSelected)
        {
            Message = "请先选择一个阀门或其他部件节点";
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
                Message = "已取消导出";
                return;
            }

            var exportService = new ReportExportService();
            exportService.ExportObjectHistory(SelectedNode.Code, records, dialog.FileName);

            Message = $"✅ 已导出 {records.Count} 条记录：{dialog.FileName}";
        }
        catch (Exception ex)
        {
            Message = $"❌ 导出失败：{ex.Message}";
        }
    }
}
