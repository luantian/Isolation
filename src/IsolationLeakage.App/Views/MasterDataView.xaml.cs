using System.Windows;
using System.Windows.Controls;
using IsolationLeakage.App.ViewModels;

namespace IsolationLeakage.App.Views;

public partial class MasterDataView : UserControl
{
    public MasterDataView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 切换标签页时，刷新即将显示的子页面数据。
    /// 解决"在一个标签页导入后，另一个标签页因缓存看不到新数据"的问题。
    /// </summary>
    private void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 只处理 TabControl 自身的选中变化（忽略子控件冒泡上来的事件）
        if (!ReferenceEquals(e.OriginalSource, sender)) return;

        if (e.AddedItems.Count == 0) return;
        if (e.AddedItems[0] is not TabItem tab) return;

        // 取该标签页内容控件的 DataContext（子页面 ViewModel）
        var vm = (tab.Content as FrameworkElement)?.DataContext;

        if (vm is IRefreshable refreshable)
        {
            _ = refreshable.RefreshAsync();
        }
    }
}
