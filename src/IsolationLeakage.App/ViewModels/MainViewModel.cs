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
        StatisticsAnalysisPage = new StatisticsAnalysisViewModel();
        SystemManagementPage = new SystemManagementViewModel();

        NavigateOverviewCommand = new RelayCommand(() => ActivePage = OverviewPage);
        NavigateMasterDataCommand = new RelayCommand(() => ActivePage = MasterDataPage);
        NavigateRecordsCommand = new RelayCommand(() => ActivePage = TestRecordsPage);
        NavigateAnalysisCommand = new RelayCommand(() => ActivePage = StatisticsAnalysisPage);
        NavigateSystemManagementCommand = new RelayCommand(() => ActivePage = SystemManagementPage);

        ActivePage = OverviewPage;
    }

    public OverviewViewModel OverviewPage { get; }
    public MasterDataViewModel MasterDataPage { get; }
    public TestRecordsViewModel TestRecordsPage { get; }
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
                OnPropertyChanged(nameof(IsAnalysisActive));
                OnPropertyChanged(nameof(IsSystemManagementActive));
                OnPropertyChanged(nameof(CurrentPageTitle));
            }
        }
    }

    public ICommand NavigateOverviewCommand { get; }
    public ICommand NavigateMasterDataCommand { get; }
    public ICommand NavigateRecordsCommand { get; }
    public ICommand NavigateAnalysisCommand { get; }
    public ICommand NavigateSystemManagementCommand { get; }

    public string CurrentPageTitle => ActivePage switch
    {
        OverviewViewModel => "首页概览",
        MasterDataViewModel => "基础台账",
        TestRecordsViewModel => "试验记录",
        StatisticsAnalysisViewModel => "数据分析",
        SystemManagementViewModel => "系统设置",
        _ => "首页概览"
    };

    public bool IsOverviewActive => ActivePage is OverviewViewModel;
    public bool IsMasterDataActive => ActivePage is MasterDataViewModel;
    public bool IsRecordsActive => ActivePage is TestRecordsViewModel;
    public bool IsAnalysisActive => ActivePage is StatisticsAnalysisViewModel;
    public bool IsSystemManagementActive => ActivePage is SystemManagementViewModel;

    /// <summary>当前登录用户名（用于状态栏显示）</summary>
    public string CurrentUserName => UserSession.IsLoggedIn ? UserSession.DisplayName : "未登录";
}
