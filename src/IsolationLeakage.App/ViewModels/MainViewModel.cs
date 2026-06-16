using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using IsolationLeakage.App.Services.Security;

namespace IsolationLeakage.App.ViewModels;

/// <summary>
/// 主窗口视图模型
/// </summary>
public sealed class MainViewModel : ViewModelBase
{
    private object? _activePage;

    public MainViewModel()
    {
        OverviewPage = new OverviewViewModel();
        MasterDataPage = new MasterDataViewModel();
        TestRecordsPage = new TestRecordsViewModel();
        RealtimeMonitorPage = new RealtimeMonitorViewModel();
        StatisticsAnalysisPage = new StatisticsAnalysisViewModel();
        SystemManagementPage = new SystemManagementViewModel();

        NavigateOverviewCommand = new RelayCommand(() => ActivePage = OverviewPage);
        NavigateMasterDataCommand = new RelayCommand(() => ActivePage = MasterDataPage);
        NavigateRecordsCommand = new RelayCommand(() => ActivePage = TestRecordsPage);
        NavigateRealtimeMonitorCommand = new RelayCommand(() => ActivePage = RealtimeMonitorPage);
        NavigateAnalysisCommand = new RelayCommand(() => ActivePage = StatisticsAnalysisPage);
        NavigateSystemManagementCommand = new RelayCommand(() => ActivePage = SystemManagementPage);

        // 注册所有导航项（null 表示无需权限检查）
        NavItems.Add(new NavItemDef("首页概览", "", NavigateOverviewCommand, null, () => IsOverviewActive));
        NavItems.Add(new NavItemDef("基础台账", "", NavigateMasterDataCommand, "masterdata:view", () => IsMasterDataActive));
        NavItems.Add(new NavItemDef("试验记录", "", NavigateRecordsCommand, "records:view", () => IsRecordsActive));
        NavItems.Add(new NavItemDef("实时监视", "", NavigateRealtimeMonitorCommand, null, () => IsRealtimeMonitorActive));
        NavItems.Add(new NavItemDef("数据分析", "", NavigateAnalysisCommand, "analysis:view", () => IsAnalysisActive));
        NavItems.Add(new NavItemDef("系统设置", "", NavigateSystemManagementCommand, "system:view", () => IsSystemManagementActive));

        // 按权限过滤导航项
        RefreshNavItems();

        ActivePage = OverviewPage;
    }

    public ObservableCollection<NavItemDef> NavItems { get; } = [];

    public OverviewViewModel OverviewPage { get; }
    public MasterDataViewModel MasterDataPage { get; }
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
            }
        }
    }

    public ICommand NavigateOverviewCommand { get; }
    public ICommand NavigateMasterDataCommand { get; }
    public ICommand NavigateRecordsCommand { get; }
    public ICommand NavigateRealtimeMonitorCommand { get; }
    public ICommand NavigateAnalysisCommand { get; }
    public ICommand NavigateSystemManagementCommand { get; }

    public string CurrentPageTitle => ActivePage switch
    {
        OverviewViewModel => "首页概览",
        MasterDataViewModel => "基础台账",
        TestRecordsViewModel => "试验记录",
        RealtimeMonitorViewModel => "实时监视",
        StatisticsAnalysisViewModel => "数据分析",
        SystemManagementViewModel => "系统设置",
        _ => "首页概览"
    };

    public bool IsOverviewActive => ActivePage is OverviewViewModel;
    public bool IsMasterDataActive => ActivePage is MasterDataViewModel;
    public bool IsRecordsActive => ActivePage is TestRecordsViewModel;
    public bool IsRealtimeMonitorActive => ActivePage is RealtimeMonitorViewModel;
    public bool IsAnalysisActive => ActivePage is StatisticsAnalysisViewModel;
    public bool IsSystemManagementActive => ActivePage is SystemManagementViewModel;

    /// <summary>当前登录用户名（用于状态栏显示）</summary>
    public string CurrentUserName => UserSession.IsLoggedIn ? UserSession.DisplayName : "未登录";

    /// <summary>根据当前用户权限刷新导航项</summary>
    private void RefreshNavItems()
    {
        var allItems = new List<NavItemDef>
        {
            new NavItemDef("首页概览", "", NavigateOverviewCommand, null, () => IsOverviewActive),
            new NavItemDef("基础台账", "", NavigateMasterDataCommand, "masterdata:view", () => IsMasterDataActive),
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
