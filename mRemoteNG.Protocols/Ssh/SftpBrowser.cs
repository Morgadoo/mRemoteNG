using System.Collections.ObjectModel;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using Renci.SshNet;
using Renci.SshNet.Sftp;

namespace mRemoteNG.Protocols.Ssh;

/// <summary>
/// ViewModel for the SFTP file browser panel.
/// Displayed alongside a connected SSH session.
/// </summary>
public sealed class SftpBrowserViewModel : ReactiveObject
{
    private readonly ILogger<SftpBrowserViewModel> _logger;
    private SftpClient? _sftpClient;
    private string _currentPath = "/";
    private SftpEntryViewModel? _selectedEntry;
    private bool _isBusy;
    private string _statusText = "Not connected";

    public ObservableCollection<SftpEntryViewModel> Entries { get; } = [];

    public string CurrentPath
    {
        get => _currentPath;
        private set => this.RaiseAndSetIfChanged(ref _currentPath, value);
    }

    public SftpEntryViewModel? SelectedEntry
    {
        get => _selectedEntry;
        set => this.RaiseAndSetIfChanged(ref _selectedEntry, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => this.RaiseAndSetIfChanged(ref _isBusy, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => this.RaiseAndSetIfChanged(ref _statusText, value);
    }

    public ReactiveUI.ReactiveCommand<string, System.Reactive.Unit> NavigateCommand { get; }
    public ReactiveUI.ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> NavigateUpCommand { get; }
    public ReactiveUI.ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> RefreshCommand { get; }
    public ReactiveUI.ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> DownloadSelectedCommand { get; }
    public ReactiveUI.ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> UploadCommand { get; }
    public ReactiveUI.ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> DeleteSelectedCommand { get; }

    public SftpBrowserViewModel(ILogger<SftpBrowserViewModel> logger)
    {
        _logger = logger;
        var notBusy = this.WhenAnyValue(x => x.IsBusy, busy => !busy);

        NavigateCommand = ReactiveUI.ReactiveCommand.CreateFromTask<string>(NavigateToAsync, notBusy);
        NavigateUpCommand = ReactiveUI.ReactiveCommand.CreateFromTask(NavigateUpAsync, notBusy);
        RefreshCommand = ReactiveUI.ReactiveCommand.CreateFromTask(RefreshAsync, notBusy);
        DownloadSelectedCommand = ReactiveUI.ReactiveCommand.CreateFromTask(DownloadSelectedAsync, notBusy);
        UploadCommand = ReactiveUI.ReactiveCommand.CreateFromTask(UploadAsync, notBusy);
        DeleteSelectedCommand = ReactiveUI.ReactiveCommand.CreateFromTask(DeleteSelectedAsync, notBusy);
    }

    public async Task ConnectAsync(ConnectionInfo connectionInfo, CancellationToken ct)
    {
        IsBusy = true;
        StatusText = "Connecting SFTP…";
        try
        {
            _sftpClient = new SftpClient(connectionInfo);
            await Task.Run(() => _sftpClient.Connect(), ct);
            StatusText = "Connected";
            await NavigateToAsync("/", ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SFTP connect failed");
            StatusText = $"SFTP error: {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    private async Task NavigateToAsync(string path, CancellationToken ct = default)
    {
        if (_sftpClient?.IsConnected != true) return;

        IsBusy = true;
        StatusText = $"Loading {path}…";
        try
        {
            var items = await Task.Run(
                () => _sftpClient.ListDirectory(path).OrderBy(f => !f.IsDirectory).ThenBy(f => f.Name),
                ct);

            Entries.Clear();
            foreach (var item in items)
            {
                if (item.Name is "." or "..") continue;
                Entries.Add(new SftpEntryViewModel(item));
            }

            CurrentPath = path;
            StatusText = $"{Entries.Count} items";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SFTP list failed for {Path}", path);
            StatusText = $"Error: {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    private Task NavigateUpAsync(CancellationToken ct = default)
    {
        var parent = Path.GetDirectoryName(CurrentPath.TrimEnd('/')) ?? "/";
        return NavigateToAsync(parent == string.Empty ? "/" : parent, ct);
    }

    private Task RefreshAsync(CancellationToken ct = default) =>
        NavigateToAsync(CurrentPath, ct);

    private async Task DownloadSelectedAsync(CancellationToken ct = default)
    {
        if (SelectedEntry?.IsDirectory == true)
        {
            await NavigateToAsync(SelectedEntry.FullPath, ct);
            return;
        }
        if (SelectedEntry is null || _sftpClient?.IsConnected != true) return;

        // Phase 4: show file picker dialog
        var localPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads",
            SelectedEntry.Name);

        IsBusy = true;
        StatusText = $"Downloading {SelectedEntry.Name}…";
        try
        {
            using var stream = File.Create(localPath);
            await Task.Run(() => _sftpClient.DownloadFile(SelectedEntry.FullPath, stream), ct);
            StatusText = $"Downloaded → {localPath}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SFTP download failed");
            StatusText = $"Download error: {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    private async Task UploadAsync(CancellationToken ct = default)
    {
        // Phase 4: wire to Avalonia StorageProvider file picker
        StatusText = "Upload: use drag-and-drop or file picker (Phase 4)";
        await Task.CompletedTask;
    }

    private async Task DeleteSelectedAsync(CancellationToken ct = default)
    {
        if (SelectedEntry is null || _sftpClient?.IsConnected != true) return;
        IsBusy = true;
        try
        {
            string remotePath = SelectedEntry.FullPath;
            await Task.Run(() =>
            {
                if (SelectedEntry.IsDirectory)
                    _sftpClient.DeleteDirectory(remotePath);
                else
                    _sftpClient.DeleteFile(remotePath);
            }, ct);

            Entries.Remove(SelectedEntry);
            StatusText = $"Deleted {SelectedEntry.Name}";
        }
        catch (Exception ex)
        {
            StatusText = $"Delete error: {ex.Message}";
        }
        finally { IsBusy = false; }
    }
}

public sealed class SftpEntryViewModel
{
    private readonly ISftpFile _file;

    public SftpEntryViewModel(ISftpFile file) => _file = file;

    public string Name => _file.Name;
    public string FullPath => _file.FullName;
    public bool IsDirectory => _file.IsDirectory;
    public long Size => _file.Length;
    public string SizeText => IsDirectory ? "<DIR>" : FormatSize(Size);
    public DateTime LastModified => _file.LastWriteTime;
    public string Icon => IsDirectory ? "📁" : "📄";

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024 * 1024)} MB",
        _ => $"{bytes / (1024L * 1024 * 1024)} GB"
    };
}
