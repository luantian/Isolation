using System.Collections.ObjectModel;
using IsolationLeakage.App.Models;

namespace IsolationLeakage.App.Services;

public sealed class MasterDataStore
{
    public MasterDataStore()
    {
        AddScrollTestData();
    }

    public ObservableCollection<ProjectCatalogItem> Projects { get; } =
    [
        new() { Code = "HN", Name = "\u6d77\u5357\u9879\u76ee", Status = "\u542f\u7528", Remark = "\u793a\u4f8b\u9879\u76ee" },
        new() { Code = "ZZ", Name = "\u6f33\u5dde\u9879\u76ee", Status = "\u542f\u7528", Remark = "\u793a\u4f8b\u9879\u76ee" },
        new() { Code = "TEST-01", Name = "\u6d4b\u8bd5\u793a\u4f8b\u9879\u76ee 01", Status = "\u542f\u7528", Remark = "\u6eda\u52a8\u6d4b\u8bd5\u6570\u636e" },
        new() { Code = "TEST-02", Name = "\u6d4b\u8bd5\u793a\u4f8b\u9879\u76ee 02", Status = "\u542f\u7528", Remark = "\u6eda\u52a8\u6d4b\u8bd5\u6570\u636e" },
        new() { Code = "TEST-03", Name = "\u6d4b\u8bd5\u793a\u4f8b\u9879\u76ee 03", Status = "\u505c\u7528", Remark = "\u6eda\u52a8\u6d4b\u8bd5\u6570\u636e" },
        new() { Code = "TEST-04", Name = "\u6d4b\u8bd5\u793a\u4f8b\u9879\u76ee 04", Status = "\u542f\u7528", Remark = "\u6eda\u52a8\u6d4b\u8bd5\u6570\u636e" },
        new() { Code = "TEST-05", Name = "\u6d4b\u8bd5\u793a\u4f8b\u9879\u76ee 05", Status = "\u542f\u7528", Remark = "\u6eda\u52a8\u6d4b\u8bd5\u6570\u636e" },
        new() { Code = "TEST-06", Name = "\u6d4b\u8bd5\u793a\u4f8b\u9879\u76ee 06", Status = "\u505c\u7528", Remark = "\u6eda\u52a8\u6d4b\u8bd5\u6570\u636e" },
        new() { Code = "TEST-07", Name = "\u6d4b\u8bd5\u793a\u4f8b\u9879\u76ee 07", Status = "\u542f\u7528", Remark = "\u6eda\u52a8\u6d4b\u8bd5\u6570\u636e" },
        new() { Code = "TEST-08", Name = "\u6d4b\u8bd5\u793a\u4f8b\u9879\u76ee 08", Status = "\u542f\u7528", Remark = "\u6eda\u52a8\u6d4b\u8bd5\u6570\u636e" }
    ];

    public ObservableCollection<UnitCatalogItem> Units { get; } =
    [
        new() { ProjectName = "\u6d77\u5357\u9879\u76ee", Code = "HN-3", Name = "\u6d77\u5357 3 \u53f7\u673a\u7ec4", Status = "\u542f\u7528", Remark = "\u793a\u4f8b\u673a\u7ec4" },
        new() { ProjectName = "\u6d77\u5357\u9879\u76ee", Code = "HN-4", Name = "\u6d77\u5357 4 \u53f7\u673a\u7ec4", Status = "\u542f\u7528", Remark = "\u793a\u4f8b\u673a\u7ec4" },
        new() { ProjectName = "\u6f33\u5dde\u9879\u76ee", Code = "ZZ-1", Name = "\u6f33\u5dde 1 \u53f7\u673a\u7ec4", Status = "\u542f\u7528", Remark = "\u793a\u4f8b\u673a\u7ec4" },
        new() { ProjectName = "\u6d4b\u8bd5\u793a\u4f8b\u9879\u76ee 01", Code = "T01-01", Name = "\u6d4b\u8bd5 01 \u53f7\u673a\u7ec4", Status = "\u542f\u7528", Remark = "\u6eda\u52a8\u6d4b\u8bd5\u673a\u7ec4" },
        new() { ProjectName = "\u6d4b\u8bd5\u793a\u4f8b\u9879\u76ee 01", Code = "T01-02", Name = "\u6d4b\u8bd5 02 \u53f7\u673a\u7ec4", Status = "\u542f\u7528", Remark = "\u6eda\u52a8\u6d4b\u8bd5\u673a\u7ec4" },
        new() { ProjectName = "\u6d4b\u8bd5\u793a\u4f8b\u9879\u76ee 01", Code = "T01-03", Name = "\u6d4b\u8bd5 03 \u53f7\u673a\u7ec4", Status = "\u505c\u7528", Remark = "\u6eda\u52a8\u6d4b\u8bd5\u673a\u7ec4" },
        new() { ProjectName = "\u6d4b\u8bd5\u793a\u4f8b\u9879\u76ee 01", Code = "T01-04", Name = "\u6d4b\u8bd5 04 \u53f7\u673a\u7ec4", Status = "\u542f\u7528", Remark = "\u6eda\u52a8\u6d4b\u8bd5\u673a\u7ec4" },
        new() { ProjectName = "\u6d4b\u8bd5\u793a\u4f8b\u9879\u76ee 01", Code = "T01-05", Name = "\u6d4b\u8bd5 05 \u53f7\u673a\u7ec4", Status = "\u542f\u7528", Remark = "\u6eda\u52a8\u6d4b\u8bd5\u673a\u7ec4" },
        new() { ProjectName = "\u6d4b\u8bd5\u793a\u4f8b\u9879\u76ee 01", Code = "T01-06", Name = "\u6d4b\u8bd5 06 \u53f7\u673a\u7ec4", Status = "\u542f\u7528", Remark = "\u6eda\u52a8\u6d4b\u8bd5\u673a\u7ec4" },
        new() { ProjectName = "\u6d4b\u8bd5\u793a\u4f8b\u9879\u76ee 01", Code = "T01-07", Name = "\u6d4b\u8bd5 07 \u53f7\u673a\u7ec4", Status = "\u505c\u7528", Remark = "\u6eda\u52a8\u6d4b\u8bd5\u673a\u7ec4" },
        new() { ProjectName = "\u6d4b\u8bd5\u793a\u4f8b\u9879\u76ee 01", Code = "T01-08", Name = "\u6d4b\u8bd5 08 \u53f7\u673a\u7ec4", Status = "\u542f\u7528", Remark = "\u6eda\u52a8\u6d4b\u8bd5\u673a\u7ec4" },
        new() { ProjectName = "\u6d4b\u8bd5\u793a\u4f8b\u9879\u76ee 01", Code = "T01-09", Name = "\u6d4b\u8bd5 09 \u53f7\u673a\u7ec4", Status = "\u542f\u7528", Remark = "\u6eda\u52a8\u6d4b\u8bd5\u673a\u7ec4" },
        new() { ProjectName = "\u6d4b\u8bd5\u793a\u4f8b\u9879\u76ee 01", Code = "T01-10", Name = "\u6d4b\u8bd5 10 \u53f7\u673a\u7ec4", Status = "\u542f\u7528", Remark = "\u6eda\u52a8\u6d4b\u8bd5\u673a\u7ec4" },
        new() { ProjectName = "\u6d4b\u8bd5\u793a\u4f8b\u9879\u76ee 02", Code = "T02-01", Name = "\u6d4b\u8bd5 11 \u53f7\u673a\u7ec4", Status = "\u542f\u7528", Remark = "\u6eda\u52a8\u6d4b\u8bd5\u673a\u7ec4" },
        new() { ProjectName = "\u6d4b\u8bd5\u793a\u4f8b\u9879\u76ee 02", Code = "T02-02", Name = "\u6d4b\u8bd5 12 \u53f7\u673a\u7ec4", Status = "\u542f\u7528", Remark = "\u6eda\u52a8\u6d4b\u8bd5\u673a\u7ec4" }
    ];

    public ObservableCollection<MeasurementDeviceItem> MeasurementDevices { get; } =
    [
        new() { DeviceCode = "DEV-001", DeviceName = "\u6cc4\u6f0f\u7387\u6d4b\u91cf\u88c5\u7f6e 01", Model = "LRM-100", SerialNumber = "SN-HN-0001", PrimaryCommunication = "USB", EnabledStatus = "\u542f\u7528", RecentConnectionStatus = "\u5728\u7ebf", LastSyncTime = "2026-05-26 12:18", LastUploadTime = "2026-05-26 12:18", UploadCount = 128, LastUploadResult = "\u6210\u529f", Remark = "\u793a\u4f8b\u88c5\u7f6e" },
        new() { DeviceCode = "DEV-002", DeviceName = "\u6cc4\u6f0f\u7387\u6d4b\u91cf\u88c5\u7f6e 02", Model = "LRM-100", SerialNumber = "SN-HN-0002", PrimaryCommunication = "RJ45", EnabledStatus = "\u542f\u7528", RecentConnectionStatus = "\u5728\u7ebf", LastSyncTime = "2026-05-26 11:42", LastUploadTime = "2026-05-26 11:42", UploadCount = 96, LastUploadResult = "\u6210\u529f", Remark = "\u793a\u4f8b\u88c5\u7f6e" },
        new() { DeviceCode = "DEV-003", DeviceName = "\u6cc4\u6f0f\u7387\u6d4b\u91cf\u88c5\u7f6e 03", Model = "LRM-200", SerialNumber = "SN-HN-0003", PrimaryCommunication = "RS232/485", EnabledStatus = "\u542f\u7528", RecentConnectionStatus = "\u79bb\u7ebf", LastSyncTime = "2026-05-25 16:05", LastUploadTime = "2026-05-25 16:05", UploadCount = 62, LastUploadResult = "\u6709\u4e0d\u5408\u683c\u8bb0\u5f55", Remark = "\u793a\u4f8b\u88c5\u7f6e" },
        new() { DeviceCode = "DEV-004", DeviceName = "\u6cc4\u6f0f\u7387\u6d4b\u91cf\u88c5\u7f6e 04", Model = "LRM-200", SerialNumber = "SN-HN-0004", PrimaryCommunication = "USB", EnabledStatus = "\u505c\u7528", RecentConnectionStatus = "\u672a\u540c\u6b65", LastSyncTime = "-", LastUploadTime = "-", UploadCount = 0, LastUploadResult = "-", Remark = "\u505c\u7528\u540e\u4ec5\u4fdd\u7559\u5386\u53f2\u6570\u636e" }
    ];

    public IEnumerable<string> GetProjectNames()
    {
        return Projects.Select(project => project.Name);
    }

    public IEnumerable<string> GetUnitNames(string projectName)
    {
        return Units.Where(unit => unit.ProjectName == projectName).Select(unit => unit.Name);
    }

    public ProjectCatalogItem AddProject(string code, string name, string remark)
    {
        var project = new ProjectCatalogItem
        {
            Code = code.Trim(),
            Name = name.Trim(),
            Status = "\u542f\u7528",
            Remark = remark.Trim()
        };
        Projects.Add(project);
        return project;
    }

    public UnitCatalogItem AddUnit(string projectName, string code, string name, string remark)
    {
        var unit = new UnitCatalogItem
        {
            ProjectName = projectName,
            Code = code.Trim(),
            Name = name.Trim(),
            Status = "\u542f\u7528",
            Remark = remark.Trim()
        };
        Units.Add(unit);
        return unit;
    }

    private void AddScrollTestData()
    {
        for (var projectIndex = 9; projectIndex <= 30; projectIndex++)
        {
            Projects.Add(new ProjectCatalogItem
            {
                Code = $"TEST-{projectIndex:00}",
                Name = $"\u6d4b\u8bd5\u793a\u4f8b\u9879\u76ee {projectIndex:00}",
                Status = projectIndex % 6 == 0 ? "\u505c\u7528" : "\u542f\u7528",
                Remark = "\u6eda\u52a8\u6d4b\u8bd5\u6570\u636e"
            });
        }

        for (var unitIndex = 11; unitIndex <= 45; unitIndex++)
        {
            Units.Add(new UnitCatalogItem
            {
                ProjectName = "\u6d4b\u8bd5\u793a\u4f8b\u9879\u76ee 01",
                Code = $"T01-{unitIndex:00}",
                Name = $"\u6d4b\u8bd5 {unitIndex:00} \u53f7\u673a\u7ec4",
                Status = unitIndex % 7 == 0 ? "\u505c\u7528" : "\u542f\u7528",
                Remark = "\u6eda\u52a8\u6d4b\u8bd5\u673a\u7ec4"
            });
        }

        for (var unitIndex = 3; unitIndex <= 24; unitIndex++)
        {
            Units.Add(new UnitCatalogItem
            {
                ProjectName = "\u6d4b\u8bd5\u793a\u4f8b\u9879\u76ee 02",
                Code = $"T02-{unitIndex:00}",
                Name = $"\u6d4b\u8bd5 {unitIndex + 10:00} \u53f7\u673a\u7ec4",
                Status = unitIndex % 5 == 0 ? "\u505c\u7528" : "\u542f\u7528",
                Remark = "\u6eda\u52a8\u6d4b\u8bd5\u673a\u7ec4"
            });
        }
    }
}
