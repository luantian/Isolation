using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace IsolationLeakage.App.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private object? _activePage;

    public MainViewModel()
    {
        OverviewPage = new OverviewViewModel();
        MasterDataPage = new MasterDataViewModel();
        RealtimeMonitorPage = new RealtimeMonitorViewModel();
        TestRecordsPage = new TestRecordsViewModel();
        StatisticsAnalysisPage = new StatisticsAnalysisViewModel();
        SystemManagementPage = new SystemManagementViewModel();

        NavigateOverview = new RelayCommand(() => ActivePage = OverviewPage);
        NavigateMasterData = new RelayCommand(() => ActivePage = MasterDataPage);
        NavigateRealtimeMonitor = new RelayCommand(() => ActivePage = RealtimeMonitorPage);
        NavigateRecords = new RelayCommand(() => ActivePage = TestRecordsPage);
        NavigateAnalysis = new RelayCommand(() => ActivePage = StatisticsAnalysisPage);
        NavigateSystemManagement = new RelayCommand(() => ActivePage = SystemManagementPage);

        ActivePage = OverviewPage;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public MasterDataViewModel MasterDataPage { get; }

    public OverviewViewModel OverviewPage { get; }

    public RealtimeMonitorViewModel RealtimeMonitorPage { get; }

    public TestRecordsViewModel TestRecordsPage { get; }

    public StatisticsAnalysisViewModel StatisticsAnalysisPage { get; }

    public SystemManagementViewModel SystemManagementPage { get; }

    public object? ActivePage
    {
        get => _activePage;
        set
        {
            if (SetField(ref _activePage, value))
            {
                OnPropertyChanged(nameof(IsOverviewActive));
                OnPropertyChanged(nameof(IsMasterDataActive));
                OnPropertyChanged(nameof(IsRealtimeMonitorActive));
                OnPropertyChanged(nameof(IsRecordsActive));
                OnPropertyChanged(nameof(IsAnalysisActive));
                OnPropertyChanged(nameof(IsSystemManagementActive));
            }
        }
    }

    public ICommand NavigateOverview { get; }

    public ICommand NavigateMasterData { get; }

    public ICommand NavigateRealtimeMonitor { get; }

    public ICommand NavigateRecords { get; }

    public ICommand NavigateAnalysis { get; }

    public ICommand NavigateSystemManagement { get; }

    public string CurrentPageTitle => ActivePage switch
    {
        OverviewViewModel => "首页概览",
        MasterDataViewModel => "基础台账",
        RealtimeMonitorViewModel => "实时监视",
        TestRecordsViewModel => "试验记录",
        StatisticsAnalysisViewModel => "统计分析",
        SystemManagementViewModel => "系统管理",
        _ => "首页概览"
    };

    public bool IsOverviewActive => ActivePage is OverviewViewModel;
    public bool IsMasterDataActive => ActivePage is MasterDataViewModel;
    public bool IsRealtimeMonitorActive => ActivePage is RealtimeMonitorViewModel;
    public bool IsRecordsActive => ActivePage is TestRecordsViewModel;
    public bool IsAnalysisActive => ActivePage is StatisticsAnalysisViewModel;
    public bool IsSystemManagementActive => ActivePage is SystemManagementViewModel;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        OnPropertyChanged(nameof(CurrentPageTitle));
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class RelayCommand : ICommand
{
    private readonly Action _execute;

    public RelayCommand(Action execute) => _execute = execute;

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => _execute();
}
