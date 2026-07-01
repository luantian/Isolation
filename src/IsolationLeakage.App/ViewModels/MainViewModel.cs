using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using IsolationLeakage.App.Models;
using IsolationLeakage.App.Services;
using IsolationLeakage.App.Services.Security;

namespace IsolationLeakage.App.ViewModels;

/// <summary>
/// 主窗口视图模型
/// </summary>
public sealed class MainViewModel : ViewModelBase
{
    private object? _activePage;
    private string _connectionStatusText = "⚪ 无设备连接";
    private Brush _connectionBadgeBrush = Brushes.Gray;
    private string _lastSyncTimeText = "暂无同步";
    private readonly DispatcherTimer _connectionTimer;

    public MainViewModel()
    {
        OverviewPage = new OverviewViewModel();
        MasterDataPage = new MasterDataViewModel();
        RecipeManagementPage = new RecipeManagementViewModel();
        TestRecordsPage = new TestRecordsViewModel();
        RealtimeMonitorPage = new RealtimeMonitorViewModel();
        StatisticsAnalysisPage = new StatisticsAnalysisViewModel();
        SystemManagementPage = new SystemManagementViewModel();

        NavigateOverviewCommand = new RelayCommand(() => ActivePage = OverviewPage);
        NavigateMasterDataCommand = new RelayCommand(() => ActivePage = MasterDataPage);
        NavigateRecipeCommand = new RelayCommand(() => ActivePage = RecipeManagementPage);
        NavigateRecordsCommand = new RelayCommand(() => ActivePage = TestRecordsPage);
        NavigateRealtimeMonitorCommand = new RelayCommand(() => ActivePage = RealtimeMonitorPage);
        NavigateAnalysisCommand = new RelayCommand(() => ActivePage = StatisticsAnalysisPage);
        NavigateSystemManagementCommand = new RelayCommand(() => ActivePage = SystemManagementPage);

        // 按权限过滤导航项
        RefreshNavItems();

        ActivePage = OverviewPage;

        // 初始化设备连接状态定时器（30秒轮询）
        _connectionTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(30)
        };
        _connectionTimer.Tick += (_, _) => RefreshConnectionStatus();
        _connectionTimer.Start();
        RefreshConnectionStatus();
    }

    public ObservableCollection<NavItemDef> NavItems { get; } = [];

    public OverviewViewModel OverviewPage { get; }
    public MasterDataViewModel MasterDataPage { get; }
    public RecipeManagementViewModel RecipeManagementPage { get; }
    public TestRecordsViewModel TestRecordsPage { get; }
    public RealtimeMonitorViewModel RealtimeMonitorPage { get; }
    public StatisticsAnalysisViewModel StatisticsAnalysisPage { get; }
    public SystemManagementViewModel SystemManagementPage { get; }

    public object? ActivePage
    {
        get => _activePage;
        set
        {
            if (SetProperty(ref _activePage, value))
            {
                OnPropertyChanged(nameof(IsOverviewActive));
                OnPropertyChanged(nameof(IsMasterDataActive));
                OnPropertyChanged(nameof(IsRecordsActive));
                OnPropertyChanged(nameof(IsRealtimeMonitorActive));
                OnPropertyChanged(nameof(IsAnalysisActive));
                OnPropertyChanged(nameof(IsSystemManagementActive));
                OnPropertyChanged(nameof(CurrentPageTitle));
                // 刷新导航项激活状态
                foreach (var item in NavItems) item.RefreshActive();
                // 切换页面时刷新数据
                if (value is IRefreshable refreshable)
                    _ = refreshable.RefreshAsync();
            }
        }
    }

    public ICommand NavigateOverviewCommand { get; }
    public ICommand NavigateMasterDataCommand { get; }
    public ICommand NavigateRecipeCommand { get; }
    public ICommand NavigateRecordsCommand { get; }
    public ICommand NavigateRealtimeMonitorCommand { get; }
    public ICommand NavigateAnalysisCommand { get; }
    public ICommand NavigateSystemManagementCommand { get; }

    public string CurrentPageTitle => ActivePage switch
    {
        OverviewViewModel => "首页概览",
        MasterDataViewModel => "基础台账",
        RecipeManagementViewModel => "配方管理",
        TestRecordsViewModel => "试验记录",
        RealtimeMonitorViewModel => "实时监视",
        StatisticsAnalysisViewModel => "数据分析",
        SystemManagementViewModel => "系统设置",
        _ => "首页概览"
    };

    public bool IsOverviewActive => ActivePage is OverviewViewModel;
    public bool IsMasterDataActive => ActivePage is MasterDataViewModel;
    public bool IsRecipeActive => ActivePage is RecipeManagementViewModel;
    public bool IsRecordsActive => ActivePage is TestRecordsViewModel;
    public bool IsRealtimeMonitorActive => ActivePage is RealtimeMonitorViewModel;
    public bool IsAnalysisActive => ActivePage is StatisticsAnalysisViewModel;
    public bool IsSystemManagementActive => ActivePage is SystemManagementViewModel;

    /// <summary>当前登录用户名（用于状态栏显示）</summary>
    public string CurrentUserName => UserSession.IsLoggedIn ? UserSession.DisplayName : "未登录";

    /// <summary>当前登录角色名（用于状态栏显示）</summary>
    public string CurrentRoleName
    {
        get
        {
            if (!UserSession.IsLoggedIn) return "";
            var session = UserSession.Current;
            if (session == null) return "";
            var role = session.Roles.FirstOrDefault();
            return role != null ? $"[{role.RoleName}]" : "";
        }
    }

    /// <summary>设备连接状态文字</summary>
    public string ConnectionStatusText
    {
        get => _connectionStatusText;
        private set
        {
            if (_connectionStatusText != value)
            {
                _connectionStatusText = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>设备连接状态指示灯颜色</summary>
    public Brush ConnectionBadgeBrush
    {
        get => _connectionBadgeBrush;
        private set
        {
            if (_connectionBadgeBrush != value)
            {
                _connectionBadgeBrush = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>最近同步时间文字</summary>
    public string LastSyncTimeText
    {
        get => _lastSyncTimeText;
        private set
        {
            if (_lastSyncTimeText != value)
            {
                _lastSyncTimeText = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>刷新设备连接状态</summary>
    private void RefreshConnectionStatus()
    {
        try
        {
            var connected = AppServices.ConnectionManager.GetConnectedDevices();
            int connectedCount = connected.Count;

            // 从数据库获取总装置数及最近同步时间
            int totalCount;
            DateTime? lastSync;
            try
            {
                totalCount = AppServices.DbContext.MeasurementDevices.Count(d => d.EnabledStatus == EnabledStatus.Enabled);
                lastSync = AppServices.DbContext.MeasurementDevices
                    .Where(d => d.LastSyncTime != null)
                    .Max(d => (DateTime?)d.LastSyncTime);
            }
            catch
            {
                ConnectionStatusText = "⚪ 设备状态查询失败";
                ConnectionBadgeBrush = Brushes.Gray;
                LastSyncTimeText = "查询失败";
                return;
            }

            if (totalCount == 0)
            {
                ConnectionStatusText = "⚪ 无已启用设备";
                ConnectionBadgeBrush = Brushes.Gray;
            }
            else if (connectedCount == 0)
            {
                ConnectionStatusText = $"🔴 设备 {connectedCount}/{totalCount} 在线";
                ConnectionBadgeBrush = Brushes.Red;
            }
            else
            {
                ConnectionStatusText = $"🟢 设备 {connectedCount}/{totalCount} 在线";
                ConnectionBadgeBrush = new SolidColorBrush(Color.FromRgb(16, 185, 129)); // emerald-500
            }

            // 更新最近同步时间
            if (lastSync.HasValue)
            {
                var diff = DateTime.Now - lastSync.Value;
                if (diff.TotalMinutes < 1)
                    LastSyncTimeText = "刚刚同步";
                else if (diff.TotalHours < 1)
                    LastSyncTimeText = $"{(int)diff.TotalMinutes}分钟前同步";
                else if (diff.TotalDays < 1)
                    LastSyncTimeText = $"{(int)diff.TotalHours}小时前同步";
                else
                    LastSyncTimeText = $"{(int)diff.TotalDays}天前同步";
            }
            else
            {
                LastSyncTimeText = "暂无同步";
            }
        }
        catch
        {
            ConnectionStatusText = "⚪ 连接管理器未就绪";
            LastSyncTimeText = "暂无同步";
        }
    }

    /// <summary>根据当前用户权限刷新导航项</summary>
    private void RefreshNavItems()
    {
        var allItems = new List<NavItemDef>
        {
            new NavItemDef("首页概览", "", NavigateOverviewCommand, null, () => IsOverviewActive),
            new NavItemDef("基础台账", "", NavigateMasterDataCommand, "masterdata:view", () => IsMasterDataActive),
            new NavItemDef("配方管理", "", NavigateRecipeCommand, "recipe:view", () => IsRecipeActive),
            new NavItemDef("试验记录", "", NavigateRecordsCommand, "records:view", () => IsRecordsActive),
            new NavItemDef("实时监视", "", NavigateRealtimeMonitorCommand, null, () => IsRealtimeMonitorActive),
            new NavItemDef("数据分析", "", NavigateAnalysisCommand, "analysis:view", () => IsAnalysisActive),
            new NavItemDef("系统设置", "", NavigateSystemManagementCommand, "system:view", () => IsSystemManagementActive),
        };

        NavItems.Clear();
        foreach (var item in allItems)
        {
            if (item.RequiredPermission == null || UserSession.HasPermission(item.RequiredPermission))
            {
                NavItems.Add(item);
            }
        }
    }
}

/// <summary>
/// 导航项定义
/// </summary>
public sealed class NavItemDef : INotifyPropertyChanged
{
    private readonly Func<bool> _isActiveCheck;
    private bool _isActive;

    public NavItemDef(string text, string iconGlyph, ICommand command, string? requiredPermission, Func<bool> isActiveCheck)
    {
        Text = text;
        IconGlyph = iconGlyph;
        Command = command;
        RequiredPermission = requiredPermission;
        _isActiveCheck = isActiveCheck;
        _isActive = isActiveCheck();
    }

    public string Text { get; }
    public string IconGlyph { get; }
    public ICommand Command { get; }
    public string? RequiredPermission { get; }

    public bool IsActive
    {
        get => _isActive;
        private set
        {
            if (_isActive != value)
            {
                _isActive = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsActive)));
            }
        }
    }

    internal void RefreshActive()
    {
        IsActive = _isActiveCheck();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
