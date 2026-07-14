using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace IsolationLeakage.App.Controls;

/// <summary>
/// 分页控件
/// </summary>
public class PaginationControl : Control
{
    static PaginationControl()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(PaginationControl),
            new FrameworkPropertyMetadata(typeof(PaginationControl)));
    }

    public static readonly DependencyProperty CurrentPageProperty =
        DependencyProperty.Register(nameof(CurrentPage), typeof(int), typeof(PaginationControl),
            new FrameworkPropertyMetadata(1, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnPagePropertyChanged));

    public static readonly DependencyProperty TotalCountProperty =
        DependencyProperty.Register(nameof(TotalCount), typeof(int), typeof(PaginationControl),
            new FrameworkPropertyMetadata(0, OnPagePropertyChanged));

    public static readonly DependencyProperty PageSizeProperty =
        DependencyProperty.Register(nameof(PageSize), typeof(int), typeof(PaginationControl),
            new FrameworkPropertyMetadata(20, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnPagePropertyChanged));

    public static readonly DependencyProperty PageSizeOptionsProperty =
        DependencyProperty.Register(nameof(PageSizeOptions), typeof(int[]), typeof(PaginationControl),
            new FrameworkPropertyMetadata(new int[] { 10, 20, 50, 100, 200 }));

    public static readonly DependencyProperty JumpToPageProperty =
        DependencyProperty.Register(nameof(JumpToPage), typeof(int), typeof(PaginationControl),
            new FrameworkPropertyMetadata(1, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty GotoPageCommandProperty =
        DependencyProperty.Register(nameof(GotoPageCommand), typeof(ICommand), typeof(PaginationControl));

    public static readonly DependencyProperty ChangePageSizeCommandProperty =
        DependencyProperty.Register(nameof(ChangePageSizeCommand), typeof(ICommand), typeof(PaginationControl));

    // 只读依赖属性 Key（必须先声明，public Property 从中获取）
    private static readonly DependencyPropertyKey TotalPagesPropertyKey =
        DependencyProperty.RegisterReadOnly(nameof(TotalPages), typeof(int), typeof(PaginationControl),
            new FrameworkPropertyMetadata(0));

    private static readonly DependencyPropertyKey HasPreviousPagePropertyKey =
        DependencyProperty.RegisterReadOnly(nameof(HasPreviousPage), typeof(bool), typeof(PaginationControl),
            new FrameworkPropertyMetadata(false));

    private static readonly DependencyPropertyKey HasNextPagePropertyKey =
        DependencyProperty.RegisterReadOnly(nameof(HasNextPage), typeof(bool), typeof(PaginationControl),
            new FrameworkPropertyMetadata(false));

    private static readonly DependencyPropertyKey PageStatusTextPropertyKey =
        DependencyProperty.RegisterReadOnly(nameof(PageStatusText), typeof(string), typeof(PaginationControl),
            new FrameworkPropertyMetadata("无数据"));

    // 只读依赖属性公开标识符（从 Key 获取，不是重新注册）
    public static readonly DependencyProperty TotalPagesProperty = TotalPagesPropertyKey.DependencyProperty;
    public static readonly DependencyProperty HasPreviousPageProperty = HasPreviousPagePropertyKey.DependencyProperty;
    public static readonly DependencyProperty HasNextPageProperty = HasNextPagePropertyKey.DependencyProperty;
    public static readonly DependencyProperty PageStatusTextProperty = PageStatusTextPropertyKey.DependencyProperty;

    public int CurrentPage
    {
        get => (int)GetValue(CurrentPageProperty);
        set => SetValue(CurrentPageProperty, value);
    }

    public int TotalCount
    {
        get => (int)GetValue(TotalCountProperty);
        set => SetValue(TotalCountProperty, value);
    }

    public int PageSize
    {
        get => (int)GetValue(PageSizeProperty);
        set => SetValue(PageSizeProperty, value);
    }

    public int[] PageSizeOptions
    {
        get => (int[])GetValue(PageSizeOptionsProperty);
        set => SetValue(PageSizeOptionsProperty, value);
    }

    public int JumpToPage
    {
        get => (int)GetValue(JumpToPageProperty);
        set => SetValue(JumpToPageProperty, value);
    }

    public int TotalPages
    {
        get => (int)GetValue(TotalPagesProperty);
        private set => SetValue(TotalPagesPropertyKey, value);
    }

    public bool HasPreviousPage
    {
        get => (bool)GetValue(HasPreviousPageProperty);
        private set => SetValue(HasPreviousPagePropertyKey, value);
    }

    public bool HasNextPage
    {
        get => (bool)GetValue(HasNextPageProperty);
        private set => SetValue(HasNextPagePropertyKey, value);
    }

    public string PageStatusText
    {
        get => (string)GetValue(PageStatusTextProperty);
        private set => SetValue(PageStatusTextPropertyKey, value);
    }

    public ICommand GotoPageCommand
    {
        get => (ICommand)GetValue(GotoPageCommandProperty);
        set => SetValue(GotoPageCommandProperty, value);
    }

    // 内部命令
    private readonly SimpleCommand _goToFirstCommand;
    private readonly SimpleCommand _goToPrevCommand;
    private readonly SimpleCommand _goToNextCommand;
    private readonly SimpleCommand _goToLastCommand;
    private readonly SimpleCommand _jumpToPageCommand;
    private readonly SimpleCommand<string> _changePageSizeCommand;

    public PaginationControl()
    {
        _goToFirstCommand = new SimpleCommand(GoToFirst, () => HasPreviousPage);
        _goToPrevCommand = new SimpleCommand(GoToPrev, () => HasPreviousPage);
        _goToNextCommand = new SimpleCommand(GoToNext, () => HasNextPage);
        _goToLastCommand = new SimpleCommand(GoToLast, () => HasNextPage);
        _jumpToPageCommand = new SimpleCommand(ExecuteJumpToPage, CanJumpToPage);
        _changePageSizeCommand = new SimpleCommand<string>(ExecuteChangePageSize);
    }

    public SimpleCommand GoToFirstCommand => _goToFirstCommand;
    public SimpleCommand GoToPreviousCommand => _goToPrevCommand;
    public SimpleCommand GoToNextCommand => _goToNextCommand;
    public SimpleCommand GoToLastCommand => _goToLastCommand;
    public SimpleCommand JumpToPageCommand => _jumpToPageCommand;
    public SimpleCommand<string> ChangePageSizeCommand => _changePageSizeCommand;

    private static void OnPagePropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (PaginationControl)d;
        control.UpdateState();
    }

    private void UpdateState()
    {
        var totalPages = TotalCount > 0 ? (int)Math.Ceiling((double)TotalCount / Math.Max(PageSize, 1)) : 0;
        TotalPages = totalPages;
        HasPreviousPage = CurrentPage > 1;
        HasNextPage = CurrentPage < totalPages;
        PageStatusText = totalPages > 0 ? $"第 {CurrentPage} / {totalPages} 页" : "无数据";

        // 同步 JumpToPage 到当前页
        if (JumpToPage != CurrentPage)
        {
            JumpToPage = CurrentPage;
        }

        _goToFirstCommand.NotifyCanExecuteChanged();
        _goToPrevCommand.NotifyCanExecuteChanged();
        _goToNextCommand.NotifyCanExecuteChanged();
        _goToLastCommand.NotifyCanExecuteChanged();
        _jumpToPageCommand.NotifyCanExecuteChanged();
    }

    private void GoToFirst() => GoToPage(1);
    private void GoToPrev() => GoToPage(CurrentPage - 1);
    private void GoToNext() => GoToPage(CurrentPage + 1);
    private void GoToLast() => GoToPage(TotalPages);

    private bool CanJumpToPage() => TotalPages > 0;

    private void ExecuteJumpToPage()
    {
        if (JumpToPage >= 1 && JumpToPage <= TotalPages)
        {
            GoToPage(JumpToPage);
        }
    }

    private void ExecuteChangePageSize(string? pageSizeStr)
    {
        if (int.TryParse(pageSizeStr, out int newPageSize) && newPageSize > 0)
        {
            PageSize = newPageSize;
            CurrentPage = 1;
            GotoPageCommand?.Execute(1);
        }
    }

    private void GoToPage(int page)
    {
        if (page < 1 || page > TotalPages) return;

        CurrentPage = page;
        GotoPageCommand?.Execute(page);
    }

    /// <summary>
    /// 从外部刷新分页状态
    /// </summary>
    public void Refresh() => UpdateState();
}

/// <summary>
/// 简单 ICommand 实现（用于控件内部，避免与 CommunityToolkit 冲突）
/// </summary>
public class SimpleCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    public SimpleCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;
    public void Execute(object? parameter) => _execute();

    public void NotifyCanExecuteChanged() => CommandManager.InvalidateRequerySuggested();
}

/// <summary>
/// 带参数的 SimpleCommand 实现
/// </summary>
public class SimpleCommand<T> : ICommand
{
    private readonly Action<T> _execute;
    private readonly Predicate<T>? _canExecute;

    public SimpleCommand(Action<T> execute, Predicate<T>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter)
    {
        if (_canExecute == null) return true;
        if (parameter is T t) return _canExecute(t);
        return false;
    }

    public void Execute(object? parameter)
    {
        if (parameter is T t) _execute(t);
    }
}
