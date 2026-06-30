using System.Collections.ObjectModel;
using System.Windows;
using IsolationLeakage.App.Models.Database;

namespace IsolationLeakage.App.Views;

public partial class RecipeChangeDialog : Window
{
    public RecipeChangeDialog()
    {
        InitializeComponent();
        DataContext = this;
    }

    public static readonly DependencyProperty CurrentRecipeNameProperty =
        DependencyProperty.Register(nameof(CurrentRecipeName), typeof(string), typeof(RecipeChangeDialog), new PropertyMetadata("（无）"));

    public static readonly DependencyProperty AvailableRecipesProperty =
        DependencyProperty.Register(nameof(AvailableRecipes), typeof(ObservableCollection<TestRecipe>), typeof(RecipeChangeDialog), new PropertyMetadata(new ObservableCollection<TestRecipe>()));

    public static readonly DependencyProperty SelectedRecipeProperty =
        DependencyProperty.Register(nameof(SelectedRecipe), typeof(TestRecipe), typeof(RecipeChangeDialog), new PropertyMetadata(null));

    public static readonly DependencyProperty RecordCountProperty =
        DependencyProperty.Register(nameof(RecordCount), typeof(int), typeof(RecipeChangeDialog), new PropertyMetadata(1));

    public string CurrentRecipeName
    {
        get => (string)GetValue(CurrentRecipeNameProperty);
        set => SetValue(CurrentRecipeNameProperty, value);
    }

    public ObservableCollection<TestRecipe> AvailableRecipes
    {
        get => (ObservableCollection<TestRecipe>)GetValue(AvailableRecipesProperty);
        set => SetValue(AvailableRecipesProperty, value);
    }

    public TestRecipe? SelectedRecipe
    {
        get => (TestRecipe?)GetValue(SelectedRecipeProperty);
        set => SetValue(SelectedRecipeProperty, value);
    }

    public int RecordCount
    {
        get => (int)GetValue(RecordCountProperty);
        set => SetValue(RecordCountProperty, value);
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
