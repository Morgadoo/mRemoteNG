using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace mRemoteNG.Avalonia.Views.Dialogs;

public partial class ExportDialog : Window
{
    public ExportDialog()
    {
        InitializeComponent();
        CancelButton.Click += (_, _) => Close(false);
        ExportButton.Click += (_, _) => Close(true);
        BrowseButton.Click += async (_, _) =>
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export connections to file",
                SuggestedFileName = "connections.xml",
                FileTypeChoices = [
                    new FilePickerFileType("mRemoteNG XML") { Patterns = ["*.xml"] },
                    new FilePickerFileType("mRemoteNG CSV") { Patterns = ["*.csv"] },
                ],
            });
            if (file is not null)
                FilePathBox.Text = file.Path.LocalPath;
        };
    }
}
