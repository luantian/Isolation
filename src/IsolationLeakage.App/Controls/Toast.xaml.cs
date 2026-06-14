using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace IsolationLeakage.App.Controls;

public partial class Toast : UserControl
{
    public Toast()
    {
        InitializeComponent();
    }

    /// <summary>显示成功提示</summary>
    public void ShowSuccess(string message, double durationSeconds = 2.5)
    {
        Show(message, ToastType.Success, durationSeconds);
    }

    /// <summary>显示错误提示</summary>
    public void ShowError(string message, double durationSeconds = 3)
    {
        Show(message, ToastType.Error, durationSeconds);
    }

    /// <summary>显示警告提示</summary>
    public void ShowWarning(string message, double durationSeconds = 2.5)
    {
        Show(message, ToastType.Warning, durationSeconds);
    }

    private void Show(string message, ToastType type, double durationSeconds)
    {
        // 设置内容
        MessageText.Text = message;

        // 根据类型设置样式
        var (icon, bg, fg, border) = type switch
        {
            ToastType.Success => ("", "#E8F5E9", "#2E7D32", "#A5D6A7"),
            ToastType.Error => ("", "#FFF3E0", "#E65100", "#FFCC80"),
            ToastType.Warning => ("", "#FFF8E1", "#F57F17", "#FFE082"),
            _ => ("", "#E8F5E9", "#2E7D32", "#A5D6A7")
        };

        IconText.Text = icon;
        IconText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(fg));
        MessageText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(fg));
        ToastBorder.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(bg));
        ToastBorder.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(border));

        // 显示并开始动画
        Visibility = Visibility.Visible;
        ToastBorder.Opacity = 0;

        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200))
        {
            BeginTime = TimeSpan.Zero
        };
        ToastBorder.BeginAnimation(UIElement.OpacityProperty, fadeIn);

        // 定时隐藏
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(durationSeconds)
        };
        timer.Tick += (s, e) =>
        {
            timer.Stop();
            HideToast();
        };
        timer.Start();
    }

    private void HideToast()
    {
        var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(200));
        fadeOut.Completed += (s, e) =>
        {
            Visibility = Visibility.Collapsed;
        };
        ToastBorder.BeginAnimation(UIElement.OpacityProperty, fadeOut);
    }
}

public enum ToastType
{
    Success,
    Error,
    Warning
}
