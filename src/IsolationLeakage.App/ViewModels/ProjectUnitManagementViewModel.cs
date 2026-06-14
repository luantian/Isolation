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
public sealed class ProjectUnitManagementViewModel : ViewModelBase
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

    private async Task LoadDataAsync()
    {
        try
        {
            using var context = DbContextFactory.CreateDbContext();

            var projects = await context.Projects.ToListAsync();
            Projects.Clear();
            foreach (var p in projects) Projects.Add(p);

            var units = await context.Units.ToListAsync();
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
            await context.SaveChangesAsync();

            // 记录操作日志
            await logService.LogAsync("创建项目", currentUser,
                $"新增项目【{project.Name}】({project.Code})", "Success");

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

            var unit = new Unit
            {
                Code = NewUnitCode.Trim(),
                Name = NewUnitName.Trim(),
                ProjectCode = SelectedProject.Code,
                Status = EnabledStatus.Enabled,
                Remark = NewUnitRemark.Trim(),
                CreatedAt = DateTime.Now
            };

            context.Units.Add(unit);
            await context.SaveChangesAsync();

            // 记录操作日志
            await logService.LogAsync("创建机组", currentUser,
                $"新增机组【{unit.Name}】({unit.Code}) 所属项目【{SelectedProject.Name}】", "Success");

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

            using var context = DbContextFactory.CreateDbContext();

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
                            Status = EnabledStatus.Enabled,
                            CreatedAt = DateTime.Now
                        };
                        context.Units.Add(unit);
                        await context.SaveChangesAsync();
                        Units.Add(unit);
                        importedUnits++;
                    }

                    // TODO: 解析 unitDir 下的试验数据文件并入库
                }
            }

            Message = $"✅ 导入完成：{importedProjects} 个项目，{importedUnits} 个机组（试验数据解析待接入）";
            OnPropertyChanged(nameof(CurrentUnits));
        }
        catch (Exception ex)
        {
            Message = $"❌ 导入失败：{ex.Message}";
        }
    }
}
