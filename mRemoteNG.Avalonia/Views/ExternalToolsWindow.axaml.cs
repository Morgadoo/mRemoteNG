using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using mRemoteNG.Avalonia.ViewModels;
using mRemoteNG.Avalonia.Views.Dialogs;
using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Localization;
using mRemoteNG.Protocols.External;

namespace mRemoteNG.Avalonia.Views;

/// <summary>The External Tools window (see <see cref="ExternalToolsWindowViewModel"/>).</summary>
public partial class ExternalToolsWindow : Window
{
    public ExternalToolsWindow() : this(null)
    {
    }

    public ExternalToolsWindow(ExternalToolsWindowViewModel? viewModel)
    {
        InitializeComponent();
        var vm = viewModel ?? new ExternalToolsWindowViewModel(AppServices.GetRequired<ExternalToolsService>());
        DataContext = vm;
        vm.CloseRequested += Close;
        VariableChips.ItemsSource = mRemoteNG.Core.Tools.ExternalToolVariables.Names.Select(n => $"%{n}%").ToList();
        vm.ConfirmDeleteAsync = name => MessageDialog.ConfirmAsync(this, Localizer.Get("DeleteExternalTool", "Delete External Tool"),
            Localizer.Format("ConfirmDeleteExternalToolDetailFormat", name),
            Localizer.Get("Delete"), Localizer.Get("_Cancel"), confirmIsDefault: false);
    }

    public ExternalToolsWindowViewModel ViewModel => (ExternalToolsWindowViewModel)DataContext!;

    /// <summary>
    /// Opens the window for <paramref name="targetConnection"/> (the tree selection: "Launch" and the preview use it).
    /// </summary>
    public static Task ShowAsync(Window owner, ConnectionInfo? targetConnection)
    {
        var vm = new ExternalToolsWindowViewModel(AppServices.GetRequired<ExternalToolsService>(), targetConnection);
        return new ExternalToolsWindow(vm).ShowDialog(owner);
    }

    /// <summary>Inserts the clicked variable chip at the caret of the arguments box.</summary>
    private void OnInsertVariable(object? sender, RoutedEventArgs e)
    {
        if (ViewModel.Selected is not { } tool || sender is not Button { Content: string variable })
            return;
        var text = tool.Arguments ?? string.Empty;
        var caret = Math.Clamp(ArgumentsBox.CaretIndex, 0, text.Length);
        if (!ArgumentsBox.IsFocused)
            caret = text.Length;
        var separator = caret > 0 && !char.IsWhiteSpace(text[caret - 1]) ? " " : string.Empty;
        tool.Arguments = text[..caret] + separator + variable + text[caret..];
        ArgumentsBox.Focus();
        ArgumentsBox.CaretIndex = caret + separator.Length + variable.Length;
    }

    private async void OnBrowseFileName(object? sender, RoutedEventArgs e)
    {
        if (ViewModel.Selected is not { } tool)
            return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Localizer.Get("SelectTheProgram"),
            AllowMultiple = false,
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
            tool.FileName = path;
    }

    private async void OnBrowseWorkingDir(object? sender, RoutedEventArgs e)
    {
        if (ViewModel.Selected is not { } tool)
            return;
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Localizer.Get("SelectTheWorkingDirectory"),
            AllowMultiple = false,
        });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
            tool.WorkingDir = path;
    }

    private async void OnBrowseIcon(object? sender, RoutedEventArgs e)
    {
        if (ViewModel.Selected is not { } tool)
            return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Localizer.Get("SelectAnIcon"),
            AllowMultiple = false,
            FileTypeFilter = [FilePickerFileTypes.ImageAll],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
            tool.IconPath = path;
    }
}
