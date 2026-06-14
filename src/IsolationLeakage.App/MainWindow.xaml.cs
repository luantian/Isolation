using System.Windows;

namespace IsolationLeakage.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        // 数据库连接状态已在首页概览顶部显示，无需弹窗
    }
}
