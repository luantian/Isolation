using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using IsolationLeakage.App.Data;
using IsolationLeakage.App.Models;
using IsolationLeakage.App.Models.Database;
using IsolationLeakage.App.Services;
using IsolationLeakage.App.Services.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Win32;

namespace IsolationLeakage.App.ViewModels;

/// <summary>
/// 项目/机组管理视图模型
/// </summary>
public sealed class ProjectUnitManagementViewModel : ViewModelBase, IRefreshable
{
    private Project? _selectedProject;
    private Unit? _selectedUnit;
    private string _message = string.Empty;
    private bool _isBatchImporting;
    private int _batchImportProgress;
    private string _batchImportStatus = string.Empty;

    public ProjectUnitManagementViewModel()
    {
        Projects = new ObservableCollection<Project>();
        Units = new ObservableCollection<Unit>();

        // 初始化命令
        AddProjectCommand = new RelayCommand(() => _ = ShowAddProjectDialogAsync(), () => PermissionGuard.Can(Perms.ProjectAdd));
        EditProjectCommand = new RelayCommand(() => _ = ShowEditProjectDialogAsync(), () => SelectedProject != null && PermissionGuard.Can(Perms.ProjectAdd));
        AddUnitCommand = new RelayCommand(() => _ = ShowAddUnitDialogAsync(), () => SelectedProject != null && PermissionGuard.Can(Perms.ProjectAdd));
        EditUnitCommand = new RelayCommand(() => _ = ShowEditUnitDialogAsync(), () => SelectedUnit != null && PermissionGuard.Can(Perms.ProjectAdd));
        ImportBatchDataCommand = new RelayCommand(() => _ = ImportBatchDataAsync(), () => PermissionGuard.Can(Perms.RecordsUpload) && !IsBatchImporting);
        CancelBatchImportCommand = new RelayCommand(() => _batchImportCts?.Cancel(), () => IsBatchImporting);
        DeleteProjectCommand = new RelayCommand(() => _ = DeleteProjectAsync(), () => SelectedProject != null && PermissionGuard.Can(Perms.ProjectDelete));
        DeleteUnitCommand = new RelayCommand(() => _ = DeleteUnitAsync(), () => SelectedUnit != null && PermissionGuard.Can(Perms.ProjectDelete));

        // 从数据库加载数据
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

    public ObservableCollection<Project> Projects { get; }

    public ObservableCollection<Unit> Units { get; }

    public IEnumerable<Unit> CurrentUnits => SelectedProject == null
        ? Enumerable.Empty<Unit>()
        : Units.Where(u => u.ProjectCode == SelectedProject.Code);

    public Project? SelectedProject
    {
        get => _selectedProject;
        set
        {
            if (SetProperty(ref _selectedProject, value))
            {
                OnPropertyChanged(nameof(CurrentUnits));
                // 通知命令状态更新
                ((RelayCommand)EditProjectCommand).NotifyCanExecuteChanged();
                ((RelayCommand)DeleteProjectCommand).NotifyCanExecuteChanged();
                ((RelayCommand)AddUnitCommand).NotifyCanExecuteChanged();
            }
        }
    }

    public Unit? SelectedUnit
    {
        get => _selectedUnit;
        set
        {
            if (SetProperty(ref _selectedUnit, value))
            {
                // 通知命令状态更新
                ((RelayCommand)EditUnitCommand).NotifyCanExecuteChanged();
                ((RelayCommand)DeleteUnitCommand).NotifyCanExecuteChanged();
            }
        }
    }

    public string Message
    {
        get => _message;
        set => SetProperty(ref _message, value);
    }

    /// <summary>是否正在批量导入中</summary>
    public bool IsBatchImporting
    {
        get => _isBatchImporting;
        private set
        {
            if (SetProperty(ref _isBatchImporting, value))
            {
                ((RelayCommand)ImportBatchDataCommand).NotifyCanExecuteChanged();
                ((RelayCommand)CancelBatchImportCommand).NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>批量导入进度百分比（0-100）</summary>
    public int BatchImportProgress
    {
        get => _batchImportProgress;
        private set => SetProperty(ref _batchImportProgress, value);
    }

    /// <summary>批量导入状态文本</summary>
    public string BatchImportStatus
    {
        get => _batchImportStatus;
        private set => SetProperty(ref _batchImportStatus, value);
    }

    public IRelayCommand AddProjectCommand { get; }
    public IRelayCommand EditProjectCommand { get; }
    public IRelayCommand AddUnitCommand { get; }
    public IRelayCommand EditUnitCommand { get; }
    public IRelayCommand ImportBatchDataCommand { get; }

    /// <summary>取消正在进行的批量导入（已导入的数据保留）</summary>
    public IRelayCommand CancelBatchImportCommand { get; }

    public IRelayCommand DeleteProjectCommand { get; }
    public IRelayCommand DeleteUnitCommand { get; }

    // 批量导入取消源（导入期间有效，结束/异常后释放）
    private CancellationTokenSource? _batchImportCts;

    /// <summary>切换到本页时重新从数据库加载（其他页面导入后能看到新数据）</summary>
    public Task RefreshAsync() => LoadDataAsync();

    private async Task LoadDataAsync()
    {
        try
        {
            using var context = DbContextFactory.CreateDbContext();

            var projects = await context.Projects.AsNoTracking().ToListAsync();
            Projects.Clear();
            foreach (var p in projects) Projects.Add(p);

            var units = await context.Units.AsNoTracking().Include(u => u.Project).ToListAsync();
            Units.Clear();
            foreach (var u in units) Units.Add(u);

            SelectedProject = Projects.FirstOrDefault();
            Message = $"已从数据库加载 {Projects.Count} 个项目，{Units.Count} 个机组";
        }
        catch (Exception ex)
        {
            Message = $"加载数据失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 按已有编号的最大连番 +1 生成下一个序号（而非用集合数量，避免删除后重复）。
    /// 从每个以 prefix 开头的编号中取出紧随其后的数字部分，取最大值 +1。
    /// </summary>
    private static int NextSequence(IEnumerable<string> existingCodes, string prefix)
    {
        int max = 0;
        foreach (var code in existingCodes)
        {
            if (string.IsNullOrEmpty(code) || !code.StartsWith(prefix)) continue;
            var tail = code[prefix.Length..];
            // 只取开头连续的数字部分
            var digits = new string(tail.TakeWhile(char.IsDigit).ToArray());
            if (int.TryParse(digits, out var n) && n > max) max = n;
        }
        return max + 1;
    }

    public async Task ShowAddProjectDialogAsync()
    {
        if (!PermissionGuard.Can(Perms.ProjectAdd)) return;

        var projectPrefix = $"P{DateTime.Now:yyMM}";
        var newProject = new Project
        {
            Code = $"{projectPrefix}{NextSequence(Projects.Select(p => p.Code), projectPrefix):D2}",
            Name = string.Empty,
            Status = EnabledStatus.Enabled,
            Remark = string.Empty,
            CreatedAt = DateTime.Now
        };

        var dialog = new Views.ProjectEditDialog(newProject)
        {
            Title = "新增项目",
            Owner = Application.Current.MainWindow
        };

        if (dialog.ShowDialog() == true)
        {
            try
            {
                using var context = DbContextFactory.CreateDbContext();
                var logService = new OperationLogService(context);
                var currentUser = Services.Security.UserSession.Current?.User.UserName ?? "system";

                if (await context.Projects.AnyAsync(p => p.Code == newProject.Code || p.Name == newProject.Name))
                {
                    MessageBox.Show("项目编号或名称已存在", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                context.Projects.Add(newProject);
                await logService.LogAsync("创建项目", currentUser,
                    $"新增项目【{newProject.Name}】({newProject.Code})", "Success");
                await context.SaveChangesAsync();

                Projects.Add(newProject);
                SelectedProject = newProject;
                Message = $"✅ 已新增项目并保存到数据库：{newProject.Name}";
            }
            catch (Exception ex)
            {
                Message = $"❌ 新增项目失败：{ex.Message}";
            }
        }
    }

    public async Task ShowEditProjectDialogAsync()
    {
        if (SelectedProject == null || !PermissionGuard.Can(Perms.ProjectAdd)) return;

        // 创建一个副本用于编辑
        var editProject = new Project
        {
            Code = SelectedProject.Code,
            Name = SelectedProject.Name,
            Status = SelectedProject.Status,
            Remark = SelectedProject.Remark,
            CreatedAt = SelectedProject.CreatedAt
        };

        var dialog = new Views.ProjectEditDialog(editProject)
        {
            Title = "编辑项目",
            Owner = Application.Current.MainWindow
        };

        if (dialog.ShowDialog() == true)
        {
            try
            {
                using var context = DbContextFactory.CreateDbContext();
                var logService = new OperationLogService(context);
                var currentUser = Services.Security.UserSession.Current?.User.UserName ?? "system";

                var project = await context.Projects.FindAsync(SelectedProject.Code);
                if (project == null)
                {
                    MessageBox.Show("项目在数据库中不存在", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                project.Name = editProject.Name;
                project.Remark = editProject.Remark;
                project.Status = editProject.Status;

                await logService.LogAsync("修改项目", currentUser,
                    $"修改项目【{project.Name}】({project.Code})", "Success");
                await context.SaveChangesAsync();

                // 更新 UI
                SelectedProject.Name = project.Name;
                SelectedProject.Remark = project.Remark;
                SelectedProject.Status = project.Status;
                OnPropertyChanged(nameof(SelectedProject));

                // 同步更新相关机组的所属项目引用，使机组列表中的"所属项目"名称实时更新
                foreach (var u in Units.Where(u => u.ProjectCode == SelectedProject.Code))
                {
                    u.Project = SelectedProject;
                }

                Message = $"✅ 已保存项目修改：{project.Name}";
            }
            catch (Exception ex)
            {
                Message = $"❌ 修改项目失败：{ex.Message}";
            }
        }
    }

    public async Task ShowAddUnitDialogAsync()
    {
        if (SelectedProject == null || !PermissionGuard.Can(Perms.ProjectAdd)) return;

        var unitPrefix = $"{SelectedProject.Code}-";
        var newUnit = new Unit
        {
            Code = $"{unitPrefix}{NextSequence(Units.Where(u => u.ProjectCode == SelectedProject.Code).Select(u => u.Code), unitPrefix):D2}",
            Name = string.Empty,
            ProjectCode = SelectedProject.Code,
            Status = EnabledStatus.Enabled,
            Remark = string.Empty,
            CreatedAt = DateTime.Now
        };

        var dialog = new Views.UnitEditDialog(newUnit)
        {
            Title = "新增机组",
            Owner = Application.Current.MainWindow
        };

        if (dialog.ShowDialog() == true)
        {
            try
            {
                using var context = DbContextFactory.CreateDbContext();
                var logService = new OperationLogService(context);
                var currentUser = Services.Security.UserSession.Current?.User.UserName ?? "system";

                var project = await context.Projects.FindAsync(SelectedProject.Code);
                if (project == null)
                {
                    MessageBox.Show("所选项目在数据库中不存在", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // 唯一性校验：同一编号不可重复；同一项目下机组名不可重复（与项目新增逻辑保持一致）
                if (await context.Units.AnyAsync(u => u.Code == newUnit.Code
                        || (u.ProjectCode == newUnit.ProjectCode && u.Name == newUnit.Name)))
                {
                    MessageBox.Show("机组编号或名称已存在", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                newUnit.Project = project;
                context.Units.Add(newUnit);

                await logService.LogAsync("创建机组", currentUser,
                    $"新增机组【{newUnit.Name}】({newUnit.Code}) 所属项目【{SelectedProject.Name}】", "Success");
                await context.SaveChangesAsync();

                Units.Add(newUnit);
                OnPropertyChanged(nameof(CurrentUnits));
                Message = $"✅ 已新增机组并保存到数据库：{newUnit.Name}";
            }
            catch (Exception ex)
            {
                Message = $"❌ 新增机组失败：{ex.Message}";
            }
        }
    }

    public async Task ShowEditUnitDialogAsync()
    {
        if (SelectedUnit == null || !PermissionGuard.Can(Perms.ProjectAdd)) return;

        // 创建一个副本用于编辑
        var editUnit = new Unit
        {
            Code = SelectedUnit.Code,
            Name = SelectedUnit.Name,
            ProjectCode = SelectedUnit.ProjectCode,
            Status = SelectedUnit.Status,
            Remark = SelectedUnit.Remark,
            CreatedAt = SelectedUnit.CreatedAt
        };

        var dialog = new Views.UnitEditDialog(editUnit)
        {
            Title = "编辑机组",
            Owner = Application.Current.MainWindow
        };

        if (dialog.ShowDialog() == true)
        {
            try
            {
                using var context = DbContextFactory.CreateDbContext();
                var logService = new OperationLogService(context);
                var currentUser = Services.Security.UserSession.Current?.User.UserName ?? "system";

                var unit = await context.Units.FindAsync(SelectedUnit.Code);
                if (unit == null)
                {
                    MessageBox.Show("机组在数据库中不存在", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                unit.Name = editUnit.Name;
                unit.Remark = editUnit.Remark;
                unit.Status = editUnit.Status;

                await logService.LogAsync("修改机组", currentUser,
                    $"修改机组【{unit.Name}】({unit.Code}) 所属项目【{SelectedProject?.Name}】", "Success");
                await context.SaveChangesAsync();

                // 更新 UI
                SelectedUnit.Name = unit.Name;
                SelectedUnit.Remark = unit.Remark;
                SelectedUnit.Status = unit.Status;
                OnPropertyChanged(nameof(SelectedUnit));
                Message = $"✅ 已保存机组修改：{unit.Name}";
            }
            catch (Exception ex)
            {
                Message = $"❌ 修改机组失败：{ex.Message}";
            }
        }
    }

    public async Task DeleteProjectAsync()
    {
        if (!PermissionGuard.Can(Perms.ProjectDelete)) return;
        if (SelectedProject == null)
        {
            Message = "请先选择要删除的项目";
            return;
        }

        // 实时采集保护：正在采集的项目不能删除
        if (Application.Current.MainWindow?.DataContext is MainViewModel mainVm)
        {
            var monitor = mainVm.RealtimeMonitorPage;
            if (monitor.IsMonitoring)
            {
                var monitoredProjectCode = monitor.SelectedProject?.Code;
                if (monitoredProjectCode == SelectedProject.Code)
                {
                    MessageBox.Show(
                        $"项目【{SelectedProject.Name}】下有正在采集的试验对象，无法删除。\n\n请先停止实时采集后再删除。",
                        "无法删除", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }
        }

        // 检查项目下是否有机组
        var unitsUnderProject = Units.Where(u => u.ProjectCode == SelectedProject.Code).ToList();

        if (unitsUnderProject.Count > 0)
        {
            var result = MessageBox.Show(
                $"项目【{SelectedProject.Name}】下有 {unitsUnderProject.Count} 个机组，删除项目将同时删除：\n" +
                $"- 所有机组\n" +
                $"- 所有试验对象路径节点\n" +
                $"- 所有试验数据\n\n" +
                $"确认删除吗？",
                "删除确认",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes)
                return;
        }
        else
        {
            var result = MessageBox.Show(
                $"确认删除项目【{SelectedProject.Name}】吗？",
                "删除确认",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes)
                return;
        }

        try
        {
            using var context = DbContextFactory.CreateDbContext();
            var logService = new OperationLogService(context);
            var currentUser = Services.Security.UserSession.Current?.User.UserName ?? "system";

            var project = await context.Projects.FindAsync(SelectedProject.Code);
            if (project == null)
            {
                Message = "项目在数据库中不存在";
                return;
            }

            // 删除项目下的所有机组
            var units = await context.Units.Where(u => u.ProjectCode == SelectedProject.Code).ToListAsync();

            // 删除所有机组下的试验对象路径节点
            var unitCodes = units.Select(u => u.Code).ToList();
            var pathNodes = await context.TestObjectPathNodes
                .Where(n => unitCodes.Contains(n.UnitCode))
                .ToListAsync();
            context.TestObjectPathNodes.RemoveRange(pathNodes);

            // 删除所有机组下的试验记录
            var testRecords = await context.TestRecords
                .Where(r => unitCodes.Contains(r.UnitCode))
                .ToListAsync();
            context.TestRecords.RemoveRange(testRecords);

            // 删除机组
            context.Units.RemoveRange(units);

            // 删除项目
            context.Projects.Remove(project);

            await logService.LogAsync("删除项目", currentUser,
                $"删除项目【{project.Name}】({project.Code})，" +
                $"同时删除 {units.Count} 个机组，{pathNodes.Count} 个路径节点，{testRecords.Count} 条试验记录", "Success");

            await context.SaveChangesAsync();

            // 更新 UI：内存中的 Units 是 LoadData 的 AsNoTracking 实例，与上面从库里查出的 units 不是同一对象，
            // 按引用 Remove 会失效（残留脏数据），故改为按 ProjectCode 从内存集合移除。
            var deletedCode = SelectedProject.Code;
            Projects.Remove(SelectedProject);
            foreach (var u in Units.Where(u => u.ProjectCode == deletedCode).ToList())
                Units.Remove(u);

            SelectedProject = Projects.FirstOrDefault();
            SelectedUnit = null;
            OnPropertyChanged(nameof(CurrentUnits));
            Message = $"✅ 已删除项目【{project.Name}】，{units.Count} 个机组，{pathNodes.Count} 个路径节点";
        }
        catch (Exception ex)
        {
            Message = $"❌ 删除项目失败：{ex.Message}";
        }
    }

    public async Task DeleteUnitAsync()
    {
        if (!PermissionGuard.Can(Perms.ProjectDelete)) return;
        if (SelectedProject == null)
        {
            Message = "请先选择项目";
            return;
        }

        if (SelectedUnit == null)
        {
            Message = "请先在列表中选择要删除的机组";
            return;
        }

        // 实时采集保护：正在采集的机组不能删除
        if (Application.Current.MainWindow?.DataContext is MainViewModel mainVm)
        {
            var monitor = mainVm.RealtimeMonitorPage;
            if (monitor.IsMonitoring)
            {
                var monitoredUnitCode = monitor.SelectedUnit?.Code;
                if (monitoredUnitCode == SelectedUnit.Code)
                {
                    MessageBox.Show(
                        $"机组【{SelectedUnit.Name}】下有正在采集的试验对象，无法删除。\n\n请先停止实时采集后再删除。",
                        "无法删除", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }
        }

        try
        {
            using var context = DbContextFactory.CreateDbContext();

            // 检查是否有试验对象路径关联
            var pathNodes = await context.TestObjectPathNodes
                .Where(n => n.UnitCode == SelectedUnit.Code)
                .ToListAsync();

            var result = MessageBox.Show(
                $"确认删除机组【{SelectedUnit.Name}】({SelectedUnit.Code}) 吗？\n\n" +
                $"注意：删除机组将同时删除：\n" +
                $"- {pathNodes.Count} 个试验对象路径节点\n" +
                $"- 该机组下的所有试验数据",
                "删除确认",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes)
                return;

            var logService = new OperationLogService(context);
            var currentUser = Services.Security.UserSession.Current?.User.UserName ?? "system";

            var unit = await context.Units.FindAsync(SelectedUnit.Code);
            if (unit == null)
            {
                Message = "机组在数据库中不存在";
                return;
            }

            // 删除机组下的所有试验记录
            var testRecords = await context.TestRecords
                .Where(r => r.ProjectCode == unit.ProjectCode && r.UnitCode == unit.Code)
                .ToListAsync();
            context.TestRecords.RemoveRange(testRecords);

            // 删除机组下的所有试验对象路径节点
            context.TestObjectPathNodes.RemoveRange(pathNodes);

            // 删除机组
            context.Units.Remove(unit);

            await logService.LogAsync("删除机组", currentUser,
                $"删除机组【{unit.Name}】({unit.Code}) 所属项目【{SelectedProject.Name}】，" +
                $"同时删除 {pathNodes.Count} 个路径节点，{testRecords.Count} 条试验记录", "Success");

            await context.SaveChangesAsync();

            // 更新 UI
            Units.Remove(SelectedUnit);
            SelectedUnit = null;
            OnPropertyChanged(nameof(CurrentUnits));
            Message = $"✅ 已删除机组【{unit.Name}】，{pathNodes.Count} 个路径节点，{testRecords.Count} 条试验记录";
        }
        catch (Exception ex)
        {
            Message = $"❌ 删除机组失败：{ex.Message}";
        }
    }

    /// <summary>批量导入：选择文件夹 → 使用 BatchUploadAsync 导入 → 显示简单结果</summary>
    private async Task ImportBatchDataAsync()
    {
        if (!PermissionGuard.Can(Perms.RecordsUpload) || IsBatchImporting) return;
        var dialog = new OpenFolderDialog
        {
            Title = "选择数据文件夹（一级=项目，二级=机组，三级及以下=试验对象层级）"
        };

        if (dialog.ShowDialog() != true)
        {
            Message = "已取消导入";
            return;
        }

        // 创建日志文件（保存在应用目录下的 ImportLogs 子目录，避免硬编码开发机路径导致部署后失败）
        var logDir = System.IO.Path.Combine(AppContext.BaseDirectory, "ImportLogs");
        if (!System.IO.Directory.Exists(logDir))
            System.IO.Directory.CreateDirectory(logDir);

        var logFile = System.IO.Path.Combine(
            logDir,
            $"批量导入日志_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
        var logWriter = new System.IO.StreamWriter(logFile, false, System.Text.Encoding.UTF8);

        // 进度回调
        var progress = new Progress<BatchUploadProgress>(p =>
        {
            // 使用 Service 层上报的 Current 和 Total 计算进度
            BatchImportProgress = p.Total > 0 ? Math.Min(100, p.Current * 100 / p.Total) : 0;
            BatchImportStatus = $"正在导入：{p.CurrentFileName} ({p.Current}/{p.Total})";
        });

        try
        {
            IsBatchImporting = true;
            BatchImportProgress = 0;
            BatchImportStatus = "准备导入...";
            _batchImportCts = new CancellationTokenSource();

            await logWriter.WriteLineAsync($"=== 批量导入开始 ===");
            await logWriter.WriteLineAsync($"时间: {DateTime.Now}");
            await logWriter.WriteLineAsync($"文件夹: {dialog.FolderName}");
            await logWriter.WriteLineAsync();

            Message = "正在扫描文件夹...";
            System.Diagnostics.Debug.WriteLine($"[批量导入] 开始导入，文件夹: {dialog.FolderName}");

            var currentUser = Services.Security.UserSession.Current?.User.UserName ?? "system";
            await logWriter.WriteLineAsync($"操作用户: {currentUser}");

            var dataUploadService = new DataUploadService(AppServices.TestRecordService);

            // 批量解析文件夹
            await logWriter.WriteLineAsync($"--- 开始解析文件夹 ---");
            System.Diagnostics.Debug.WriteLine("[批量导入] 开始解析文件夹...");
            var parsedItems = await dataUploadService.BatchParseFolderAsync(dialog.FolderName);

            await logWriter.WriteLineAsync($"解析完成，共 {parsedItems.Count} 个文件");
            await logWriter.WriteLineAsync($"就绪: {parsedItems.Count(p => p.IsReady)}, 跳过: {parsedItems.Count(p => p.IsSkipped)}, 错误: {parsedItems.Count(p => !p.IsReady && !p.IsSkipped)}");

            System.Diagnostics.Debug.WriteLine($"[批量导入] 解析完成，共 {parsedItems.Count} 个文件");
            System.Diagnostics.Debug.WriteLine($"[批量导入] 就绪: {parsedItems.Count(p => p.IsReady)}, 跳过: {parsedItems.Count(p => p.IsSkipped)}, 错误: {parsedItems.Count(p => !p.IsReady && !p.IsSkipped)}");

            // 记录错误详情
            var errorItems = parsedItems.Where(p => !p.IsReady && !p.IsSkipped).ToList();
            if (errorItems.Count > 0)
            {
                await logWriter.WriteLineAsync();
                await logWriter.WriteLineAsync($"--- 解析错误的文件 ({errorItems.Count} 个) ---");
                System.Diagnostics.Debug.WriteLine("[批量导入] 解析错误的文件:");
                foreach (var item in errorItems)
                {
                    await logWriter.WriteLineAsync($"  - {item.FileName}: {item.ErrorMessage}");
                    System.Diagnostics.Debug.WriteLine($"  - {item.FileName}: {item.ErrorMessage}");
                }
            }

            // 直接上传所有就绪的文件
            await logWriter.WriteLineAsync();
            await logWriter.WriteLineAsync($"--- 开始上传 ---");
            System.Diagnostics.Debug.WriteLine("[批量导入] 开始上传...");

            // 清空 DbContext 的变更追踪器，确保删除的记录不会被误判为重复
            AppServices.DbContext.ChangeTracker.Clear();
            await logWriter.WriteLineAsync("已清空 DbContext 变更追踪器");

            var result = await dataUploadService.BatchUploadAsync(parsedItems, currentUser, progress, logWriter,
                cancellationToken: _batchImportCts.Token);
            await logWriter.WriteLineAsync($"上传完成，成功: {result.SuccessCount}, 失败: {result.FailedCount}"
                + (result.WasCancelled ? "（用户中途取消）" : ""));
            System.Diagnostics.Debug.WriteLine($"[批量导入] 上传完成，成功: {result.SuccessCount}, 失败: {result.FailedCount}");

            // 记录操作日志
            using var context = DbContextFactory.CreateDbContext();
            var logService = new OperationLogService(context);
            await logService.LogAsync("批量导入", currentUser,
                $"导入 {result.SuccessCount} 条试验记录，失败 {result.FailedCount} 条", "Success");

            // 刷新数据
            await LoadDataAsync();

            await logWriter.WriteLineAsync();
            await logWriter.WriteLineAsync($"=== 批量导入结束 ===");
            await logWriter.WriteLineAsync($"结果: 成功 {result.SuccessCount} 条，失败 {result.FailedCount} 条");
            await logWriter.WriteLineAsync($"日志文件: {logFile}");

            // 显示简单结果
            if (result.WasCancelled)
            {
                MessageBox.Show(
                    $"批量导入已取消。\n成功：{result.SuccessCount} 条\n失败：{result.FailedCount} 条\n未处理：{Math.Max(0, result.TotalCount - result.SuccessCount - result.FailedCount)} 条\n\n已导入的数据保留，详细日志已保存到：\n{logFile}",
                    "已取消",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                Message = $"⏹ 导入已取消：成功 {result.SuccessCount} 条";
            }
            else if (result.FailedCount > 0)
            {
                MessageBox.Show(
                    $"批量导入完成！\n成功：{result.SuccessCount} 条\n失败：{result.FailedCount} 条\n\n详细日志已保存到：\n{logFile}",
                    "导入完成",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                Message = $"导入完成：成功 {result.SuccessCount} 条，失败 {result.FailedCount} 条";
            }
            else
            {
                MessageBox.Show(
                    $"批量导入成功！共导入 {result.SuccessCount} 条试验记录\n\n详细日志已保存到：\n{logFile}",
                    "导入完成",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                Message = $"✅ 导入完成：成功 {result.SuccessCount} 条试验记录";
            }
        }
        catch (Exception ex)
        {
            await logWriter.WriteLineAsync();
            await logWriter.WriteLineAsync($"!!! 异常 !!!");
            await logWriter.WriteLineAsync(ex.ToString());
            System.Diagnostics.Debug.WriteLine($"[批量导入] 异常: {ex}");
            MessageBox.Show($"批量导入失败：{ex.Message}\n\n详细日志已保存到：\n{logFile}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            Message = $"❌ 导入失败：{ex.Message}";
        }
        finally
        {
            // 日志落盘失败（磁盘满/被杀软锁定文件）不得阻断状态复位——
            // 否则 IsBatchImporting 永久为 true：导入按钮禁用、进度条常驻，
            // 且异常发生在 fire-and-forget 任务里被吞，只能重启应用恢复
            try
            {
                await logWriter.FlushAsync();
                logWriter.Close();
            }
            catch (Exception flushEx)
            {
                System.Diagnostics.Debug.WriteLine($"[批量导入] 日志落盘失败: {flushEx.Message}");
            }

            _batchImportCts?.Dispose();
            _batchImportCts = null;

            // 进度条到 100% 并延迟 3 秒后消失
            BatchImportProgress = 100;
            BatchImportStatus = "导入完成！";
            await Task.Delay(3000);
            IsBatchImporting = false;
        }
    }
}
