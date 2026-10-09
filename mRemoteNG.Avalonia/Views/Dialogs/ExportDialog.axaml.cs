using Avalonia.Controls;
using Avalonia.Platform.Storage;
using mRemoteNG.Core.Config.Export;
using mRemoteNG.Core.Localization;
using mRemoteNG.Core.Security;

namespace mRemoteNG.Avalonia.Views.Dialogs;

/// <summary>What the user chose in the <see cref="ExportDialog"/>.</summary>
/// <param name="SelectedFolderOnly">Export only the selected folder instead of the whole tree.</param>
/// <param name="Password">XML master password; null for an unprotected file.</param>
public sealed record ExportRequest(
    ExportFormat Format,
    bool SelectedFolderOnly,
    SaveFilter SaveFilter,
    string? Password,
    string FilePath);

/// <summary>
/// Lets the user choose export format, scope, credentials and destination.
/// <see cref="Window.ShowDialog{TResult}(Window)"/> returns an <see cref="ExportRequest"/>, or null when cancelled.
/// </summary>
public partial class ExportDialog : Window
{
    public ExportDialog() : this(null)
    {
    }

    /// <param name="selectedFolderName">The folder selected in the tree; null disables the "selected folder" scope.</param>
    public ExportDialog(string? selectedFolderName)
    {
        InitializeComponent();

        if (selectedFolderName is null)
        {
            SelectedRadio.IsEnabled = false;
        }
        else
        {
            SelectedRadio.Content = Localizer.Format("ExportSelectedFolderFormat", selectedFolderName);
            SelectedRadio.IsChecked = true;
        }

        XmlRadio.IsCheckedChanged += (_, _) => UpdateFormat();
        PasswordCheck.IsCheckedChanged += (_, _) => UpdateFormat();
        CancelButton.Click += (_, _) => Close(null);
        ExportButton.Click += (_, _) => OnExport();
        BrowseButton.Click += async (_, _) => await BrowseAsync();

        UpdateFormat();
    }

    private ExportFormat Format => CsvRadio.IsChecked == true ? ExportFormat.Csv : ExportFormat.Xml;

    private string Extension => Format == ExportFormat.Csv ? ".csv" : ".xml";

    private void UpdateFormat()
    {
        var isXml = Format == ExportFormat.Xml;
        PasswordCard.IsVisible = isXml;
        CsvPasswordWarning.IsVisible = !isXml && PasswordCheck.IsChecked == true;

        // Keep a typed path's extension in step with the format.
        var path = FilePathBox.Text;
        if (!string.IsNullOrWhiteSpace(path))
        {
            var current = Path.GetExtension(path);
            if (current.Equals(".xml", StringComparison.OrdinalIgnoreCase) || current.Equals(".csv", StringComparison.OrdinalIgnoreCase))
                FilePathBox.Text = Path.ChangeExtension(path, Extension);
        }
    }

    private async Task BrowseAsync()
    {
        var isCsv = Format == ExportFormat.Csv;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Localizer.Get("ExportConnectionsToFile"),
            SuggestedFileName = "connections" + Extension,
            DefaultExtension = Extension.TrimStart('.'),
            FileTypeChoices =
            [
                isCsv
                    ? new FilePickerFileType("mRemoteNG CSV") { Patterns = ["*.csv"] }
                    : new FilePickerFileType("mRemoteNG XML") { Patterns = ["*.xml"] },
            ],
        });
        if (file is not null)
            FilePathBox.Text = file.Path.LocalPath;
    }

    private void OnExport()
    {
        var path = FilePathBox.Text?.Trim() ?? "";
        string? error = null;
        string? password = null;

        if (path.Length == 0)
        {
            error = Localizer.Get("ExportChooseFile");
        }
        else if (Path.GetDirectoryName(Path.GetFullPath(path)) is not { } directory || !Directory.Exists(directory))
        {
            error = Localizer.Get("ExportFolderMissing");
        }
        else if (Format == ExportFormat.Xml && !string.IsNullOrEmpty(FilePasswordBox.Text))
        {
            if (FilePasswordBox.Text != FilePasswordConfirmBox.Text)
                error = Localizer.Get("PasswordsDoNotMatch");
            else
                password = FilePasswordBox.Text;
        }

        if (error is not null)
        {
            ErrorText.Text = error;
            ErrorPanel.IsVisible = true;
            return;
        }

        if (!Path.HasExtension(path))
            path += Extension;

        var filter = new SaveFilter
        {
            SaveUsername = UsernameCheck.IsChecked == true,
            SavePassword = PasswordCheck.IsChecked == true,
            SaveDomain = DomainCheck.IsChecked == true,
            SaveInheritance = InheritanceCheck.IsChecked == true,
            SaveCredentialId = true,
        };

        Close(new ExportRequest(Format, SelectedRadio.IsChecked == true, filter, password, path));
    }
}
