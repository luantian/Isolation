namespace IsolationLeakage.App.Models;

public sealed class ProjectCatalogItem
{
    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Status { get; set; } = "启用";

    public string Remark { get; set; } = string.Empty;
}
