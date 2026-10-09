using System.Reactive;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using mRemoteNG.Avalonia.ViewModels;
using mRemoteNG.Protocols.Abstractions;
using mRemoteNG.Protocols.Ssh;

namespace mRemoteNG.Avalonia.Views.Dialogs;

/// <summary>
/// SFTP file browser window. Open it with <see cref="ShowFor"/>.
/// </summary>
public partial class SshFileTransferDialog : Window
{
    public SshFileTransferDialog() : this(CreateViewModel(null))
    {
    }

    public SshFileTransferDialog(SshFileTransferViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        viewModel.PickFilesToUpload.RegisterHandler(async ctx =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = $"Upload to {viewModel.RemotePath}",
                AllowMultiple = true,
            });
            ctx.SetOutput(files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList());
        });
        viewModel.PickDownloadTarget.RegisterHandler(async ctx =>
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Download as",
                SuggestedFileName = ctx.Input,
                ShowOverwritePrompt = true,
            });
            ctx.SetOutput(file?.TryGetLocalPath());
        });
        viewModel.AskFolderName.RegisterHandler(async ctx =>
        {
            var dialog = new TextPromptDialog("New Folder", $"Create a folder in {ctx.Input}:", false, "Folder name", null);
            ctx.SetOutput(await dialog.ShowDialog<string?>(this));
        });
        viewModel.ConfirmDelete.RegisterHandler(async ctx =>
        {
            var dialog = new ConfirmDialog("Delete", ctx.Input, "Delete");
            ctx.SetOutput(await dialog.ShowDialog<bool>(this));
        });

        RemoteList.DoubleTapped += async (_, _) =>
        {
            if (viewModel.SelectedRemote is { IsDirectory: true } item)
                await viewModel.OpenAsync(item);
        };
        RemoteList.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter && viewModel.SelectedRemote is { IsDirectory: true } item)
                await viewModel.OpenAsync(item);
        };
        RemotePathBox.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter && viewModel.IsConnected)
            {
                e.Handled = true;
                await viewModel.NavigateAsync(viewModel.RemotePath);
            }
        };
        CloseButton.Click += (_, _) => Close();
        Closed += (_, _) => viewModel.Dispose();
        Opened += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(viewModel.Host))
                viewModel.ConnectCommand.Execute(Unit.Default).Subscribe();
        };
    }

    /// <summary>
    /// Opens the SFTP browser as a non-modal window owned by <paramref name="owner"/>. When
    /// <paramref name="prefill"/> names a host, the connection fields are filled from it and the
    /// window connects immediately (asking for a missing username, password or host key decision).
    /// </summary>
    public static SshFileTransferDialog ShowFor(Window owner, ConnectionParameters? prefill = null)
    {
        var dialog = new SshFileTransferDialog(CreateViewModel(prefill));
        dialog.Show(owner);
        return dialog;
    }

    private static SshFileTransferViewModel CreateViewModel(ConnectionParameters? prefill) =>
        new(AppServices.GetRequired<IHostKeyVerifier>(), AppServices.GetRequired<ISshUserPrompt>(), prefill);
}
