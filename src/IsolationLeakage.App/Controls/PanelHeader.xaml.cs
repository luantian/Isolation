using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace IsolationLeakage.App.Controls;

public partial class PanelHeader : UserControl
{
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(PanelHeader), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty StatusTextProperty =
        DependencyProperty.Register(nameof(StatusText), typeof(string), typeof(PanelHeader), new PropertyMetadata(string.Empty, OnStatusChanged));

    public static readonly DependencyProperty StatusBrushProperty =
        DependencyProperty.Register(nameof(StatusBrush), typeof(Brush), typeof(PanelHeader), new PropertyMetadata(Brushes.Transparent));

    public static readonly DependencyProperty StatusVisibilityProperty =
        DependencyProperty.Register(nameof(StatusVisibility), typeof(Visibility), typeof(PanelHeader), new PropertyMetadata(Visibility.Collapsed));

    public PanelHeader()
    {
        InitializeComponent();
        UpdateStatusVisibility();
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

    public Visibility StatusVisibility
    {
        get => (Visibility)GetValue(StatusVisibilityProperty);
        private set => SetValue(StatusVisibilityProperty, value);
    }

    private static void OnStatusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((PanelHeader)d).UpdateStatusVisibility();
    }

    private void UpdateStatusVisibility()
    {
        StatusVisibility = string.IsNullOrWhiteSpace(StatusText) ? Visibility.Collapsed : Visibility.Visible;
    }
}
