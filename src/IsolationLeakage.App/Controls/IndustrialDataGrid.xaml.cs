using System.Collections;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace IsolationLeakage.App.Controls;

public partial class IndustrialDataGrid : UserControl
{
    #region 核心数据绑定

    public static readonly DependencyProperty ItemsSourceProperty =
        DependencyProperty.Register(nameof(ItemsSource), typeof(IEnumerable), typeof(IndustrialDataGrid), new PropertyMetadata(null, OnItemsSourceChanged));

    private static void OnItemsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is IndustrialDataGrid grid)
        {
            grid.PART_DataGrid.ItemsSource = e.NewValue as IEnumerable;
        }
    }

    public IEnumerable ItemsSource
    {
        get => (IEnumerable)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public static readonly DependencyProperty SelectedItemProperty =
        DependencyProperty.Register(nameof(SelectedItem), typeof(object), typeof(IndustrialDataGrid), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnSelectedItemChanged));

    private static void OnSelectedItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is IndustrialDataGrid grid)
        {
            grid.PART_DataGrid.SelectedItem = e.NewValue;
        }
    }

    public object SelectedItem
    {
        get => GetValue(SelectedItemProperty);
        set => SetValue(SelectedItemProperty, value);
    }

    #endregion

    #region 列定义（直接暴露给外部，和原生 DataGrid 用法一致）

    /// <summary>
    /// DataGrid 列集合（直接暴露给外部，用法和原生 DataGrid 完全一致）
    /// </summary>
    public ObservableCollection<DataGridColumn> Columns => PART_DataGrid.Columns;

    #endregion

    #region 其他配置

    public static readonly DependencyProperty IsReadOnlyProperty =
        DependencyProperty.Register(nameof(IsReadOnly), typeof(bool), typeof(IndustrialDataGrid), new PropertyMetadata(true));

    public bool IsReadOnly
    {
        get => (bool)GetValue(IsReadOnlyProperty);
        set => SetValue(IsReadOnlyProperty, value);
    }

    public static readonly DependencyProperty VerticalScrollBarVisibilityProperty =
        DependencyProperty.Register(nameof(VerticalScrollBarVisibility), typeof(ScrollBarVisibility), typeof(IndustrialDataGrid), new PropertyMetadata(ScrollBarVisibility.Auto));

    public ScrollBarVisibility VerticalScrollBarVisibility
    {
        get => (ScrollBarVisibility)GetValue(VerticalScrollBarVisibilityProperty);
        set => SetValue(VerticalScrollBarVisibilityProperty, value);
    }

    #endregion

    #region 错误信息

    public static readonly DependencyProperty HasErrorProperty =
        DependencyProperty.Register(nameof(HasError), typeof(bool), typeof(IndustrialDataGrid), new PropertyMetadata(false));

    public bool HasError
    {
        get => (bool)GetValue(HasErrorProperty);
        set => SetValue(HasErrorProperty, value);
    }

    public static readonly DependencyProperty ErrorDetailProperty =
        DependencyProperty.Register(nameof(ErrorDetail), typeof(string), typeof(IndustrialDataGrid), new PropertyMetadata(string.Empty));

    public string ErrorDetail
    {
        get => (string)GetValue(ErrorDetailProperty);
        set => SetValue(ErrorDetailProperty, value);
    }

    public static readonly DependencyProperty CopyErrorCommandProperty =
        DependencyProperty.Register(nameof(CopyErrorCommand), typeof(ICommand), typeof(IndustrialDataGrid), new PropertyMetadata(null));

    public ICommand CopyErrorCommand
    {
        get => (ICommand)GetValue(CopyErrorCommandProperty);
        set => SetValue(CopyErrorCommandProperty, value);
    }

    public Visibility ErrorVisibility => HasError ? Visibility.Visible : Visibility.Collapsed;

    #endregion

    #region 分页

    public static readonly DependencyProperty ShowPaginationProperty =
        DependencyProperty.Register(nameof(ShowPagination), typeof(bool), typeof(IndustrialDataGrid), new PropertyMetadata(true));

    public bool ShowPagination
    {
        get => (bool)GetValue(ShowPaginationProperty);
        set => SetValue(ShowPaginationProperty, value);
    }

    public static readonly DependencyProperty CurrentPageProperty =
        DependencyProperty.Register(nameof(CurrentPage), typeof(int), typeof(IndustrialDataGrid), new FrameworkPropertyMetadata(1, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public int CurrentPage
    {
        get => (int)GetValue(CurrentPageProperty);
        set => SetValue(CurrentPageProperty, value);
    }

    public static readonly DependencyProperty TotalCountProperty =
        DependencyProperty.Register(nameof(TotalCount), typeof(int), typeof(IndustrialDataGrid), new PropertyMetadata(0));

    public int TotalCount
    {
        get => (int)GetValue(TotalCountProperty);
        set => SetValue(TotalCountProperty, value);
    }

    public static readonly DependencyProperty PageSizeProperty =
        DependencyProperty.Register(nameof(PageSize), typeof(int), typeof(IndustrialDataGrid), new PropertyMetadata(20));

    public int PageSize
    {
        get => (int)GetValue(PageSizeProperty);
        set => SetValue(PageSizeProperty, value);
    }

    public static readonly DependencyProperty GotoPageCommandProperty =
        DependencyProperty.Register(nameof(GotoPageCommand), typeof(ICommand), typeof(IndustrialDataGrid), new PropertyMetadata(null));

    public ICommand GotoPageCommand
    {
        get => (ICommand)GetValue(GotoPageCommandProperty);
        set => SetValue(GotoPageCommandProperty, value);
    }

    #endregion

    public IndustrialDataGrid()
    {
        InitializeComponent();

        // 同步内部 DataGrid 的选择到外部
        PART_DataGrid.SelectionChanged += (_, _) =>
        {
            SelectedItem = PART_DataGrid.SelectedItem;
        };
    }
}
