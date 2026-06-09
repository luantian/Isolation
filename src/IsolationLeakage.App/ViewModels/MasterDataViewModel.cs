using System.ComponentModel;
using System.Runtime.CompilerServices;
using IsolationLeakage.App.Services;

namespace IsolationLeakage.App.ViewModels;

public sealed class MasterDataViewModel : INotifyPropertyChanged
{
    public MasterDataViewModel()
    {
        ProjectUnitPage = new ProjectUnitManagementViewModel(AppServices.MasterData);
        TestObjectPathPage = new TestObjectPathManagementViewModel(AppServices.MasterData);
        MeasurementDevicePage = new MeasurementDeviceLedgerViewModel(AppServices.MasterData);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ProjectUnitManagementViewModel ProjectUnitPage { get; }

    public TestObjectPathManagementViewModel TestObjectPathPage { get; }

    public MeasurementDeviceLedgerViewModel MeasurementDevicePage { get; }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
