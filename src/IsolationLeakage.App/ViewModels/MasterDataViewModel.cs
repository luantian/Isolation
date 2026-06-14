namespace IsolationLeakage.App.ViewModels;

public sealed class MasterDataViewModel : ViewModelBase
{
    public MasterDataViewModel()
    {
        ProjectUnitPage = new ProjectUnitManagementViewModel();
        TestObjectPathPage = new TestObjectPathManagementViewModel();
        MeasurementDevicePage = new MeasurementDeviceLedgerViewModel();
        DataUploadPage = new DataUploadViewModel();
        ReportExportPage = new ReportExportViewModel();
    }

    public ProjectUnitManagementViewModel ProjectUnitPage { get; }

    public TestObjectPathManagementViewModel TestObjectPathPage { get; }

    public MeasurementDeviceLedgerViewModel MeasurementDevicePage { get; }

    public DataUploadViewModel DataUploadPage { get; }

    public ReportExportViewModel ReportExportPage { get; }
}
