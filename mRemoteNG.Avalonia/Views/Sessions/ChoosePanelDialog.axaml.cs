using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace mRemoteNG.Avalonia.Views.Sessions;

/// <summary>
/// Legacy frmChoosePanel: pick an existing panel or name a new one.
/// <see cref="Window.ShowDialog{TResult}(Window)"/> returns the panel name, or null when cancelled.
/// </summary>
/// <remarks>
/// Opens with the keyboard in the new-panel box (typing names a new panel; Up/Down pick an existing one; Enter
/// confirms). The window activates itself when shown so the first keystroke is not lost to the window under it.
/// </remarks>
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
        NewPanelBox.AddHandler(KeyDownEvent, OnNewPanelBoxKeyDown, RoutingStrategies.Tunnel);
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
        Opened += (_, _) => FocusInput();
        UpdateOk();
    }

    /// <summary>The new panel name when one is typed, else the selected panel.</summary>
    public string? Result =>
        !string.IsNullOrWhiteSpace(NewPanelBox.Text) ? NewPanelBox.Text.Trim() : PanelList.SelectedItem as string;

    /// <summary>The text box that has the keyboard when the dialog opens (for tests).</summary>
    public TextBox InputBox => NewPanelBox;

    private void FocusInput()
    {
        NewPanelBox.Focus();
        // The window manager may not hand the keyboard to a new window right away (or something embedded in the
        // window below holds it): ask for activation, then focus again once the window is active.
        Activate();
        Dispatcher.UIThread.Post(() => NewPanelBox.Focus(), DispatcherPriority.Input);
    }

    private void OnNewPanelBoxKeyDown(object? sender, KeyEventArgs e)
    {
        // Up/Down choose an existing panel while the box is empty, without leaving the box.
        if (e.Key is not (Key.Up or Key.Down) || !string.IsNullOrEmpty(NewPanelBox.Text) || PanelList.ItemCount == 0)
            return;
        var index = Math.Clamp(PanelList.SelectedIndex + (e.Key == Key.Down ? 1 : -1), 0, PanelList.ItemCount - 1);
        PanelList.SelectedIndex = index;
        PanelList.ScrollIntoView(index);
        e.Handled = true;
    }

    private void UpdateOk() => OkButton.IsEnabled = Result is not null;
}
