using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using IsolationLeakage.App.Models;
using IsolationLeakage.App.Models.Database;
using IsolationLeakage.App.ViewModels;
using MahApps.Metro.IconPacks;

namespace IsolationLeakage.App.Views;

public partial class TestObjectPathManagementView : UserControl
{
    public TestObjectPathManagementView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is TestObjectPathManagementViewModel oldVm)
            oldVm.PropertyChanged -= OnVmPropertyChanged;
        if (e.NewValue is TestObjectPathManagementViewModel newVm)
            newVm.PropertyChanged += OnVmPropertyChanged;
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TestObjectPathManagementViewModel.SelectedNode))
        {
            if (sender is TestObjectPathManagementViewModel vm && vm.SelectedNode != null)
            {
                // 延迟一帧，等 TreeView 完成绑定更新
                Dispatcher.BeginInvoke(new Action(() => SelectAndHighlightNode(vm.SelectedNode)),
                    System.Windows.Threading.DispatcherPriority.Loaded);
            }
        }
    }

    /// <summary>在 TreeView 中找到节点，展开父级，选中并滚动到可见</summary>
    private void SelectAndHighlightNode(TestObjectPathNode targetNode)
    {
        var treeView = FindVisualChild<TreeView>(this);
        if (treeView == null) return;

        // 展开从根到目标节点的所有父节点
        ExpandPathToNode(treeView, targetNode);

        // 找到目标 TreeViewItem 并选中
        var container = FindTreeViewItem(treeView, targetNode);
        if (container != null)
        {
            container.IsSelected = true;
            container.BringIntoView();
            container.Focus();
        }
    }

    /// <summary>递归展开从根到目标节点的路径</summary>
    private static bool ExpandPathToNode(ItemsControl parentControl, TestObjectPathNode targetNode)
    {
        foreach (var item in parentControl.Items)
        {
            if (item is TestObjectPathNode node)
            {
                if (node == targetNode || IsDescendantOf(node, targetNode))
                {
                    // 展开这个节点
                    if (parentControl is TreeViewItem tvi)
                        tvi.IsExpanded = true;

                    // 如果是目标节点的祖先，继续向下展开
                    if (node != targetNode)
                    {
                        var container = parentControl.ItemContainerGenerator.ContainerFromItem(node) as ItemsControl;
                        if (container != null)
                            ExpandPathToNode(container, targetNode);
                    }
                    return true;
                }

                // 检查子节点
                if (node.Children.Count > 0)
                {
                    var container = parentControl.ItemContainerGenerator.ContainerFromItem(node) as ItemsControl;
                    if (container != null && ExpandPathToNode(container, targetNode))
                    {
                        if (parentControl is TreeViewItem parentTvi)
                            parentTvi.IsExpanded = true;
                        return true;
                    }
                }
            }
        }
        return false;
    }

    /// <summary>检查 target 是否是 node 的后代</summary>
    private static bool IsDescendantOf(TestObjectPathNode node, TestObjectPathNode target)
    {
        foreach (var child in node.Children)
        {
            if (child == target || IsDescendantOf(child, target))
                return true;
        }
        return false;
    }

    /// <summary>在 TreeView 中递归查找数据项对应的 TreeViewItem</summary>
    private static TreeViewItem? FindTreeViewItem(ItemsControl parentControl, TestObjectPathNode targetNode)
    {
        foreach (var item in parentControl.Items)
        {
            if (item == targetNode)
            {
                return parentControl.ItemContainerGenerator.ContainerFromItem(item) as TreeViewItem;
            }

            if (item is TestObjectPathNode node && node.Children.Count > 0)
            {
                var container = parentControl.ItemContainerGenerator.ContainerFromItem(item) as ItemsControl;
                if (container != null)
                {
                    var result = FindTreeViewItem(container, targetNode);
                    if (result != null) return result;
                }
            }
        }
        return null;
    }

    /// <summary>在 VisualTree 中递归查找指定类型的子元素</summary>
    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found) return found;
            var result = FindVisualChild<T>(child);
            if (result != null) return result;
        }
        return null;
    }

    private void PathTree_SelectedItemChanged(object sender, System.Windows.RoutedPropertyChangedEventArgs<object> e)
    {
        if (DataContext is TestObjectPathManagementViewModel vm && e.NewValue is TestObjectPathNode node)
        {
            vm.SelectedNode = node;
        }
    }
}

/// <summary>节点类型 → Material 图标控件</summary>
public sealed class NodeTypeToGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var kind = value switch
        {
            PathNodeType.System => PackIconMaterialKind.Factory,
            PathNodeType.Penetration => PackIconMaterialKind.Connection,
            PathNodeType.Valve => PackIconMaterialKind.Cog,
            PathNodeType.OtherComponent => PackIconMaterialKind.Puzzle,
            _ => PackIconMaterialKind.Help
        };

        return new PackIconMaterial
        {
            Kind = kind,
            Width = 12,
            Height = 12,
            Foreground = System.Windows.Media.Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>节点类型 → 图标背景色</summary>
public sealed class NodeTypeToIconBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        switch (value)
        {
            case PathNodeType.System:
                return Application.Current.FindResource("BrushPrimary");
            case PathNodeType.Penetration:
                return new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#0891B2"));
            case PathNodeType.Valve:
                return Application.Current.FindResource("BrushWarn");
            case PathNodeType.OtherComponent:
                return Application.Current.FindResource("BrushMutedText");
            default:
                return Application.Current.FindResource("BrushMutedText");
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>节点类型 → 类型徽章背景色</summary>
public sealed class NodeTypeToBadgeBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        switch (value)
        {
            case PathNodeType.System:
                return Application.Current.FindResource("BrushPrimary");
            case PathNodeType.Penetration:
                return new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#0891B2"));
            case PathNodeType.Valve:
                return Application.Current.FindResource("BrushWarn");
            case PathNodeType.OtherComponent:
                return Application.Current.FindResource("BrushMutedText");
            default:
                return Application.Current.FindResource("BrushMutedText");
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>布尔值取反转换器（用于 IsEnabled="{Binding IsImporting, Converter=...}"）</summary>
public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool b) return !b;
        return value;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
