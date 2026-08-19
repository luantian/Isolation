using System.Collections.ObjectModel;
using System.Windows;
using IsolationLeakage.App.Models.Database;

namespace IsolationLeakage.App.Views;

public partial class TestRecordEditDialog : Window
{
    public TestRecordEditDialog()
    {
        InitializeComponent();
        DataContext = this;
    }

    public static readonly DependencyProperty AvailableRecipesProperty =
        DependencyProperty.Register(nameof(AvailableRecipes), typeof(ObservableCollection<TestRecipe>), typeof(TestRecordEditDialog), new PropertyMetadata(new ObservableCollection<TestRecipe>()));

    public static readonly DependencyProperty SelectedRecipeProperty =
        DependencyProperty.Register(nameof(SelectedRecipe), typeof(TestRecipe), typeof(TestRecordEditDialog), new PropertyMetadata(null));

    public static readonly DependencyProperty RemarkProperty =
        DependencyProperty.Register(nameof(Remark), typeof(string), typeof(TestRecordEditDialog), new PropertyMetadata(string.Empty));

    public ObservableCollection<TestRecipe> AvailableRecipes
    {
        get => (ObservableCollection<TestRecipe>)GetValue(AvailableRecipesProperty);
        set => SetValue(AvailableRecipesProperty, value);
    }

    /// <summary>
    /// 选中的试验路径（下拉框当前选中项）
    /// </summary>
    public TestRecipe? SelectedRecipe
    {
        get => (TestRecipe?)GetValue(SelectedRecipeProperty);
        set => SetValue(SelectedRecipeProperty, value);
    }

    /// <summary>
    /// 备注内容
    /// </summary>
    public string Remark
    {
        get => (string)GetValue(RemarkProperty);
        set => SetValue(RemarkProperty, value);
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
