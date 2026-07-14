using System.Windows.Controls;
using System.Windows.Input;
using IsolationLeakage.App.ViewModels;

namespace IsolationLeakage.App.Views;

public partial class RealtimeMonitorView : UserControl
{
    public RealtimeMonitorView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 显示时长输入框回车：先把输入值提交到绑定源（LostFocus 绑定此时尚未提交），
    /// 再执行“确认”命令按新时长对齐视口，等效于点“确认”按钮。
    /// </summary>
    private void OnDurationKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        if (sender is TextBox tb)
            tb.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();

        if (DataContext is RealtimeMonitorViewModel vm && vm.ApplyDisplayDurationCommand.CanExecute(null))
            vm.ApplyDisplayDurationCommand.Execute(null);

        e.Handled = true;
    }
}
