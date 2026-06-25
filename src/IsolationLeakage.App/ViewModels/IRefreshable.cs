namespace IsolationLeakage.App.ViewModels;

/// <summary>
/// 可刷新数据的页面接口
/// 切换到该页面时自动调用 RefreshAsync 重新加载数据
/// </summary>
public interface IRefreshable
{
    Task RefreshAsync();
}
