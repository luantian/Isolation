using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace IsolationLeakage.App.Controls;

public partial class NavItem : UserControl
{
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(nameof(Text), typeof(string), typeof(NavItem), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty IconGlyphProperty =
        DependencyProperty.Register(nameof(IconGlyph), typeof(string), typeof(NavItem), new PropertyMetadata("\uE10F"));

    public static readonly DependencyProperty IsActiveProperty =
        DependencyProperty.Register(nameof(IsActive), typeof(bool), typeof(NavItem), new PropertyMetadata(false, OnVisualPropertyChanged));

    public static readonly DependencyProperty ActiveBackgroundProperty =
        DependencyProperty.Register(nameof(ActiveBackground), typeof(Brush), typeof(NavItem), new PropertyMetadata(Brushes.Transparent));

    public static readonly DependencyProperty ItemBackgroundProperty =
        DependencyProperty.Register(nameof(ItemBackground), typeof(Brush), typeof(NavItem), new PropertyMetadata(Brushes.Transparent));

    public static readonly DependencyProperty IconBackgroundProperty =
        DependencyProperty.Register(nameof(IconBackground), typeof(Brush), typeof(NavItem), new PropertyMetadata(Brushes.Transparent));

    public static readonly DependencyProperty IconForegroundProperty =
        DependencyProperty.Register(nameof(IconForeground), typeof(Brush), typeof(NavItem), new PropertyMetadata(Brushes.Gray));

    public static readonly DependencyProperty LabelBrushProperty =
        DependencyProperty.Register(nameof(LabelBrush), typeof(Brush), typeof(NavItem), new PropertyMetadata(Brushes.Black));

    public static readonly DependencyProperty LabelWeightProperty =
        DependencyProperty.Register(nameof(LabelWeight), typeof(FontWeight), typeof(NavItem), new PropertyMetadata(FontWeights.Normal));

    public static readonly RoutedEvent ClickEvent =
        EventManager.RegisterRoutedEvent(nameof(Click), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(NavItem));

    public static readonly DependencyProperty CommandProperty =
        DependencyProperty.Register(nameof(Command), typeof(ICommand), typeof(NavItem));

    public static readonly DependencyProperty CommandParameterProperty =
        DependencyProperty.Register(nameof(CommandParameter), typeof(object), typeof(NavItem));

    public ICommand Command
    {
        get => (ICommand)GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    public object? CommandParameter
    {
        get => GetValue(CommandParameterProperty);
        set => SetValue(CommandParameterProperty, value);
    }

    public NavItem()
    {
        InitializeComponent();
        UpdateVisualState();
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string IconGlyph
    {
        get => (string)GetValue(IconGlyphProperty);
        set => SetValue(IconGlyphProperty, value);
    }

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public Brush ActiveBackground
    {
        get => (Brush)GetValue(ActiveBackgroundProperty);
        private set => SetValue(ActiveBackgroundProperty, value);
    }

    public Brush ItemBackground
    {
        get => (Brush)GetValue(ItemBackgroundProperty);
        private set => SetValue(ItemBackgroundProperty, value);
    }

    public Brush IconBackground
    {
        get => (Brush)GetValue(IconBackgroundProperty);
        private set => SetValue(IconBackgroundProperty, value);
    }

    public Brush IconForeground
    {
        get => (Brush)GetValue(IconForegroundProperty);
        private set => SetValue(IconForegroundProperty, value);
    }

    public Brush LabelBrush
    {
        get => (Brush)GetValue(LabelBrushProperty);
        private set => SetValue(LabelBrushProperty, value);
    }

    public FontWeight LabelWeight
    {
        get => (FontWeight)GetValue(LabelWeightProperty);
        private set => SetValue(LabelWeightProperty, value);
    }

    public event RoutedEventHandler Click
    {
        add => AddHandler(ClickEvent, value);
        remove => RemoveHandler(ClickEvent, value);
    }

    private static void OnVisualPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((NavItem)d).UpdateVisualState();
    }

    private void UpdateVisualState()
    {
        if (IsActive)
        {
            ActiveBackground = (Brush)FindResource("BrushPrimaryLight");
            ItemBackground = ActiveBackground;
            IconBackground = (Brush)FindResource("BrushPrimary");
            IconForeground = Brushes.White;
            LabelBrush = (Brush)FindResource("BrushPrimary");
            LabelWeight = FontWeights.SemiBold;
        }
        else
        {
            ActiveBackground = Brushes.Transparent;
            ItemBackground = Brushes.Transparent;
            IconBackground = new SolidColorBrush(Color.FromRgb(238, 242, 247));
            IconForeground = (Brush)FindResource("BrushMutedText");
            LabelBrush = (Brush)FindResource("BrushText");
            LabelWeight = FontWeights.Normal;
        }
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (Command?.CanExecute(CommandParameter) == true)
        {
            Command.Execute(CommandParameter);
        }
        RaiseEvent(new RoutedEventArgs(ClickEvent));
    }

    private void OnMouseEnter(object sender, MouseEventArgs e)
    {
        if (IsActive)
        {
            return;
        }

        ItemBackground = new SolidColorBrush(Color.FromRgb(244, 248, 255));
        IconBackground = new SolidColorBrush(Color.FromRgb(226, 235, 250));
        LabelBrush = (Brush)FindResource("BrushPrimary");
    }

    private void OnMouseLeave(object sender, MouseEventArgs e)
    {
        UpdateVisualState();
    }
}
