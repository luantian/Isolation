using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using IsolationLeakage.App.Data;
using IsolationLeakage.App.Models;
using IsolationLeakage.App.Models.Database;
using IsolationLeakage.App.Services;
using Microsoft.EntityFrameworkCore;

namespace IsolationLeakage.App.ViewModels;

/// <summary>
/// 任务下载视图模型
/// 支持两种模式：
/// 1. 独立模式（默认）：自行加载项目/机组/路径树
/// 2. 共享树模式：由父页面提供路径树，自身只负责任务操作
/// </summary>
public sealed class TaskDownloadViewModel : ViewModelBase
{
    private string _selectedProject = string.Empty;
    private string _selectedUnit = string.Empty;
    private TestObjectPathNode? _selectedNode;
    private TestObjectPathNode? _selectedObjectForRemove;
    private string _message = string.Empty;
    private string _taskStatus = "未开始";
    private string _taskMessage = "请选择试验对象并创建任务";
    private bool _sharedTreeMode;

    public TaskDownloadViewModel()
    {
        Projects = new ObservableCollection<string>();
        Units = new ObservableCollection<string>();
        PathTree = new ObservableCollection<TestObjectPathNode>();
        SelectedObjects = new ObservableCollection<TestObjectPathNode>();
        Devices = new ObservableCollection<MeasurementDevice>();
        TaskHistory = new ObservableCollection<TaskDownloadRecord>();

        // 命令只创建一次
        AddSelectedObjectCommand = new RelayCommand(() => AddNodeFromParent(SelectedNode));
        RemoveSelectedObjectCommand = new RelayCommand(RemoveSelectedObject);
        CreateTaskCommand = new RelayCommand(() => _ = CreateTaskAsync());
        RefreshHistoryCommand = new RelayCommand(() => _ = LoadTaskHistoryAsync());

        // 注意：不在此处调用 SafeLoadAsync/LoadDataAsync
        // 独立模式由调用方决定何时加载；共享树模式由 InitializeForSharedTreeAsync 加载
    }

    /// <summary>
    /// 初始化为共享树模式（由父页面提供路径树）
    /// 不加载项目/机组/路径树，只加载装置和历史
    /// </summary>
    public async Task InitializeForSharedTreeAsync()
    {
        _sharedTreeMode = true;
        try
        {
            using var context = DbContextFactory.CreateDbContext();

            // 只加载测量装置（过滤掉系统默认装置"未指定"）
            var devices = await context.MeasurementDevices
                .Where(d => d.EnabledStatus == EnabledStatus.Enabled && d.DeviceCode != "未指定")
                .ToListAsync();

            Devices.Clear();
            foreach (var device in devices) Devices.Add(device);
            SelectedDevice = Devices.FirstOrDefault();

            // 加载任务历史
            await LoadTaskHistoryAsync();
        }
        catch (Exception ex)
        {
            Message = $"加载装置数据失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 同步共享树引用（父页面树变化时调用）
    /// </summary>
    public void SyncSharedTree(ObservableCollection<TestObjectPathNode> sharedTree)
    {
        // 共享树模式下 PathTree 不自行管理，由父页面绑定
    }

    /// <summary>
    /// 从父页面的选中节点添加对象到待下载列表
    /// </summary>
    public void AddNodeFromParent(TestObjectPathNode? node)
    {
        if (node == null)
        {
            Message = "请先在左侧路径树中选择一个试验对象";
            return;
        }

        if (node.NodeType is not (PathNodeType.Valve or PathNodeType.OtherComponent))
        {
            Message = "只能选择阀门或其他密封性部件";
            return;
        }

        if (SelectedObjects.Any(n => n.Code == node.Code))
        {
            Message = $"对象 {node.Code} 已在列表中";
            return;
        }

        SelectedObjects.Add(node);
        Message = $"已添加：{node.DisplayName}";
    }

    // ================ 项目/机组选择（独立模式使用） ================

    public ObservableCollection<string> Projects { get; }
    public ObservableCollection<string> Units { get; }

    public string SelectedProject
    {
        get => _selectedProject;
        set
        {
            if (SetProperty(ref _selectedProject, value) && !_sharedTreeMode)
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
            if (SetProperty(ref _selectedUnit, value) && !_sharedTreeMode)
            {
                _ = LoadPathTreeAsync();
            }
        }
    }

    // ================ 试验对象路径树 ================

    public ObservableCollection<TestObjectPathNode> PathTree { get; }

    public TestObjectPathNode? SelectedNode
    {
        get => _selectedNode;
        set
        {
            if (_selectedNode == value) return;
            _selectedNode = value;
            OnPropertyChanged();
        }
    }

    // ================ 已选试验对象 ================

    public ObservableCollection<TestObjectPathNode> SelectedObjects { get; }

    /// <summary>待移除对象（DataGrid 选中行绑定）</summary>
    public TestObjectPathNode? SelectedObjectForRemove
    {
        get => _selectedObjectForRemove;
        set => SetProperty(ref _selectedObjectForRemove, value);
    }

    // ================ 测量装置 ================

    public ObservableCollection<MeasurementDevice> Devices { get; }

    private MeasurementDevice? _selectedDevice;
    public MeasurementDevice? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (SetProperty(ref _selectedDevice, value))
            {
                OnPropertyChanged(nameof(SelectedDeviceText));
                OnPropertyChanged(nameof(SelectedDeviceCode));
                OnPropertyChanged(nameof(SelectedDeviceStatusText));
            }
        }
    }

    // ================ 任务状态 ================

    public string TaskStatus
    {
        get => _taskStatus;
        set => SetProperty(ref _taskStatus, value);
    }

    public string TaskMessage
    {
        get => _taskMessage;
        set => SetProperty(ref _taskMessage, value);
    }

    /// <summary>选中装置显示文字</summary>
    public string SelectedDeviceText => SelectedDevice != null ? $"{SelectedDevice.DeviceName}" : "未选择装置";

    /// <summary>选中装置编号</summary>
    public string SelectedDeviceCode => SelectedDevice?.DeviceCode ?? "-";

    /// <summary>选中装置状态文字</summary>
    public string SelectedDeviceStatusText => SelectedDevice?.ConnectionStatusText ?? "-";

    public string Message
    {
        get => _message;
        set => SetProperty(ref _message, value);
    }

    // ================ 任务历史 ================

    public ObservableCollection<TaskDownloadRecord> TaskHistory { get; }

    // ================ 命令 ================

    public IRelayCommand AddSelectedObjectCommand { get; }
    public IRelayCommand RemoveSelectedObjectCommand { get; }
    public IRelayCommand CreateTaskCommand { get; }
    public IRelayCommand RefreshHistoryCommand { get; }

    // ================ 方法 ================

    /// <summary>
    /// 从待下载列表移除选中对象
    /// </summary>
    private void RemoveSelectedObject()
    {
        var node = SelectedObjectForRemove;
        if (node == null)
        {
            Message = "请在列表中选择一个要移除的对象";
            return;
        }

        var displayName = node.DisplayName;
        SelectedObjects.Remove(node);
        Message = $"已移除：{displayName}";
    }

    /// <summary>
    /// 创建并下载任务
    /// </summary>
    private async Task CreateTaskAsync()
    {
        if (SelectedObjects.Count == 0)
        {
            Message = "请至少选择一个试验对象";
            TaskStatus = "错误";
            TaskMessage = "未选择试验对象";
            return;
        }

        if (SelectedDevice == null)
        {
            Message = "请选择目标测量装置";
            TaskStatus = "错误";
            TaskMessage = "未选择测量装置";
            return;
        }

        try
        {
            TaskStatus = "创建中...";
            TaskMessage = "正在生成任务载荷...";
            Message = "正在创建任务...";

            var objectCodes = SelectedObjects.Select(n => n.Code).ToList();

            var payload = await AppServices.TaskDownloadService.CreateTaskAsync(objectCodes, SelectedDevice.DeviceCode);

            TaskStatus = "下发中...";
            TaskMessage = $"任务 {payload.TaskId} 已创建，正在下发至装置...";
            Message = $"任务已创建：{payload.TaskId}";

            var result = await AppServices.TaskDownloadService.DownloadTaskAsync(payload.TaskId, SelectedDevice.DeviceCode);

            if (result.Success)
            {
                TaskStatus = "成功";
                TaskMessage = $"任务下发成功：成功 {result.SentCount}/{result.TotalObjects} 个对象";
                Message = $"✅ 任务下发成功：{result.Message}";
            }
            else
            {
                TaskStatus = "失败";
                TaskMessage = $"下发失败：{result.Message}";
                Message = $"❌ 任务下发失败：{result.Message}";
            }

            await LoadTaskHistoryAsync();
        }
        catch (Exception ex)
        {
            TaskStatus = "错误";
            TaskMessage = ex.Message;
            Message = $"❌ 任务创建失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 加载任务历史记录
    /// </summary>
    private async Task LoadTaskHistoryAsync()
    {
        try
        {
            var history = await AppServices.TaskDownloadService.GetTaskHistoryAsync(pageSize: 100);

            TaskHistory.Clear();
            foreach (var record in history)
            {
                TaskHistory.Add(record);
            }
        }
        catch (Exception ex)
        {
            Message = $"加载任务历史失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 初始化加载数据（独立模式）
    /// </summary>
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

            var devices = await context.MeasurementDevices
                .Where(d => d.EnabledStatus == EnabledStatus.Enabled && d.DeviceCode != "未指定")
                .ToListAsync();

            Devices.Clear();
            foreach (var device in devices) Devices.Add(device);
            SelectedDevice = Devices.FirstOrDefault();

            await LoadTaskHistoryAsync();
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
        if (_sharedTreeMode) return;

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
        }
        catch (Exception ex)
        {
            Message = $"加载路径树失败：{ex.Message}";
        }
    }
}
