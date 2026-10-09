using Avalonia.Controls;
using Avalonia.Platform.Storage;
using mRemoteNG.Core.Config.Import;

namespace mRemoteNG.Avalonia.Views.Dialogs;

/// <summary>What the user chose in the <see cref="ImportDialog"/>.</summary>
/// <param name="Source">File or folder to import; empty means the source's default location.</param>
/// <param name="IntoSelectedFolder">Import into the selected folder instead of the root.</param>
public sealed record ImportRequest(ImportSourceType Type, string Source, bool IntoSelectedFolder);

/// <summary>
/// Lets the user pick an import source and target folder.
/// <see cref="Window.ShowDialog{TResult}(Window)"/> returns an <see cref="ImportRequest"/>, or null when cancelled.
/// </summary>
public partial class ImportDialog : Window
{
    private readonly bool _hasSelectedFolder;

    public ImportDialog() : this(null)
    {
    }

    /// <param name="selectedFolderName">The folder selected in the tree, offered as import target; null when none.</param>
    public ImportDialog(string? selectedFolderName)
    {
        InitializeComponent();

        SourceTypeBox.ItemsSource = ImportSourceDescriptor.All.Select(d => d.DisplayName).ToList();
        SourceTypeBox.SelectedIndex = 0;
        SourceTypeBox.SelectionChanged += (_, _) => OnSourceTypeChanged();

        _hasSelectedFolder = selectedFolderName is not null;
        var targets = new List<string> { "Root (top level)" };
        if (_hasSelectedFolder)
            targets.Add($"Selected folder: {selectedFolderName}");
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
        ErrorText.IsVisible = false;
        FilePathBox.Text = ConnectionImportService.GetDefaultSource(source.Type) ?? "";

        (FilePathBox.Watermark, SourceHintText.Text) = source.Type switch
        {
            ImportSourceType.PuttySessions when OperatingSystem.IsWindows() =>
                ("PuTTY sessions folder (optional)",
                 "Leave empty to read the sessions PuTTY saved in the Windows registry."),
            ImportSourceType.PuttySessions =>
                ("PuTTY sessions folder",
                 "PuTTY stores sessions in ~/.putty/sessions (one file per session)."),
            ImportSourceType.OpenSshConfig =>
                ("OpenSSH config file",
                 "Host entries become SSH connections; wildcard patterns are skipped."),
            ImportSourceType.MRemoteNGXml =>
                ("Select a file to import...",
                 "You will be asked for the password if the file is protected."),
            ImportSourceType.RemoteDesktopConnectionManager =>
                ("Select a file to import...",
                 "Passwords encrypted by RDCMan (Windows DPAPI) cannot be imported."),
            ImportSourceType.MRemoteNGCsv or ImportSourceType.RemoteDesktopManager =>
                ("Select a file to import...",
                 "Passwords in CSV files are stored in clear text."),
            _ => ("Select a file to import...", ""),
        };
        SourceHintText.IsVisible = !string.IsNullOrEmpty(SourceHintText.Text);
    }

    private async Task BrowseAsync()
    {
        var source = SelectedSource;
        if (source.SourceIsFolder)
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select the PuTTY sessions folder",
                AllowMultiple = false,
            });
            if (folders.Count > 0)
                FilePathBox.Text = folders[0].Path.LocalPath;
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = $"Import from {source.DisplayName}",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(source.DisplayName) { Patterns = source.FilePatterns.ToList() },
                new FilePickerFileType("All files") { Patterns = ["*"] },
            ],
        });
        if (files.Count > 0)
            FilePathBox.Text = files[0].Path.LocalPath;
    }

    private void OnImport()
    {
        var source = SelectedSource;
        var path = FilePathBox.Text?.Trim() ?? "";

        var error = (source.Type, path.Length == 0) switch
        {
            (ImportSourceType.PuttySessions, true) when !OperatingSystem.IsWindows() =>
                "No PuTTY sessions folder was found. Select the folder that holds the session files.",
            (ImportSourceType.PuttySessions, _) or (ImportSourceType.OpenSshConfig, true) => null,
            (_, true) => "Select a file to import.",
            _ => null,
        };
        if (error is null && path.Length > 0)
        {
            var exists = source.SourceIsFolder ? Directory.Exists(path) : File.Exists(path);
            if (!exists)
                error = $"\"{path}\" does not exist.";
        }

        if (error is not null)
        {
            ErrorText.Text = error;
            ErrorText.IsVisible = true;
            return;
        }

        Close(new ImportRequest(source.Type, path, _hasSelectedFolder && TargetFolderBox.SelectedIndex == 1));
    }
}
