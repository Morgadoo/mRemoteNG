using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Material.Icons;
using mRemoteNG.Core.Config.Import;
using mRemoteNG.Core.Config.Import.ActiveDirectory;
using mRemoteNG.Core.Localization;

namespace mRemoteNG.Avalonia.Views.Dialogs;

/// <summary>What the user chose in the <see cref="ImportDialog"/>.</summary>
/// <param name="Source">File or folder to import; empty means the source's default location.</param>
/// <param name="IntoSelectedFolder">Import into the selected folder instead of the root.</param>
public sealed record ImportRequest(ImportSourceType Type, string Source, bool IntoSelectedFolder)
{
    /// <summary>
    /// Password that goes with <see cref="Source"/> when the source is not a file: the LDAP bind password of an
    /// Active Directory import. Pass it to <c>ConnectionImportService.Import</c> as the password.
    /// </summary>
    public string? Password { get; init; }
}

/// <summary>An import source shown as a selectable card in the <see cref="ImportDialog"/>.</summary>
public sealed record ImportSourceCard(ImportSourceDescriptor Descriptor, MaterialIconKind Icon)
{
    public static ImportSourceCard For(ImportSourceDescriptor descriptor) => new(descriptor, descriptor.Type switch
    {
        ImportSourceType.MRemoteNGXml => MaterialIconKind.FileCodeOutline,
        ImportSourceType.MRemoteNGCsv => MaterialIconKind.FileDelimitedOutline,
        ImportSourceType.PuttySessions => MaterialIconKind.ConsoleNetworkOutline,
        ImportSourceType.OpenSshConfig => MaterialIconKind.ConsoleLine,
        ImportSourceType.RemoteDesktopConnectionManager => MaterialIconKind.MonitorMultiple,
        ImportSourceType.RemoteDesktopConnectionFile => MaterialIconKind.RemoteDesktop,
        ImportSourceType.RemoteDesktopManager => MaterialIconKind.FileTableOutline,
        ImportSourceType.SecureCrt => MaterialIconKind.ShieldLockOutline,
        ImportSourceType.ActiveDirectory => MaterialIconKind.Domain,
        _ => MaterialIconKind.FileImportOutline,
    });
}

/// <summary>
/// Lets the user pick an import source and target folder.
/// <see cref="Window.ShowDialog{TResult}(Window)"/> returns an <see cref="ImportRequest"/>, or null when cancelled.
/// </summary>
public partial class ImportDialog : Window
{
    private readonly bool _hasSelectedFolder;
    private ActiveDirectoryImportRequest? _directoryRequest;

    public ImportDialog() : this(null)
    {
    }

    /// <param name="selectedFolderName">The folder selected in the tree, offered as import target; null when none.</param>
    public ImportDialog(string? selectedFolderName)
    {
        InitializeComponent();

        SourceTypeBox.ItemsSource = ImportSourceDescriptor.All.Select(ImportSourceCard.For).ToList();
        SourceTypeBox.SelectedIndex = 0;
        SourceTypeBox.SelectionChanged += (_, _) => OnSourceTypeChanged();

        _hasSelectedFolder = selectedFolderName is not null;
        var targets = new List<string> { Localizer.Get("ImportTargetRoot") };
        if (_hasSelectedFolder)
            targets.Add(Localizer.Format("ImportTargetSelectedFolderFormat", selectedFolderName));
        TargetFolderBox.ItemsSource = targets;
        TargetFolderBox.SelectedIndex = _hasSelectedFolder ? 1 : 0;

        CancelButton.Click += (_, _) => Close(null);
        ImportButton.Click += (_, _) => OnImport();
        BrowseButton.Click += async (_, _) => await BrowseAsync();

        OnSourceTypeChanged();
    }

    private ImportSourceDescriptor SelectedSource =>
        ImportSourceDescriptor.All[Math.Max(0, SourceTypeBox.SelectedIndex)];

    private void OnSourceTypeChanged()
    {
        var source = SelectedSource;
        ErrorPanel.IsVisible = false;
        FileRow.Header = Localizer.Get(source.SourceIsDirectory ? "DirectoryToImport" : source.SourceIsFolder ? "FolderToImport" : "FileToImport");
        FilePathBox.Text = source.SourceIsDirectory
            ? _directoryRequest?.ToUrl() ?? ""
            : ConnectionImportService.GetDefaultSource(source.Type) ?? "";
        FilePathBox.IsReadOnly = source.SourceIsDirectory;
        BrowseButton.Content = source.SourceIsDirectory ? Localizer.Get("BrowseDirectory") + "..." : Localizer.Get("_Browse");

        (FilePathBox.Watermark, SourceHintText.Text) = source.Type switch
        {
            ImportSourceType.PuttySessions when OperatingSystem.IsWindows() =>
                (Localizer.Get("PuttySessionsFolderOptional"), Localizer.Get("ImportHintPuttyRegistry")),
            ImportSourceType.PuttySessions =>
                (Localizer.Get("PuttySessionsFolder"), Localizer.Get("ImportHintPuttyFolder")),
            ImportSourceType.OpenSshConfig =>
                (Localizer.Get("OpenSshConfigFile"), Localizer.Get("ImportHintOpenSsh")),
            ImportSourceType.MRemoteNGXml =>
                (SelectFileWatermark, Localizer.Get("ImportHintXml")),
            ImportSourceType.RemoteDesktopConnectionManager =>
                (SelectFileWatermark, Localizer.Get("ImportHintRdcMan")),
            ImportSourceType.ActiveDirectory =>
                (Localizer.Get("ImportBrowseDirectoryWatermark"), Localizer.Get("ImportHintActiveDirectory")),
            ImportSourceType.MRemoteNGCsv or ImportSourceType.RemoteDesktopManager =>
                (SelectFileWatermark, Localizer.Get("ImportHintCsv")),
            _ => (SelectFileWatermark, ""),
        };
        SourceHintText.IsVisible = !string.IsNullOrEmpty(SourceHintText.Text);
    }

    private static string SelectFileWatermark => Localizer.Get("SelectAFileToImport") + "...";

    private async Task BrowseAsync()
    {
        var source = SelectedSource;
        if (source.SourceIsDirectory)
        {
            await BrowseDirectoryAsync();
            return;
        }
        if (source.SourceIsFolder)
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = Localizer.Get("SelectPuttySessionsFolder"),
                AllowMultiple = false,
            });
            if (folders.Count > 0)
                FilePathBox.Text = folders[0].Path.LocalPath;
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Localizer.Format("ImportFromFormat", source.DisplayName),
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(source.DisplayName) { Patterns = source.FilePatterns.ToList() },
                new FilePickerFileType(Localizer.Get("FilterAll", "All files")) { Patterns = ["*"] },
            ],
        });
        if (files.Count > 0)
            FilePathBox.Text = files[0].Path.LocalPath;
    }

    /// <summary>Opens the Active Directory browser; returns false when it was cancelled.</summary>
    private async Task<bool> BrowseDirectoryAsync()
    {
        var request = await new ActiveDirectoryImportDialog().ShowDialog<ActiveDirectoryImportRequest?>(this);
        if (request is null) return false;
        _directoryRequest = request;
        FilePathBox.Text = request.ToUrl();
        ErrorPanel.IsVisible = false;
        return true;
    }

    private async void OnImport()
    {
        var source = SelectedSource;
        if (source.SourceIsDirectory)
        {
            if (_directoryRequest is null && !await BrowseDirectoryAsync())
                return;
            Close(new ImportRequest(source.Type, _directoryRequest!.ToUrl(), _hasSelectedFolder && TargetFolderBox.SelectedIndex == 1)
            {
                Password = _directoryRequest.Server.Password,
            });
            return;
        }

        var path = FilePathBox.Text?.Trim() ?? "";

        var error = (source.Type, path.Length == 0) switch
        {
            (ImportSourceType.PuttySessions, true) when !OperatingSystem.IsWindows() =>
                Localizer.Get("ImportNoPuttyFolder"),
            (ImportSourceType.PuttySessions, _) or (ImportSourceType.OpenSshConfig, true) => null,
            (_, true) => Localizer.Get("SelectAFileToImport") + ".",
            _ => null,
        };
        if (error is null && path.Length > 0)
        {
            var exists = source.SourceIsFolder ? Directory.Exists(path) : File.Exists(path);
            if (!exists)
                error = Localizer.Format("PathDoesNotExistFormat", path);
        }

        if (error is not null)
        {
            ErrorText.Text = error;
            ErrorPanel.IsVisible = true;
            return;
        }

        Close(new ImportRequest(source.Type, path, _hasSelectedFolder && TargetFolderBox.SelectedIndex == 1));
    }
}
