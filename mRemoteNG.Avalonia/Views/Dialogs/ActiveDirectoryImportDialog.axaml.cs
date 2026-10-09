using Avalonia.Controls;
using mRemoteNG.Avalonia.ViewModels;
using mRemoteNG.Core.Config.Import.ActiveDirectory;

namespace mRemoteNG.Avalonia.Views.Dialogs;

/// <summary>
/// Browses Active Directory and returns what to import.
/// <see cref="Window.ShowDialog{TResult}(Window)"/> returns an <see cref="ActiveDirectoryImportRequest"/>
/// (including the bind password), or null when cancelled.
/// </summary>
public partial class ActiveDirectoryImportDialog : Window
{
    public ActiveDirectoryImportDialog() : this(null)
    {
    }

    public ActiveDirectoryImportDialog(ActiveDirectoryImportViewModel? viewModel)
    {
        InitializeComponent();
        var vm = viewModel ?? new ActiveDirectoryImportViewModel();
        DataContext = vm;
        CancelButton.Click += (_, _) => Close(null);
        ImportButton.Click += (_, _) =>
        {
            if (vm.BuildRequest() is { } request)
                Close(request);
        };
        Closed += (_, _) => vm.Dispose();
    }

    public ActiveDirectoryImportViewModel ViewModel => (ActiveDirectoryImportViewModel)DataContext!;
}
