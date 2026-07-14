using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using IsolationLeakage.App.Data;
using IsolationLeakage.App.Models;
using IsolationLeakage.App.Models.Database;
using Microsoft.EntityFrameworkCore;

namespace IsolationLeakage.App.ViewModels;

public sealed class OverviewViewModel : ViewModelBase, IRefreshable
{
    private string _testObjectValue = "0";
    private string _deviceValue = "0";
    private string _recordValue = "0";
    private string _passRateValue = "0";
    private string _anomalyValue = "0";
    private string _backupValue = "--:--";
    private string _dbConnectionText = "数据库连接中...";
    private ObservableCollection<PreviewRecord> _previewRecords = [];

    // 台账概况数据
    private int _projectCount;
    private int _unitCount;
    private int _systemCount;
    private int _penetrationCount;
    private int _valveCount;
    private int _componentCount;
    private int _recordCount;
    private int _passCount;
    private int _failCount;

    // 最近导入详情
    private string _latestImportObjectCode = "—";
    private string _latestImportLeakageRate = "—";
    private string _latestImportResult = "—";
    private Brush _latestImportResultBrush = Brushes.Gray;
    private string _latestImportTime = "—";
    private string _latestImportDevice = "—";
    private string _latestImportDataStatus = "—";
    private string _latestImportOperator = "—";

    // 装置状态
    private ObservableCollection<DeviceStatusItem> _deviceStatusItems = [];
    private string _communicationSummaryText = "—";

    public OverviewViewModel()
    {
        _ = LoadDataAsync();
    }

    /// <summary>数据库连接状态文字</summary>
    public string DbConnectionText
    {
        get => _dbConnectionText;
        set => SetProperty(ref _dbConnectionText, value);
    }

    public string TestObjectTitle => "试验对象";
    public string TestObjectValue
    {
        get => _testObjectValue;
        set => SetProperty(ref _testObjectValue, value);
    }
    public string TestObjectUnit => "个";
    public string TestObjectDesc => "阀门 / 部件";
    public string TestObjectStatus => "台账";

    public string DeviceTitle => "测量装置";
    public string DeviceValue
    {
        get => _deviceValue;
        set => SetProperty(ref _deviceValue, value);
    }
    public string DeviceUnit => "台";
    public string DeviceDesc => "在线 / 离线";
    public string DeviceStatus => "装置";

    public string RecordTitle => "历史记录";
    public string RecordValue
    {
        get => _recordValue;
        set => SetProperty(ref _recordValue, value);
    }
    public string RecordUnit => "条";
    public string RecordDesc => "按时间顺序保存";
    public string RecordStatus => "记录";

    public string PassRateTitle => "本月合格率";
    public string PassRateValue
    {
        get => _passRateValue;
        set => SetProperty(ref _passRateValue, value);
    }
    public string PassRateUnit => "%";
    public string PassRateDesc => "按导入记录统计";
    public string PassRateStatus => "统计";

    public string AnomalyTitle => "待处理异常";
    public string AnomalyValue
    {
        get => _anomalyValue;
        set => SetProperty(ref _anomalyValue, value);
    }
    public string AnomalyUnit => "项";
    public string AnomalyDesc => "不合格 / 导入异常";
    public string AnomalyStatus => "异常";

    public string BackupTitle => "最近备份";
    public string BackupValue
    {
        get => _backupValue;
        set => SetProperty(ref _backupValue, value);
    }
    public string BackupUnit => "";
    private string _backupDesc = "自动备份";
    public string BackupDesc
    {
        get => _backupDesc;
        set => SetProperty(ref _backupDesc, value);
    }
    public string BackupStatus => "完整";

    public ObservableCollection<PreviewRecord> PreviewRecords
    {
        get => _previewRecords;
        set => SetProperty(ref _previewRecords, value);
    }

    // 台账概况属性
    public int ProjectCount
    {
        get => _projectCount;
        set => SetProperty(ref _projectCount, value);
    }
    public int UnitCount
    {
        get => _unitCount;
        set => SetProperty(ref _unitCount, value);
    }
    public int SystemCount
    {
        get => _systemCount;
        set => SetProperty(ref _systemCount, value);
    }
    public int PenetrationCount
    {
        get => _penetrationCount;
        set => SetProperty(ref _penetrationCount, value);
    }
    public int ValveCount
    {
        get => _valveCount;
        set => SetProperty(ref _valveCount, value);
    }
    public int ComponentCount
    {
        get => _componentCount;
        set => SetProperty(ref _componentCount, value);
    }
    public int RecordCount
    {
        get => _recordCount;
        set => SetProperty(ref _recordCount, value);
    }
    public int PassCount
    {
        get => _passCount;
        set => SetProperty(ref _passCount, value);
    }
    public int FailCount
    {
        get => _failCount;
        set => SetProperty(ref _failCount, value);
    }

    // ================ 最近导入详情 ================
    public string LatestImportObjectCode
    {
        get => _latestImportObjectCode;
        set => SetProperty(ref _latestImportObjectCode, value);
    }
    public string LatestImportLeakageRate
    {
        get => _latestImportLeakageRate;
        set => SetProperty(ref _latestImportLeakageRate, value);
    }
    public string LatestImportResult
    {
        get => _latestImportResult;
        set => SetProperty(ref _latestImportResult, value);
    }
    public Brush LatestImportResultBrush
    {
        get => _latestImportResultBrush;
        set => SetProperty(ref _latestImportResultBrush, value);
    }
    public string LatestImportTime
    {
        get => _latestImportTime;
        set => SetProperty(ref _latestImportTime, value);
    }
    public string LatestImportDevice
    {
        get => _latestImportDevice;
        set => SetProperty(ref _latestImportDevice, value);
    }
    public string LatestImportDataStatus
    {
        get => _latestImportDataStatus;
        set => SetProperty(ref _latestImportDataStatus, value);
    }
    public string LatestImportOperator
    {
        get => _latestImportOperator;
        set => SetProperty(ref _latestImportOperator, value);
    }

    // ================ 装置状态 ================
    public ObservableCollection<DeviceStatusItem> DeviceStatusItems
    {
        get => _deviceStatusItems;
        set => SetProperty(ref _deviceStatusItems, value);
    }
    public string CommunicationSummaryText
    {
        get => _communicationSummaryText;
        set => SetProperty(ref _communicationSummaryText, value);
    }

    Task IRefreshable.RefreshAsync() => LoadDataAsync();

    public async Task LoadDataAsync()
    {
        try
        {
            using var context = DbContextFactory.CreateDbContext();

            // 数据库连接成功
            var serverInfo = context.Database.GetDbConnection().DataSource;
            var dbName = context.Database.GetDbConnection().Database;
            DbConnectionText = $"✓ {serverInfo} / {dbName}";

            // 1. 台账概况统计
            ProjectCount = await context.Projects.CountAsync();
            UnitCount = await context.Units.CountAsync();
            SystemCount = await context.TestObjectPathNodes.CountAsync(n => n.NodeType == Models.PathNodeType.System);
            PenetrationCount = await context.TestObjectPathNodes.CountAsync(n => n.NodeType == Models.PathNodeType.Penetration);
            ValveCount = await context.TestObjectPathNodes.CountAsync(n => n.NodeType == Models.PathNodeType.Valve);
            ComponentCount = await context.TestObjectPathNodes.CountAsync(n => n.NodeType == Models.PathNodeType.OtherComponent);

            RecordCount = await context.TestRecords.CountAsync();
            PassCount = await context.TestRecords.CountAsync(r => r.Result == Models.TestResult.Pass);
            FailCount = await context.TestRecords.CountAsync(r => r.Result == Models.TestResult.Fail);

            // 顶部指标
            var testObjectCount = ValveCount + ComponentCount;
            TestObjectValue = testObjectCount.ToString();

            // 顶部指标（仅统计启用装置，排除系统默认装置"未指定"）
            var enabledDeviceCount = await context.MeasurementDevices
                .CountAsync(d => d.EnabledStatus == EnabledStatus.Enabled && d.DeviceCode != "未指定");
            DeviceValue = enabledDeviceCount.ToString();

            RecordValue = RecordCount.ToString();

            // 4. Pass rate and anomaly count from last 30 days
            var thirtyDaysAgo = DateTime.Now.AddDays(-30);
            var recentRecords = await context.TestRecords
                .Where(r => r.TestTime >= thirtyDaysAgo)
                .ToListAsync();

            if (recentRecords.Any())
            {
                // 合格率只算有明确结果的（排除 Unknown）
                var judgedRecords = recentRecords
                    .Where(r => r.Result == Models.TestResult.Pass || r.Result == Models.TestResult.Fail)
                    .ToList();

                if (judgedRecords.Count > 0)
                {
                    var passCountRecent = judgedRecords.Count(r => r.Result == Models.TestResult.Pass);
                    var passRate = (double)passCountRecent / judgedRecords.Count * 100;
                    PassRateValue = passRate.ToString("F1");
                }
                else
                {
                    PassRateValue = "0";
                }

                var failCountRecent = recentRecords.Count(r => r.Result == Models.TestResult.Fail);
                AnomalyValue = failCountRecent.ToString();
            }
            else
            {
                PassRateValue = "0";
                AnomalyValue = "0";
            }

            // 5. Last backup time (使用 AutoBackupService 获取真实状态)
            var backupService = Services.AutoBackupService.Instance;
            var backupDir = backupService.BackupDirectory;

            if (backupService.LastBackupSucceeded == false && !string.IsNullOrEmpty(backupService.LastBackupError))
            {
                // 上次备份失败，显示错误
                BackupValue = "失败";
                BackupDesc = backupService.LastBackupError.Length > 30
                    ? backupService.LastBackupError.Substring(0, 30) + "..."
                    : backupService.LastBackupError;
            }
            else if (!backupService.AutoBackupEnabled)
            {
                BackupValue = "禁用";
                BackupDesc = "自动备份已关闭";
            }
            else if (Directory.Exists(backupDir))
            {
                var latestBackup = new DirectoryInfo(backupDir)
                    .GetFiles("*.bak")
                    .OrderByDescending(f => f.LastWriteTime)
                    .FirstOrDefault();

                if (latestBackup != null)
                {
                    BackupValue = latestBackup.LastWriteTime.ToString("HH:mm:ss");
                    BackupDesc = latestBackup.LastWriteTime.ToString("yyyy-MM-dd") + " 自动备份";
                }
                else
                {
                    BackupValue = "--:--";
                    BackupDesc = "等待首次备份";
                }
            }
            else
            {
                BackupValue = "--:--";
                BackupDesc = "等待首次备份";
            }

            // 6. Latest 5 TestRecords
            var latestRecords = await context.TestRecords
                .OrderByDescending(r => r.TestTime)
                .Take(5)
                .Include(r => r.Unit)
                .Include(r => r.TestObject)
                .ToListAsync();

            var previewList = new List<PreviewRecord>();
            foreach (var record in latestRecords)
            {
                previewList.Add(new PreviewRecord(
                    ObjectCode: record.ObjectCode,
                    Unit: record.Unit?.Name ?? record.UnitCode,
                    LeakageRate: $"{record.FinalLeakageRate:F3} L/min",
                    Result: record.Result == Models.TestResult.Pass ? "合格" : "不合格",
                    UploadedAt: record.TestTime.ToString("yyyy-MM-dd HH:mm:ss")
                ));
            }

            PreviewRecords.Clear();
            foreach (var record in previewList)
            {
                PreviewRecords.Add(record);
            }

            // 7. 最近导入详情（最新一条记录）
            var latestRecord = await context.TestRecords
                .OrderByDescending(r => r.ImportTime)
                .Include(r => r.Device)
                .FirstOrDefaultAsync();

            if (latestRecord != null)
            {
                LatestImportObjectCode = latestRecord.ObjectCode;
                LatestImportLeakageRate = $"{latestRecord.FinalLeakageRate:F3} L/min";
                LatestImportResult = latestRecord.Result == Models.TestResult.Pass ? "合格" : "不合格";
                LatestImportResultBrush = latestRecord.Result == Models.TestResult.Pass ? Brushes.ForestGreen : Brushes.Red;
                LatestImportTime = latestRecord.ImportTime.ToString("yyyy-MM-dd HH:mm:ss");
                LatestImportDevice = latestRecord.DeviceCode;
                LatestImportOperator = latestRecord.Operator;

                // 判断是否为首次导入（该对象之前无记录）
                var priorCount = await context.TestRecords
                    .CountAsync(r => r.ObjectCode == latestRecord.ObjectCode
                                  && r.TestTime < latestRecord.TestTime);
                LatestImportDataStatus = priorCount == 0 ? "首次导入 / 已入库" : "追加导入 / 未覆盖历史";
            }
            else
            {
                LatestImportObjectCode = "—";
                LatestImportLeakageRate = "—";
                LatestImportResult = "—";
                LatestImportResultBrush = Brushes.Gray;
                LatestImportTime = "—";
                LatestImportDevice = "—";
                LatestImportDataStatus = "暂无数据";
                LatestImportOperator = "—";
            }

            // 8. 装置状态（仅显示启用状态的装置，排除系统默认装置"未指定"）
            var devices = await context.MeasurementDevices
                .Where(d => d.EnabledStatus == EnabledStatus.Enabled && d.DeviceCode != "未指定")
                .OrderByDescending(d => d.LastUploadTime)
                .ToListAsync();

            var statusItems = new List<DeviceStatusItem>();
            var commTypes = new HashSet<string>();
            foreach (var d in devices)
            {
                commTypes.Add(d.PrimaryCommunicationText);

                string statusText;
                Brush statusBrush;
                if (d.EnabledStatus == EnabledStatus.Disabled)
                {
                    statusText = "停用";
                    statusBrush = Brushes.Gray;
                }
                else if (d.ConnectionStatus == ConnectionStatus.Online)
                {
                    var timeStr = d.LastSyncTime?.ToString("HH:mm:ss") ?? "—";
                    statusText = $"在线 {timeStr}";
                    statusBrush = Brushes.ForestGreen;
                }
                else
                {
                    var timeStr = d.LastSyncTime.HasValue
                        ? FormatRelativeTime(d.LastSyncTime.Value)
                        : "未知";
                    statusText = $"离线 {timeStr}";
                    statusBrush = Brushes.Red;
                }

                statusItems.Add(new DeviceStatusItem(d.DeviceCode, statusText, statusBrush));
            }

            DeviceStatusItems = new ObservableCollection<DeviceStatusItem>(statusItems);
            CommunicationSummaryText = commTypes.Count > 0
                ? string.Join(" / ", commTypes.Order())
                : "—";
        }
        catch (Exception ex)
        {
            // Error handling: log exception and set default values
            System.Diagnostics.Debug.WriteLine($"Error loading overview data: {ex.Message}");

            DbConnectionText = $"✗ 连接失败: {ex.Message}";

            TestObjectValue = "0";
            DeviceValue = "0";
            RecordValue = "0";
            PassRateValue = "0";
            AnomalyValue = "0";
            BackupValue = "--:--";
            PreviewRecords = [];

            ProjectCount = 0;
            UnitCount = 0;
            SystemCount = 0;
            PenetrationCount = 0;
            ValveCount = 0;
            ComponentCount = 0;
            RecordCount = 0;
            PassCount = 0;
            FailCount = 0;

            LatestImportObjectCode = "—";
            LatestImportLeakageRate = "—";
            LatestImportResult = "—";
            LatestImportResultBrush = Brushes.Gray;
            LatestImportTime = "—";
            LatestImportDevice = "—";
            LatestImportDataStatus = "—";
            LatestImportOperator = "—";
            DeviceStatusItems = [];
            CommunicationSummaryText = "—";
        }
    }

    private static string FormatRelativeTime(DateTime time)
    {
        var diff = DateTime.Now - time;
        if (diff.TotalMinutes < 1) return "刚刚";
        if (diff.TotalHours < 1) return $"{(int)diff.TotalMinutes}分钟前";
        if (diff.TotalDays < 1) return $"{(int)diff.TotalHours}小时前";
        if (diff.TotalDays < 30) return $"{(int)diff.TotalDays}天前";
        return time.ToString("MM-dd HH:mm:ss");
    }
}

public sealed record PreviewRecord(string ObjectCode, string Unit, string LeakageRate, string Result, string UploadedAt);

public sealed record DeviceStatusItem(string DeviceCode, string StatusText, System.Windows.Media.Brush StatusBrush);
