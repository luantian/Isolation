using System;
using System.Windows;
using System.Windows.Controls;

namespace IsolationLeakage.App.Controls;

/// <summary>
/// 日期范围选择器组件
/// 提供起止日期选择和快捷选择（今天、本周、本月）
/// </summary>
public partial class DateRangePicker : UserControl
{
    #region 依赖属性

    /// <summary>
    /// 开始日期
    /// </summary>
    public static readonly DependencyProperty FromDateProperty =
        DependencyProperty.Register(
            nameof(FromDate),
            typeof(DateTime?),
            typeof(DateRangePicker),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnFromDateChanged));

    /// <summary>
    /// 结束日期
    /// </summary>
    public static readonly DependencyProperty ToDateProperty =
        DependencyProperty.Register(
            nameof(ToDate),
            typeof(DateTime?),
            typeof(DateRangePicker),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnToDateChanged));

    /// <summary>
    /// 日期范围变更事件
    /// </summary>
    public event EventHandler? DateRangeChanged;

    #endregion

    #region 属性

    public DateTime? FromDate
    {
        get => (DateTime?)GetValue(FromDateProperty);
        set => SetValue(FromDateProperty, value);
    }

    public DateTime? ToDate
    {
        get => (DateTime?)GetValue(ToDateProperty);
        set => SetValue(ToDateProperty, value);
    }

    #endregion

    public DateRangePicker()
    {
        InitializeComponent();
    }

    #region 回调方法

    private static void OnFromDateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is DateRangePicker picker)
        {
            picker.FromDatePicker.SelectedDate = (DateTime?)e.NewValue;
        }
    }

    private static void OnToDateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is DateRangePicker picker)
        {
            picker.ToDatePicker.SelectedDate = (DateTime?)e.NewValue;
        }
    }

    #endregion

    #region 事件处理

    private void OnFromDateChanged(object sender, SelectionChangedEventArgs e)
    {
        FromDate = FromDatePicker.SelectedDate;
        DateRangeChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnToDateChanged(object sender, SelectionChangedEventArgs e)
    {
        ToDate = ToDatePicker.SelectedDate;
        DateRangeChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnTodayClick(object sender, RoutedEventArgs e)
    {
        var today = DateTime.Today;
        FromDate = today;
        ToDate = today;
    }

    private void OnWeekClick(object sender, RoutedEventArgs e)
    {
        var today = DateTime.Today;
        var dayOfWeek = (int)today.DayOfWeek;
        var weekStart = today.AddDays(-dayOfWeek);
        var weekEnd = weekStart.AddDays(6);
        FromDate = weekStart;
        ToDate = weekEnd;
    }

    private void OnMonthClick(object sender, RoutedEventArgs e)
    {
        var today = DateTime.Today;
        var monthStart = new DateTime(today.Year, today.Month, 1);
        var monthEnd = monthStart.AddMonths(1).AddDays(-1);
        FromDate = monthStart;
        ToDate = monthEnd;
    }

    #endregion
}