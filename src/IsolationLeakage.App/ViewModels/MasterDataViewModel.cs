namespace IsolationLeakage.App.ViewModels;

public sealed class MasterDataViewModel : ViewModelBase
{
    public MasterDataViewModel()
    {
        ProjectUnitPage = new ProjectUnitManagementViewModel();
        TestObjectPathPage = new TestObjectPathManagementViewModel();
        MeasurementDevicePage = new MeasurementDeviceLedgerViewModel();
        ReportExportPage = new ReportExportViewModel();
    }

    public ProjectUnitManagementViewModel ProjectUnitPage { get; }

    public TestObjectPathManagementViewModel TestObjectPathPage { get; }

    public MeasurementDeviceLedgerViewModel MeasurementDevicePage { get; }

    public ReportExportViewModel ReportExportPage { get; }
}
