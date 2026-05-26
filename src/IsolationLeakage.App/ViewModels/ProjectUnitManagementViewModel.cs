using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using IsolationLeakage.App.Models;
using IsolationLeakage.App.Services;

namespace IsolationLeakage.App.ViewModels;

public sealed class ProjectUnitManagementViewModel : INotifyPropertyChanged
{
    private string _errorMessage = string.Empty;
    private string _newProjectCode = "NEW";
    private string _newProjectName = string.Empty;
    private string _newProjectRemark = string.Empty;
    private string _newUnitCode = "UNIT";
    private string _newUnitName = string.Empty;
    private string _newUnitRemark = string.Empty;
    private ProjectCatalogItem? _selectedProject;

    public ProjectUnitManagementViewModel(MasterDataStore store)
    {
        Store = store;
        SelectedProject = Projects.FirstOrDefault();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public MasterDataStore Store { get; }

    public ObservableCollection<ProjectCatalogItem> Projects => Store.Projects;

    public ObservableCollection<UnitCatalogItem> Units => Store.Units;

    public IEnumerable<UnitCatalogItem> CurrentUnits => SelectedProject is null
        ? []
        : Units.Where(unit => unit.ProjectName == SelectedProject.Name);

    public ProjectCatalogItem? SelectedProject
    {
        get => _selectedProject;
        set
        {
            if (_selectedProject == value)
            {
                return;
            }

            _selectedProject = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CurrentUnits));
        }
    }

    public string NewProjectCode
    {
        get => _newProjectCode;
        set => SetField(ref _newProjectCode, value);
    }

    public string NewProjectName
    {
        get => _newProjectName;
        set => SetField(ref _newProjectName, value);
    }

    public string NewProjectRemark
    {
        get => _newProjectRemark;
        set => SetField(ref _newProjectRemark, value);
    }

    public string NewUnitCode
    {
        get => _newUnitCode;
        set => SetField(ref _newUnitCode, value);
    }

    public string NewUnitName
    {
        get => _newUnitName;
        set => SetField(ref _newUnitName, value);
    }

    public string NewUnitRemark
    {
        get => _newUnitRemark;
        set => SetField(ref _newUnitRemark, value);
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        set => SetField(ref _errorMessage, value);
    }

    public void AddProject()
    {
        ErrorMessage = string.Empty;
        if (string.IsNullOrWhiteSpace(NewProjectCode) || string.IsNullOrWhiteSpace(NewProjectName))
        {
            ErrorMessage = "项目编号和项目名称不能为空。";
            return;
        }

        if (Projects.Any(project => project.Code == NewProjectCode.Trim() || project.Name == NewProjectName.Trim()))
        {
            ErrorMessage = "项目编号或项目名称已存在。";
            return;
        }

        SelectedProject = Store.AddProject(NewProjectCode, NewProjectName, NewProjectRemark);
        NewProjectCode = $"P{Projects.Count + 1:000}";
        NewProjectName = string.Empty;
        NewProjectRemark = string.Empty;
    }

    public void AddUnit()
    {
        ErrorMessage = string.Empty;
        if (SelectedProject is null)
        {
            ErrorMessage = "请先选择项目。";
            return;
        }

        if (string.IsNullOrWhiteSpace(NewUnitCode) || string.IsNullOrWhiteSpace(NewUnitName))
        {
            ErrorMessage = "机组编号和机组名称不能为空。";
            return;
        }

        if (Units.Any(unit => unit.ProjectName == SelectedProject.Name && (unit.Code == NewUnitCode.Trim() || unit.Name == NewUnitName.Trim())))
        {
            ErrorMessage = "当前项目下机组编号或机组名称已存在。";
            return;
        }

        Store.AddUnit(SelectedProject.Name, NewUnitCode, NewUnitName, NewUnitRemark);
        NewUnitCode = $"U{CurrentUnits.Count() + 1:000}";
        NewUnitName = string.Empty;
        NewUnitRemark = string.Empty;
        OnPropertyChanged(nameof(CurrentUnits));
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
