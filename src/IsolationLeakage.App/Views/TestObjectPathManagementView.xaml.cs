using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
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
        var brushKey = value switch
        {
            PathNodeType.System => "BrushPrimary",
            PathNodeType.Penetration => "BrushSecondary",
            PathNodeType.Valve => "BrushWarn",
            PathNodeType.OtherComponent => "BrushMutedText",
            _ => "BrushMutedText"
        };
        return Application.Current.FindResource(brushKey);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>节点类型 → 类型徽章背景色</summary>
public sealed class NodeTypeToBadgeBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var brushKey = value switch
        {
            PathNodeType.System => "BrushPrimary",
            PathNodeType.Penetration => "BrushSecondary",
            PathNodeType.Valve => "BrushWarn",
            PathNodeType.OtherComponent => "BrushMutedText",
            _ => "BrushMutedText"
        };
        return Application.Current.FindResource(brushKey);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
