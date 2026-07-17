using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace IsolationLeakage.App.Controls;

public partial class SectionPanel : UserControl
{
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(SectionPanel), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty StatusTextProperty =
        DependencyProperty.Register(nameof(StatusText), typeof(string), typeof(SectionPanel), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty StatusBrushProperty =
        DependencyProperty.Register(nameof(StatusBrush), typeof(Brush), typeof(SectionPanel), new PropertyMetadata(Brushes.Transparent));

    public static readonly DependencyProperty PanelContentProperty =
        DependencyProperty.Register(nameof(PanelContent), typeof(object), typeof(SectionPanel), new PropertyMetadata(null));

    public static readonly DependencyProperty ContentPaddingProperty =
        DependencyProperty.Register(nameof(ContentPadding), typeof(Thickness), typeof(SectionPanel), new PropertyMetadata(new Thickness(12, 10, 12, 4)));

    public static readonly DependencyProperty HeaderExtraContentProperty =
        DependencyProperty.Register(nameof(HeaderExtraContent), typeof(object), typeof(SectionPanel), new PropertyMetadata(null));

    public SectionPanel()
    {
        InitializeComponent();
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string StatusText
    {
        get => (string)GetValue(StatusTextProperty);
        set => SetValue(StatusTextProperty, value);
    }

    public Brush StatusBrush
    {
        get => (Brush)GetValue(StatusBrushProperty);
        set => SetValue(StatusBrushProperty, value);
    }

    public object? PanelContent
    {
        get => GetValue(PanelContentProperty);
        set => SetValue(PanelContentProperty, value);
    }

    public Thickness ContentPadding
    {
        get => (Thickness)GetValue(ContentPaddingProperty);
        set => SetValue(ContentPaddingProperty, value);
    }

    public object? HeaderExtraContent
    {
        get => GetValue(HeaderExtraContentProperty);
        set => SetValue(HeaderExtraContentProperty, value);
    }
}
