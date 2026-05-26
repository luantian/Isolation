using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace IsolationLeakage.App.Controls;

public partial class MetricCard : UserControl
{
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(MetricCard), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(nameof(Value), typeof(string), typeof(MetricCard), new PropertyMetadata("--"));

    public static readonly DependencyProperty UnitProperty =
        DependencyProperty.Register(nameof(Unit), typeof(string), typeof(MetricCard), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty AccentBrushProperty =
        DependencyProperty.Register(nameof(AccentBrush), typeof(Brush), typeof(MetricCard), new PropertyMetadata(Brushes.DodgerBlue));

    public static readonly DependencyProperty CardBackgroundProperty =
        DependencyProperty.Register(nameof(CardBackground), typeof(Brush), typeof(MetricCard), new PropertyMetadata(Brushes.White));

    public MetricCard()
    {
        InitializeComponent();
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Value
    {
        get => (string)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public string Unit
    {
        get => (string)GetValue(UnitProperty);
        set => SetValue(UnitProperty, value);
    }

    public Brush AccentBrush
    {
        get => (Brush)GetValue(AccentBrushProperty);
        set => SetValue(AccentBrushProperty, value);
    }

    public Brush CardBackground
    {
        get => (Brush)GetValue(CardBackgroundProperty);
        set => SetValue(CardBackgroundProperty, value);
    }
}
