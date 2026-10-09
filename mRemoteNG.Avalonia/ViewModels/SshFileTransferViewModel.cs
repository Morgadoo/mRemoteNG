using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using mRemoteNG.Protocols.Abstractions;
using mRemoteNG.Protocols.Ssh;
using ReactiveUI;

namespace mRemoteNG.Avalonia.ViewModels;

/// <summary>A row in the remote file list.</summary>
public sealed class SftpFileItem
{
    public SftpFileItem(RemoteFileEntry entry) => Entry = entry;

    public RemoteFileEntry Entry { get; }
    public string Name => Entry.IsDirectory ? Entry.Name + "/" : Entry.Name;
    public bool IsDirectory => Entry.IsDirectory;
    public string Size => Entry.IsDirectory ? "<DIR>" : SshFileTransferViewModel.FormatSize(Entry.Length);
    public string Modified => Entry.LastWriteTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
}

/// <summary>
/// SFTP browser: connects with host key verification, lists and navigates remote directories,
/// uploads/downloads with progress, creates folders and deletes entries.
/// File pickers and confirmations are provided by the dialog through the interactions.
/// </summary>
public sealed class SshFileTransferViewModel : ReactiveObject, IDisposable
{
    private readonly SftpSession _session;
    private string _host = string.Empty;
    private int _port = 22;
    private string _username = string.Empty;
    private string _password = string.Empty;
    private string _privateKeyPath = string.Empty;
    private string _remotePath = "/";
    private string _status = "Not connected.";
    private bool _hasError;
    private bool _isConnected;
    private bool _isBusy;
    private bool _isTransferring;
    private double _transferProgress;
    private SftpFileItem? _selectedRemote;
    private CancellationTokenSource? _operationCts;

    public SshFileTransferViewModel(IHostKeyVerifier hostKeyVerifier, ISshUserPrompt userPrompt, ConnectionParameters? prefill = null)
    {
        _session = new SftpSession(hostKeyVerifier, userPrompt);

        if (prefill is not null)
        {
            _host = prefill.Hostname;
            _port = prefill.Port > 0 ? prefill.Port : 22;
            _username = prefill.Username ?? string.Empty;
            _password = prefill.Password ?? string.Empty;
            _privateKeyPath = prefill.PrivateKeyPath ?? string.Empty;
        }

        var canConnect = this.WhenAnyValue(x => x.Host, x => x.IsBusy, x => x.IsConnected,
            (host, busy, connected) => !string.IsNullOrWhiteSpace(host) && !busy && !connected);
        var connectedIdle = this.WhenAnyValue(x => x.IsConnected, x => x.IsBusy, (connected, busy) => connected && !busy);
        var fileSelected = this.WhenAnyValue(x => x.IsConnected, x => x.IsBusy, x => x.SelectedRemote,
            (connected, busy, item) => connected && !busy && item is not null);

        ConnectCommand = ReactiveCommand.CreateFromTask(ConnectAsync, canConnect);
        DisconnectCommand = ReactiveCommand.CreateFromTask(DisconnectAsync, connectedIdle);
        RefreshCommand = ReactiveCommand.CreateFromTask(() => NavigateAsync(RemotePath), connectedIdle);
        UpCommand = ReactiveCommand.CreateFromTask(() => NavigateAsync(SftpSession.ParentPath(RemotePath)), connectedIdle);
        CancelCommand = ReactiveCommand.Create(() => _operationCts?.Cancel(),
            this.WhenAnyValue(x => x.IsTransferring));

        UploadCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            var files = await PickFilesToUpload.Handle(Unit.Default);
            if (files.Count > 0)
                await UploadAsync(files);
        }, connectedIdle);
        DownloadCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            if (SelectedRemote is not { IsDirectory: false } item)
                return;
            var target = await PickDownloadTarget.Handle(item.Entry.Name);
            if (!string.IsNullOrEmpty(target))
                await DownloadAsync(item, target);
        }, this.WhenAnyValue(x => x.IsConnected, x => x.IsBusy, x => x.SelectedRemote,
            (connected, busy, item) => connected && !busy && item is { IsDirectory: false }));
        NewFolderCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            var name = await AskFolderName.Handle(RemotePath);
            if (!string.IsNullOrWhiteSpace(name))
                await CreateDirectoryAsync(name);
        }, connectedIdle);
        DeleteCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            if (SelectedRemote is not { } item)
                return;
            var what = item.IsDirectory ? $"the folder '{item.Entry.FullPath}' and everything in it" : $"'{item.Entry.FullPath}'";
            if (await ConfirmDelete.Handle($"Permanently delete {what}?"))
                await DeleteAsync(item);
        }, fileSelected);

        foreach (var command in new IHandleObservableErrors[] { UploadCommand, DownloadCommand, NewFolderCommand, DeleteCommand })
            command.ThrownExceptions.Subscribe(ex => SetStatus(ex.Message, error: true));
    }

    // ── Connection fields ──────────────────────────────────────────────────

    public string Host { get => _host; set => this.RaiseAndSetIfChanged(ref _host, value); }
    public int Port { get => _port; set => this.RaiseAndSetIfChanged(ref _port, value); }
    public string Username { get => _username; set => this.RaiseAndSetIfChanged(ref _username, value); }
    public string Password { get => _password; set => this.RaiseAndSetIfChanged(ref _password, value); }
    public string PrivateKeyPath { get => _privateKeyPath; set => this.RaiseAndSetIfChanged(ref _privateKeyPath, value); }

    // ── State ──────────────────────────────────────────────────────────────

    public string RemotePath { get => _remotePath; set => this.RaiseAndSetIfChanged(ref _remotePath, value); }
    public string Status { get => _status; private set => this.RaiseAndSetIfChanged(ref _status, value); }
    public bool HasError { get => _hasError; private set => this.RaiseAndSetIfChanged(ref _hasError, value); }
    public bool IsConnected { get => _isConnected; private set => this.RaiseAndSetIfChanged(ref _isConnected, value); }
    public bool IsBusy { get => _isBusy; private set => this.RaiseAndSetIfChanged(ref _isBusy, value); }
    public bool IsTransferring { get => _isTransferring; private set => this.RaiseAndSetIfChanged(ref _isTransferring, value); }
    public double TransferProgress { get => _transferProgress; private set => this.RaiseAndSetIfChanged(ref _transferProgress, value); }
    public SftpFileItem? SelectedRemote { get => _selectedRemote; set => this.RaiseAndSetIfChanged(ref _selectedRemote, value); }

    public ObservableCollection<SftpFileItem> RemoteFiles { get; } = [];

    /// <summary>Asks for local files to upload (empty when cancelled).</summary>
    public Interaction<Unit, IReadOnlyList<string>> PickFilesToUpload { get; } = new();

    /// <summary>Asks where to save a download, given the remote file name (null when cancelled).</summary>
    public Interaction<string, string?> PickDownloadTarget { get; } = new();

    /// <summary>Asks for a new folder name, given the current remote directory (null when cancelled).</summary>
    public Interaction<string, string?> AskFolderName { get; } = new();

    /// <summary>Asks to confirm a deletion, given the question to show.</summary>
    public Interaction<string, bool> ConfirmDelete { get; } = new();

    public ReactiveCommand<Unit, Unit> ConnectCommand { get; }
    public ReactiveCommand<Unit, Unit> DisconnectCommand { get; }
    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }
    public ReactiveCommand<Unit, Unit> UpCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }
    public ReactiveCommand<Unit, Unit> UploadCommand { get; }
    public ReactiveCommand<Unit, Unit> DownloadCommand { get; }
    public ReactiveCommand<Unit, Unit> NewFolderCommand { get; }
    public ReactiveCommand<Unit, Unit> DeleteCommand { get; }

    // ── Operations ─────────────────────────────────────────────────────────

    private async Task ConnectAsync()
    {
        var parameters = new ConnectionParameters
        {
            Hostname = Host.Trim(),
            Port = Port > 0 ? Port : 22,
            Protocol = ProtocolType.SshSftp,
            Username = string.IsNullOrWhiteSpace(Username) ? null : Username.Trim(),
            Password = string.IsNullOrEmpty(Password) ? null : Password,
            PrivateKeyPath = string.IsNullOrWhiteSpace(PrivateKeyPath) ? null : PrivateKeyPath.Trim(),
        };

        await RunAsync($"Connecting to {parameters.DisplayName}…", async ct =>
        {
            await _session.ConnectAsync(parameters, ct);
            IsConnected = true;
            await LoadDirectoryAsync(_session.HomeDirectory, ct);
            SetStatus($"Connected to {parameters.DisplayName}. {RemoteFiles.Count} items in {RemotePath}.");
        });
    }

    private async Task DisconnectAsync()
    {
        await _session.DisconnectAsync();
        IsConnected = false;
        RemoteFiles.Clear();
        SetStatus("Disconnected.");
    }

    /// <summary>Lists <paramref name="path"/> and makes it the current directory.</summary>
    public Task NavigateAsync(string path) =>
        RunAsync($"Loading {path}…", async ct =>
        {
            await LoadDirectoryAsync(path, ct);
            SetStatus($"{RemoteFiles.Count} items in {RemotePath}.");
        });

    /// <summary>Opens a directory entry (double-click).</summary>
    public Task OpenAsync(SftpFileItem item) =>
        item.IsDirectory ? NavigateAsync(item.Entry.FullPath) : Task.CompletedTask;

    /// <summary>Uploads local files into the current remote directory.</summary>
    public Task UploadAsync(IReadOnlyList<string> localPaths) =>
        RunAsync("Uploading…", async ct =>
        {
            for (int i = 0; i < localPaths.Count; i++)
            {
                var local = localPaths[i];
                var name = Path.GetFileName(local);
                var remote = SftpSession.CombinePath(RemotePath, name);
                var counter = localPaths.Count > 1 ? $" ({i + 1}/{localPaths.Count})" : string.Empty;
                SetStatus($"Uploading {name}{counter}…");
                await _session.UploadFileAsync(local, remote, CreateProgress(name, "Uploading"), ct);
            }
            await LoadDirectoryAsync(RemotePath, ct);
            SetStatus(localPaths.Count == 1
                ? $"Uploaded {Path.GetFileName(localPaths[0])} to {RemotePath}."
                : $"Uploaded {localPaths.Count} files to {RemotePath}.");
        }, transfer: true);

    /// <summary>Downloads the given remote file to <paramref name="localPath"/>.</summary>
    public Task DownloadAsync(SftpFileItem item, string localPath) =>
        RunAsync($"Downloading {item.Entry.Name}…", async ct =>
        {
            await _session.DownloadFileAsync(item.Entry.FullPath, localPath, CreateProgress(item.Entry.Name, "Downloading"), ct);
            SetStatus($"Downloaded {item.Entry.Name} to {localPath}.");
        }, transfer: true);

    public Task CreateDirectoryAsync(string name) =>
        RunAsync($"Creating {name}…", async ct =>
        {
            if (string.IsNullOrWhiteSpace(name) || name.Contains('/'))
                throw new ArgumentException($"'{name}' is not a valid folder name.");
            await _session.CreateDirectoryAsync(SftpSession.CombinePath(RemotePath, name.Trim()), ct);
            await LoadDirectoryAsync(RemotePath, ct);
            SetStatus($"Created {name.Trim()}.");
        });

    public Task DeleteAsync(SftpFileItem item) =>
        RunAsync($"Deleting {item.Entry.Name}…", async ct =>
        {
            await _session.DeleteAsync(item.Entry, ct);
            await LoadDirectoryAsync(RemotePath, ct);
            SetStatus($"Deleted {item.Entry.Name}.");
        });

    private async Task LoadDirectoryAsync(string path, CancellationToken ct)
    {
        var entries = await _session.ListDirectoryAsync(path, ct);
        RemoteFiles.Clear();
        foreach (var entry in entries)
            RemoteFiles.Add(new SftpFileItem(entry));
        RemotePath = path;
        SelectedRemote = null;
    }

    private IProgress<TransferProgress> CreateProgress(string name, string verb) =>
        new Progress<TransferProgress>(p =>
        {
            TransferProgress = p.Percent;
            Status = p.TotalBytes > 0
                ? $"{verb} {name}: {FormatSize(p.BytesTransferred)} of {FormatSize(p.TotalBytes)} ({p.Percent:0}%)"
                : $"{verb} {name}: {FormatSize(p.BytesTransferred)}";
        });

    /// <summary>Runs an operation with busy state and error reporting; errors are shown, not thrown.</summary>
    private async Task RunAsync(string startStatus, Func<CancellationToken, Task> operation, bool transfer = false)
    {
        if (IsBusy)
            return;

        _operationCts = new CancellationTokenSource();
        IsBusy = true;
        IsTransferring = transfer;
        TransferProgress = 0;
        SetStatus(startStatus);
        try
        {
            await operation(_operationCts.Token);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Cancelled.", error: true);
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, error: true);
        }
        finally
        {
            IsConnected = _session.IsConnected;
            IsBusy = false;
            IsTransferring = false;
            TransferProgress = 0;
            _operationCts.Dispose();
            _operationCts = null;
        }
    }

    private void SetStatus(string text, bool error = false)
    {
        Status = text;
        HasError = error;
    }

    internal static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
    };

    public void Dispose()
    {
        _operationCts?.Cancel();
        _session.Dispose();
    }
}
