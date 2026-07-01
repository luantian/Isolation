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
    private string _newProjectCode = string.Empty;
    private string _newProjectName = string.Empty;
    private string _newProjectRemark = string.Empty;
    private string _newUnitCode = string.Empty;
    private string _newUnitName = string.Empty;
    private string _newUnitRemark = string.Empty;
    private string _projectError = string.Empty;
    private string _unitError = string.Empty;
    private string _message = string.Empty;

    public ProjectUnitManagementViewModel()
    {
        Projects = new ObservableCollection<Project>();
        Units = new ObservableCollection<Unit>();

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
            }
        }
    }

    public string NewProjectCode
    {
        get => _newProjectCode;
        set => SetProperty(ref _newProjectCode, value);
    }

    public string NewProjectName
    {
        get => _newProjectName;
        set => SetProperty(ref _newProjectName, value);
    }

    public string NewProjectRemark
    {
        get => _newProjectRemark;
        set => SetProperty(ref _newProjectRemark, value);
    }

    public string NewUnitCode
    {
        get => _newUnitCode;
        set => SetProperty(ref _newUnitCode, value);
    }

    public string NewUnitName
    {
        get => _newUnitName;
        set => SetProperty(ref _newUnitName, value);
    }

    public string NewUnitRemark
    {
        get => _newUnitRemark;
        set => SetProperty(ref _newUnitRemark, value);
    }

    public string ProjectError
    {
        get => _projectError;
        set => SetProperty(ref _projectError, value);
    }

    public string UnitError
    {
        get => _unitError;
        set => SetProperty(ref _unitError, value);
    }

    public string Message
    {
        get => _message;
        set => SetProperty(ref _message, value);
    }

    public IRelayCommand AddProjectCommand => new RelayCommand(() => _ = AddProjectAsync(), () => PermissionGuard.Can(Perms.ProjectAdd));
    public IRelayCommand AddUnitCommand => new RelayCommand(() => _ = AddUnitAsync(), () => PermissionGuard.Can(Perms.ProjectAdd));
    public IRelayCommand ImportBatchDataCommand => new RelayCommand(() => _ = ImportBatchDataAsync(), () => PermissionGuard.Can(Perms.RecordsUpload));
    public IRelayCommand DeleteProjectCommand => new RelayCommand(() => _ = DeleteProjectAsync(), () => PermissionGuard.Can(Perms.ProjectDelete));
    public IRelayCommand DeleteUnitCommand => new RelayCommand(() => _ = DeleteUnitAsync(), () => PermissionGuard.Can(Perms.ProjectDelete));

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
            NewProjectCode = $"P{DateTime.Now:yyMM}";
            Message = $"已从数据库加载 {Projects.Count} 个项目，{Units.Count} 个机组";
        }
        catch (Exception ex)
        {
            Message = $"加载数据失败：{ex.Message}";
        }
    }

    public async Task AddProjectAsync()
    {
        if (!PermissionGuard.Can(Perms.ProjectAdd)) return;
        ProjectError = string.Empty;
        if (string.IsNullOrWhiteSpace(NewProjectCode) || string.IsNullOrWhiteSpace(NewProjectName))
        {
            ProjectError = "项目编号和项目名称不能为空";
            return;
        }

        try
        {
            using var context = DbContextFactory.CreateDbContext();
            var logService = new OperationLogService(context);
            var currentUser = Services.Security.UserSession.Current?.User.UserName ?? "system";

            if (await context.Projects.AnyAsync(p => p.Code == NewProjectCode.Trim() || p.Name == NewProjectName.Trim()))
            {
                ProjectError = "项目编号或项目名称已存在";
                return;
            }

            var project = new Project
            {
                Code = NewProjectCode.Trim(),
                Name = NewProjectName.Trim(),
                Status = EnabledStatus.Enabled,
                Remark = NewProjectRemark.Trim(),
                CreatedAt = DateTime.Now
            };

            context.Projects.Add(project);

            // 记录操作日志（在 SaveChanges 之前，确保同一事务）
            await logService.LogAsync("创建项目", currentUser,
                $"新增项目【{project.Name}】({project.Code})", "Success");

            await context.SaveChangesAsync();

            Projects.Add(project);
            SelectedProject = project;
            NewProjectCode = $"P{DateTime.Now:yyMM}";
            NewProjectName = string.Empty;
            NewProjectRemark = string.Empty;
            Message = $"✅ 已新增项目并保存到数据库：{project.Name}";
        }
        catch (Exception ex)
        {
            Message = $"❌ 新增项目失败：{ex.Message}";
        }
    }

    public async Task AddUnitAsync()
    {
        if (!PermissionGuard.Can(Perms.ProjectAdd)) return;
        UnitError = string.Empty;
        if (SelectedProject == null)
        {
            UnitError = "请先选择项目";
            return;
        }

        if (string.IsNullOrWhiteSpace(NewUnitCode) || string.IsNullOrWhiteSpace(NewUnitName))
        {
            UnitError = "机组编号和机组名称不能为空";
            return;
        }

        try
        {
            using var context = DbContextFactory.CreateDbContext();
            var logService = new OperationLogService(context);
            var currentUser = Services.Security.UserSession.Current?.User.UserName ?? "system";

            // 在当前 DbContext 中重新获取 Project（避免 AsNoTracking 的实体导致追踪冲突）
            var project = await context.Projects.FindAsync(SelectedProject.Code);
            if (project == null)
            {
                UnitError = "所选项目在数据库中不存在，请刷新后重试";
                return;
            }

            var unit = new Unit
            {
                Code = NewUnitCode.Trim(),
                Name = NewUnitName.Trim(),
                ProjectCode = SelectedProject.Code,
                Project = project,
                Status = EnabledStatus.Enabled,
                Remark = NewUnitRemark.Trim(),
                CreatedAt = DateTime.Now
            };

            context.Units.Add(unit);

            // 记录操作日志（在 SaveChanges 之前，确保同一事务）
            await logService.LogAsync("创建机组", currentUser,
                $"新增机组【{unit.Name}】({unit.Code}) 所属项目【{SelectedProject.Name}】", "Success");

            await context.SaveChangesAsync();

            Units.Add(unit);
            NewUnitCode = $"{SelectedProject.Code}-{Units.Count(u => u.ProjectCode == SelectedProject.Code) + 1:00}";
            NewUnitName = string.Empty;
            NewUnitRemark = string.Empty;
            OnPropertyChanged(nameof(CurrentUnits));
            Message = $"✅ 已新增机组并保存到数据库：{unit.Name}";
        }
        catch (Exception ex)
        {
            Message = $"❌ 新增机组失败：{ex.Message}";
        }
    }

    public async Task DeleteProjectAsync()
    {
        if (!PermissionGuard.Can(Perms.ProjectDelete)) return;
        ProjectError = string.Empty;
        if (SelectedProject == null)
        {
            ProjectError = "请先选择要删除的项目";
            return;
        }

        // 检查项目下是否有机组
        var unitsUnderProject = Units.Where(u => u.ProjectCode == SelectedProject.Code).ToList();
        if (unitsUnderProject.Count > 0)
        {
            var result = MessageBox.Show(
                $"项目【{SelectedProject.Name}】下有 {unitsUnderProject.Count} 个机组，删除项目将同时删除所有机组及其相关数据。\n\n确认删除吗？",
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
                ProjectError = "项目在数据库中不存在";
                return;
            }

            // 删除项目下的所有机组
            var units = await context.Units.Where(u => u.ProjectCode == SelectedProject.Code).ToListAsync();
            context.Units.RemoveRange(units);

            // 删除项目
            context.Projects.Remove(project);

            await logService.LogAsync("删除项目", currentUser,
                $"删除项目【{project.Name}】({project.Code})，同时删除 {units.Count} 个机组", "Success");

            await context.SaveChangesAsync();

            // 更新 UI
            Projects.Remove(SelectedProject);
            foreach (var unit in units)
                Units.Remove(unit);

            SelectedProject = Projects.FirstOrDefault();
            OnPropertyChanged(nameof(CurrentUnits));
            Message = $"✅ 已删除项目【{project.Name}】及其 {units.Count} 个机组";
        }
        catch (Exception ex)
        {
            Message = $"❌ 删除项目失败：{ex.Message}";
        }
    }

    public async Task DeleteUnitAsync()
    {
        if (!PermissionGuard.Can(Perms.ProjectDelete)) return;
        UnitError = string.Empty;
        if (SelectedProject == null)
        {
            UnitError = "请先选择项目";
            return;
        }

        var selectedUnit = CurrentUnits.FirstOrDefault();
        if (selectedUnit == null)
        {
            UnitError = "请先在列表中选择要删除的机组";
            return;
        }

        var result = MessageBox.Show(
            $"确认删除机组【{selectedUnit.Name}】({selectedUnit.Code}) 吗？\n\n注意：删除机组将同时删除该机组下的所有试验数据。",
            "删除确认",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
            return;

        try
        {
            using var context = DbContextFactory.CreateDbContext();
            var logService = new OperationLogService(context);
            var currentUser = Services.Security.UserSession.Current?.User.UserName ?? "system";

            var unit = await context.Units.FindAsync(selectedUnit.Code);
            if (unit == null)
            {
                UnitError = "机组在数据库中不存在";
                return;
            }

            // 删除机组下的所有试验记录
            var testRecords = await context.TestRecords
                .Where(r => r.ProjectCode == unit.ProjectCode && r.UnitCode == unit.Code)
                .ToListAsync();
            context.TestRecords.RemoveRange(testRecords);

            // 删除机组
            context.Units.Remove(unit);

            await logService.LogAsync("删除机组", currentUser,
                $"删除机组【{unit.Name}】({unit.Code}) 所属项目【{SelectedProject.Name}】，同时删除 {testRecords.Count} 条试验记录", "Success");

            await context.SaveChangesAsync();

            // 更新 UI
            Units.Remove(selectedUnit);
            OnPropertyChanged(nameof(CurrentUnits));
            Message = $"✅ 已删除机组【{unit.Name}】及其 {testRecords.Count} 条试验记录";
        }
        catch (Exception ex)
        {
            Message = $"❌ 删除机组失败：{ex.Message}";
        }
    }

    /// <summary>批量导入：选择文件夹 → 使用 BatchUploadAsync 导入 → 显示简单结果</summary>
    private async Task ImportBatchDataAsync()
    {
        if (!PermissionGuard.Can(Perms.RecordsUpload)) return;
        var dialog = new OpenFolderDialog
        {
            Title = "选择数据文件夹（一级=项目，二级=机组，三级及以下=试验对象层级）"
        };

        if (dialog.ShowDialog() != true)
        {
            Message = "已取消导入";
            return;
        }

        // 创建日志文件
        var logFile = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            $"批量导入日志_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
        var logWriter = new System.IO.StreamWriter(logFile, false, System.Text.Encoding.UTF8);

        try
        {
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

            var result = await dataUploadService.BatchUploadAsync(parsedItems, currentUser, null, logWriter);
            await logWriter.WriteLineAsync($"上传完成，成功: {result.SuccessCount}, 失败: {result.FailedCount}");
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
            if (result.FailedCount > 0)
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
            await logWriter.FlushAsync();
            logWriter.Close();
        }
    }
}
