using Avalonia.Controls;

namespace mRemoteNG.Avalonia.Views.Sessions;

/// <summary>
/// Legacy frmChoosePanel: pick an existing panel or name a new one.
/// <see cref="Window.ShowDialog{TResult}(Window)"/> returns the panel name, or null when cancelled.
/// </summary>
public partial class ChoosePanelDialog : Window
{
    public ChoosePanelDialog() : this([], "General")
    {
    }

    public ChoosePanelDialog(IReadOnlyList<string> panels, string suggestion)
    {
        InitializeComponent();
        var names = panels.ToList();
        if (!names.Contains(suggestion, StringComparer.OrdinalIgnoreCase))
            names.Add(suggestion);
        PanelList.ItemsSource = names;
        PanelList.SelectedItem = names.First(n => string.Equals(n, suggestion, StringComparison.OrdinalIgnoreCase));

        NewPanelBox.TextChanged += (_, _) => UpdateOk();
        PanelList.SelectionChanged += (_, _) => UpdateOk();
        PanelList.DoubleTapped += (_, _) =>
        {
            if (PanelList.SelectedItem is string name)
                Close(name);
        };
        CancelButton.Click += (_, _) => Close(null);
        OkButton.Click += (_, _) =>
        {
            if (Result is { } name)
                Close(name);
        };
        Opened += (_, _) => PanelList.Focus();
        UpdateOk();
    }

    /// <summary>The new panel name when one is typed, else the selected panel.</summary>
    public string? Result =>
        !string.IsNullOrWhiteSpace(NewPanelBox.Text) ? NewPanelBox.Text.Trim() : PanelList.SelectedItem as string;

    private void UpdateOk() => OkButton.IsEnabled = Result is not null;
}
