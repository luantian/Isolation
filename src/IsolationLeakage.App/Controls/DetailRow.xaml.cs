using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace IsolationLeakage.App.Controls;

public partial class DetailRow : UserControl
{
    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(nameof(Label), typeof(string), typeof(DetailRow), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(nameof(Value), typeof(string), typeof(DetailRow), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty LabelWidthProperty =
        DependencyProperty.Register(nameof(LabelWidth), typeof(GridLength), typeof(DetailRow), new PropertyMetadata(new GridLength(92)));

    public static readonly DependencyProperty ValueAlignmentProperty =
        DependencyProperty.Register(nameof(ValueAlignment), typeof(HorizontalAlignment), typeof(DetailRow), new PropertyMetadata(HorizontalAlignment.Right));

    public static readonly DependencyProperty ValueBrushProperty =
        DependencyProperty.Register(nameof(ValueBrush), typeof(Brush), typeof(DetailRow), new PropertyMetadata(Brushes.Black));

    public static readonly DependencyProperty ValueFontFamilyProperty =
        DependencyProperty.Register(nameof(ValueFontFamily), typeof(FontFamily), typeof(DetailRow), new PropertyMetadata(new FontFamily("Consolas, Microsoft YaHei UI")));

    public DetailRow()
    {
        InitializeComponent();
    }

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public string Value
    {
        get => (string)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public GridLength LabelWidth
    {
        get => (GridLength)GetValue(LabelWidthProperty);
        set => SetValue(LabelWidthProperty, value);
    }

    public HorizontalAlignment ValueAlignment
    {
        get => (HorizontalAlignment)GetValue(ValueAlignmentProperty);
        set => SetValue(ValueAlignmentProperty, value);
    }

    public Brush ValueBrush
    {
        get => (Brush)GetValue(ValueBrushProperty);
        set => SetValue(ValueBrushProperty, value);
    }

    public FontFamily ValueFontFamily
    {
        get => (FontFamily)GetValue(ValueFontFamilyProperty);
        set => SetValue(ValueFontFamilyProperty, value);
    }
}
