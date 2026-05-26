using System.Windows.Controls;
using IsolationLeakage.App.ViewModels;

namespace IsolationLeakage.App.Views;

public partial class RealtimeMonitorView : UserControl
{
    public RealtimeMonitorView()
    {
        InitializeComponent();
        DataContext = new RealtimeMonitorViewModel();
    }
}
