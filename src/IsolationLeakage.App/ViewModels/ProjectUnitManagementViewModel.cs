using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using IsolationLeakage.App.Data;
using IsolationLeakage.App.Models;
using IsolationLeakage.App.Models.Database;
using IsolationLeakage.App.Services;
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

    public IRelayCommand AddProjectCommand => new RelayCommand(() => _ = AddProjectAsync());
    public IRelayCommand AddUnitCommand => new RelayCommand(() => _ = AddUnitAsync());
    public IRelayCommand ImportBatchDataCommand => new RelayCommand(() => _ = ImportBatchDataAsync());

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

    /// <summary>批量导入：选择文件夹 → 一级文件夹=项目，二级文件夹=机组 → 解析入库</summary>
    private async Task ImportBatchDataAsync()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择数据文件夹（一级=项目，二级=机组）"
        };

        if (dialog.ShowDialog() != true)
        {
            Message = "已取消导入";
            return;
        }

        try
        {
            string rootPath = dialog.FolderName;
            var projectDirs = Directory.GetDirectories(rootPath);

            if (projectDirs.Length == 0)
            {
                Message = "该文件夹下没有找到子文件夹（项目）";
                return;
            }

            Message = $"正在解析 {projectDirs.Length} 个项目文件夹...";

            int importedProjects = 0;
            int importedUnits = 0;
            int importedRecords = 0;
            int skippedFiles = 0;

            using var context = DbContextFactory.CreateDbContext();
            var logService = new OperationLogService(context);
            var currentUser = Services.Security.UserSession.Current?.User.UserName ?? "system";
            var testRecordService = new TestRecordService(context);
            var dataUploadService = new DataUploadService(testRecordService);

            foreach (var projectDir in projectDirs)
            {
                string projectName = Path.GetFileName(projectDir);

                // 查找或创建项目
                var project = await context.Projects.FirstOrDefaultAsync(p => p.Name == projectName);
                if (project == null)
                {
                    project = new Project
                    {
                        Code = $"P{importedProjects + 1:000}",
                        Name = projectName,
                        Status = EnabledStatus.Enabled,
                        CreatedAt = DateTime.Now
                    };
                    context.Projects.Add(project);
                    await context.SaveChangesAsync();
                    Projects.Add(project);
                    importedProjects++;
                }

                // 处理机组文件夹（第二级）
                var unitDirs = Directory.GetDirectories(projectDir);
                foreach (var unitDir in unitDirs)
                {
                    string unitName = Path.GetFileName(unitDir);

                    // 查找或创建机组
                    var unit = await context.Units.FirstOrDefaultAsync(u => u.Name == unitName && u.ProjectCode == project.Code);
                    if (unit == null)
                    {
                        unit = new Unit
                        {
                            Code = $"{project.Code}-{importedUnits + 1:00}",
                            Name = unitName,
                            ProjectCode = project.Code,
                            Project = project,
                            Status = EnabledStatus.Enabled,
                            CreatedAt = DateTime.Now
                        };
                        context.Units.Add(unit);
                        await context.SaveChangesAsync();
                        Units.Add(unit);
                        importedUnits++;
                    }

                    // 解析 unitDir 下的试验数据文件（.json / .txt）
                    var dataFiles = Directory.GetFiles(unitDir, "*.json")
                        .Concat(Directory.GetFiles(unitDir, "*.txt"))
                        .Concat(Directory.GetFiles(unitDir, "*.csv"))
                        .ToArray();

                    foreach (var file in dataFiles)
                    {
                        try
                        {
                            var parsedData = await dataUploadService.ParseDataPackageAsync(file);

                            if (string.IsNullOrWhiteSpace(parsedData.ObjectCode))
                            {
                                skippedFiles++;
                                continue;
                            }

                            // 自动生成记录编号
                            string recordCode = $"R{project.Code}-{unit.Code}-{importedRecords + 1:0000}";

                            var testRecord = await dataUploadService.ValidateAndUploadAsync(
                                parsedData,
                                recordCode,
                                project.Code,
                                unit.Code,
                                currentUser);

                            importedRecords++;
                        }
                        catch (InvalidOperationException ex) when (ex.Message.Contains("重复"))
                        {
                            // 重复记录，跳过
                            skippedFiles++;
                        }
                        catch
                        {
                            // 解析失败的文件，跳过但不中断整体流程
                            skippedFiles++;
                        }
                    }
                }
            }

            // 记录操作日志
            await logService.LogAsync("批量导入", currentUser,
                $"导入 {importedProjects} 个项目、{importedUnits} 个机组、{importedRecords} 条试验记录，跳过 {skippedFiles} 个文件", "Success");

            Message = $"✅ 导入完成：{importedProjects} 个项目，{importedUnits} 个机组，{importedRecords} 条试验记录（跳过 {skippedFiles} 个文件）";
            OnPropertyChanged(nameof(CurrentUnits));
        }
        catch (Exception ex)
        {
            Message = $"❌ 导入失败：{ex.Message}";
        }
    }
}
