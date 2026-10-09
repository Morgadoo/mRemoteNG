using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.LogicalTree;
using mRemoteNG.Avalonia.Controls;
using mRemoteNG.Avalonia.ViewModels;
using mRemoteNG.Avalonia.Views.OptionsPages;

namespace mRemoteNG.Avalonia.Views;

public partial class OptionsWindow : Window
{
    public OptionsWindow()
    {
        InitializeComponent();
        var vm = AppServices.GetRequired<OptionsWindowViewModel>();
        DataContext = vm;
        vm.CloseRequested += () => Close();

        // Enter in the search box opens the first matching page instead of pressing OK.
        SearchBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter)
                return;
            e.Handled = true;
            if (CategoryList.ItemCount > 0)
            {
                CategoryList.SelectedIndex = Math.Max(0, CategoryList.SelectedIndex);
                (CategoryList.ContainerFromIndex(CategoryList.SelectedIndex) as InputElement)?.Focus(NavigationMethod.Tab);
            }
        };
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is not OptionsWindowViewModel vm)
            return;

        IndexPages(vm);
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(OptionsWindowViewModel.CurrentPage))
                PageScroller.Offset = default;
        };
    }

    /// <summary>
    /// Lets the search box find pages by their settings: collects the labels and descriptions of every page
    /// (as shown, in the UI language) into <see cref="SettingsCategoryViewModel.Keywords"/>.
    /// </summary>
    private static void IndexPages(OptionsWindowViewModel vm)
    {
        foreach (var category in vm.Categories)
        {
            if (category.Keywords.Count > 0 || CreatePage(category.Key) is not { } page)
                continue;
            category.Keywords = PageTexts(page).Distinct().ToList();
        }
        vm.RefreshVisibleCategories();
    }

    internal static IEnumerable<string> PageTexts(Control page)
    {
        foreach (var element in page.GetLogicalDescendants().Prepend(page))
        {
            var texts = element switch
            {
                SettingRow row => [row.Header, row.Description],
                TextBlock block => [block.Text],
                ContentControl { Content: string content } => [content],
                TextBox box => [box.Watermark],
                _ => Array.Empty<string?>(),
            };
            foreach (var text in texts)
            {
                if (!string.IsNullOrWhiteSpace(text))
                    yield return text;
            }
        }
    }

    private static Control? CreatePage(string key) => key switch
    {
        "general" => new GeneralSettingsPage(),
        "appearance" => new AppearanceSettingsPage(),
        "connections" => new ConnectionSettingsPage(),
        "credentials" => new CredentialsSettingsPage(),
        "notifications" => new NotificationsSettingsPage(),
        "updates" => new UpdatesSettingsPage(),
        "externalProviders" => new ExternalProvidersSettingsPage(),
        "tabspanels" => new TabsPanelsSettingsPage(),
        "saving" => new SavingSettingsPage(),
        "sql" => new SqlServerSettingsPage(),
        "logging" => new LoggingSettingsPage(),
        _ => null,
    };
}
