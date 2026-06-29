using System.Windows.Controls;
using IsolationLeakage.App.ViewModels;

namespace IsolationLeakage.App.Views;

/// <summary>
/// BatchUploadView.xaml 的交互逻辑
/// </summary>
public partial class BatchUploadView : UserControl
{
    public BatchUploadView()
    {
        InitializeComponent();
        DataContext = new BatchUploadViewModel();
    }
}
