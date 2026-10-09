using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using mRemoteNG.Avalonia.ViewModels;
using mRemoteNG.Core.Connection;

namespace mRemoteNG.Avalonia.Views.Dialogs;

/// <summary>
/// Connection / folder / default-connection property editor. <see cref="Window.ShowDialog{TResult}(Window)"/>
/// with <c>bool</c> returns true when the user pressed OK; the caller then calls
/// <see cref="ConnectionDialogViewModel.Apply"/>.
/// </summary>
public partial class ConnectionDialog : Window
{
    /// <summary>Designer/XAML loader constructor: edits a throw-away connection.</summary>
    public ConnectionDialog()
        : this(new ConnectionDialogViewModel(ConnectionDefaults.ApplyNewConnectionDefaults(new ConnectionInfo()), null, isNew: true))
    {
    }

    public ConnectionDialog(ConnectionDialogViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.CloseRequested += ok => Close(ok);

        // Suggestion boxes list their values as soon as they get focus.
        AddHandler(GotFocusEvent, OnGotFocus, RoutingStrategies.Bubble);
        Opened += (_, _) => Dispatcher.UIThread.Post(FocusFirstEditor, DispatcherPriority.Loaded);
    }

    public ConnectionDialogViewModel ViewModel => (ConnectionDialogViewModel)DataContext!;

    private void FocusFirstEditor()
    {
        var first = Tabs.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => t.IsEffectivelyVisible && t.IsEffectivelyEnabled);
        if (first is null) return;
        first.Focus();
        first.SelectAll();
    }

    private static void OnGotFocus(object? sender, GotFocusEventArgs e)
    {
        if (e.Source is Control control
            && control.FindAncestorOfType<AutoCompleteBox>(includeSelf: true) is { IsDropDownOpen: false } box
            && box.ItemsSource is IReadOnlyCollection<string> { Count: > 0 }
            && e.NavigationMethod != NavigationMethod.Unspecified)
        {
            box.IsDropDownOpen = true;
        }
    }
}
