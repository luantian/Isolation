using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace IsolationLeakage.App.Controls;

public partial class StatusDot : UserControl
{
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(nameof(Text), typeof(string), typeof(StatusDot), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty DotBrushProperty =
        DependencyProperty.Register(nameof(DotBrush), typeof(Brush), typeof(StatusDot), new PropertyMetadata(Brushes.Gray));

    public static readonly DependencyProperty TextBrushProperty =
        DependencyProperty.Register(nameof(TextBrush), typeof(Brush), typeof(StatusDot), new PropertyMetadata(Brushes.Black));

    public static readonly DependencyProperty DotSizeProperty =
        DependencyProperty.Register(nameof(DotSize), typeof(double), typeof(StatusDot), new PropertyMetadata(8d));

    public StatusDot()
    {
        InitializeComponent();
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public Brush DotBrush
    {
        get => (Brush)GetValue(DotBrushProperty);
        set => SetValue(DotBrushProperty, value);
    }

    public Brush TextBrush
    {
        get => (Brush)GetValue(TextBrushProperty);
        set => SetValue(TextBrushProperty, value);
    }

    public double DotSize
    {
        get => (double)GetValue(DotSizeProperty);
        set => SetValue(DotSizeProperty, value);
    }
}
