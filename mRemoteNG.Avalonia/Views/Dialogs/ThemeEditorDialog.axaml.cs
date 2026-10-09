using Avalonia.Controls;
using Avalonia.Interactivity;
using mRemoteNG.Avalonia.ViewModels;

namespace mRemoteNG.Avalonia.Views.Dialogs;

/// <summary>
/// Edits and saves user themes. <see cref="Window.ShowDialog{TResult}(Window)"/> returns the name of the theme
/// saved last, or null. On close the theme from the settings is shown again (unsaved edits are dropped).
/// </summary>
public partial class ThemeEditorDialog : Window
{
    public ThemeEditorDialog()
    {
        InitializeComponent();
    }

    public ThemeEditorDialog(ThemeEditorViewModel viewModel) : this()
    {
        DataContext = viewModel;
        // Unsaved edits are only a preview: show the theme from the settings again.
        Closing += (_, _) => viewModel.RevertPreview();
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ThemeEditorViewModel vm)
            vm.Save();
    }

    private void OnClose(object? sender, RoutedEventArgs e) =>
        Close((DataContext as ThemeEditorViewModel)?.SavedThemeName);
}
