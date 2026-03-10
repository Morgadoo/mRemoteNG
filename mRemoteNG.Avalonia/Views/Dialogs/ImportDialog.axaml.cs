using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace mRemoteNG.Avalonia.Views.Dialogs;

public partial class ImportDialog : Window
{
    public ImportDialog()
    {
        InitializeComponent();
        CancelButton.Click += (_, _) => Close(false);
        ImportButton.Click += (_, _) => Close(true);
        BrowseButton.Click += async (_, _) =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select connections file",
                AllowMultiple = false,
                FileTypeFilter = [
                    new FilePickerFileType("mRemoteNG XML") { Patterns = ["*.xml"] },
                    new FilePickerFileType("mRemoteNG CSV") { Patterns = ["*.csv"] },
                    new FilePickerFileType("All files") { Patterns = ["*.*"] },
                ],
            });
            if (files.Count > 0)
                FilePathBox.Text = files[0].Path.LocalPath;
        };
    }
}
