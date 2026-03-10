using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using ReactiveUI;
using System.Reactive;

namespace mRemoteNG.Avalonia.ViewModels;

public class FileEntry : ReactiveObject
{
    public string Name { get; init; } = string.Empty;
    public string Size { get; init; } = string.Empty;
    public string Modified { get; init; } = string.Empty;
    public bool IsDirectory { get; init; }
}

public class SshFileTransferViewModel : ReactiveObject
{
    private string _localPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private string _remotePath = "/home";
    private double _transferProgress;
    private string _transferStatus = "Ready";
    private FileEntry? _selectedLocal, _selectedRemote;

    public string LocalPath { get => _localPath; set { this.RaiseAndSetIfChanged(ref _localPath, value); LoadLocalFiles(); } }
    public string RemotePath { get => _remotePath; set { this.RaiseAndSetIfChanged(ref _remotePath, value); } }
    public double TransferProgress { get => _transferProgress; set => this.RaiseAndSetIfChanged(ref _transferProgress, value); }
    public string TransferStatus { get => _transferStatus; set => this.RaiseAndSetIfChanged(ref _transferStatus, value); }
    public FileEntry? SelectedLocal { get => _selectedLocal; set => this.RaiseAndSetIfChanged(ref _selectedLocal, value); }
    public FileEntry? SelectedRemote { get => _selectedRemote; set => this.RaiseAndSetIfChanged(ref _selectedRemote, value); }

    public ObservableCollection<FileEntry> LocalFiles { get; } = new();
    public ObservableCollection<FileEntry> RemoteFiles { get; } = new();

    public ReactiveCommand<Unit, Unit> UploadCommand { get; }
    public ReactiveCommand<Unit, Unit> DownloadCommand { get; }
    public ReactiveCommand<Unit, Unit> LocalUpCommand { get; }
    public ReactiveCommand<Unit, Unit> RemoteUpCommand { get; }

    public SshFileTransferViewModel()
    {
        UploadCommand = ReactiveCommand.CreateFromTask(UploadAsync,
            this.WhenAnyValue(x => x.SelectedLocal, s => s != null && !s.IsDirectory));
        DownloadCommand = ReactiveCommand.CreateFromTask(DownloadAsync,
            this.WhenAnyValue(x => x.SelectedRemote, s => s != null && !s.IsDirectory));
        LocalUpCommand = ReactiveCommand.Create(() => {
            var parent = Directory.GetParent(LocalPath)?.FullName;
            if (parent != null) LocalPath = parent;
        });
        RemoteUpCommand = ReactiveCommand.Create(() => {
            var idx = RemotePath.LastIndexOf('/');
            RemotePath = idx > 0 ? RemotePath[..idx] : "/";
        });
        LoadLocalFiles();
    }

    private void LoadLocalFiles()
    {
        LocalFiles.Clear();
        try
        {
            foreach (var dir in Directory.GetDirectories(LocalPath))
                LocalFiles.Add(new FileEntry { Name = Path.GetFileName(dir), Size = "<DIR>", Modified = Directory.GetLastWriteTime(dir).ToString("yyyy-MM-dd HH:mm"), IsDirectory = true });
            foreach (var file in Directory.GetFiles(LocalPath))
            {
                var fi = new FileInfo(file);
                LocalFiles.Add(new FileEntry { Name = fi.Name, Size = FormatSize(fi.Length), Modified = fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm") });
            }
        }
        catch { /* access denied etc */ }
    }

    private async Task UploadAsync()
    {
        if (SelectedLocal == null) return;
        TransferStatus = $"Uploading {SelectedLocal.Name}...";
        // SftpClient injected in Phase 5 via DI; stub progress animation
        for (var i = 0; i <= 100; i += 10) { TransferProgress = i; await Task.Delay(50); }
        TransferStatus = $"Uploaded {SelectedLocal.Name}";
        TransferProgress = 0;
    }

    private async Task DownloadAsync()
    {
        if (SelectedRemote == null) return;
        TransferStatus = $"Downloading {SelectedRemote.Name}...";
        for (var i = 0; i <= 100; i += 10) { TransferProgress = i; await Task.Delay(50); }
        TransferStatus = $"Downloaded {SelectedRemote.Name}";
        TransferProgress = 0;
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1048576 => $"{bytes / 1024} KB",
        < 1073741824 => $"{bytes / 1048576} MB",
        _ => $"{bytes / 1073741824} GB"
    };
}
