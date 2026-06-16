using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using IsolationLeakage.App.Models;
using IsolationLeakage.App.Models.Database;
using IsolationLeakage.App.ViewModels;

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

/// <summary>节点类型 → Segoe MDL2 Assets 图标</summary>
public sealed class NodeTypeToGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value switch
        {
            PathNodeType.System => "\xF168",      // 🏭 工厂/系统 (PLANT)
            PathNodeType.Penetration => "\xE8C7", // 🔩 贯穿件/管道连接 (Connect)
            PathNodeType.Valve => "\xE72D",       // ⚙ 阀门/阀门控制 (Circuit)
            PathNodeType.OtherComponent => "\xE9AF", // 🔧 部件/组件 (Component)
            _ => "\xE8A5"
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
