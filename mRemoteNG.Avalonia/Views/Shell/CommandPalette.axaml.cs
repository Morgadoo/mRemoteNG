using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using mRemoteNG.Avalonia.ViewModels;

namespace mRemoteNG.Avalonia.Views.Shell;

/// <summary>
/// The command palette overlay. Keys (handled while the search box has the focus): Up/Down/PageUp/PageDown move,
/// Enter connects or runs, Ctrl+Enter connects with options, Esc closes. Clicking a row runs it; clicking outside
/// closes. The focus returns to where it was when the palette closes.
/// </summary>
public partial class CommandPalette : UserControl
{
    private CommandPaletteViewModel? _viewModel;
    private IInputElement? _focusBeforeOpen;

    public CommandPalette()
    {
        InitializeComponent();
        QueryBox.AddHandler(KeyDownEvent, OnQueryKeyDown, RoutingStrategies.Tunnel);
        Backdrop.PointerPressed += (_, e) =>
        {
            _viewModel?.Close();
            e.Handled = true;
        };
        ResultsList.AddHandler(TappedEvent, OnResultTapped, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null)
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel = DataContext as CommandPaletteViewModel;
        if (_viewModel is not null)
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_viewModel is null)
            return;
        switch (e.PropertyName)
        {
            case nameof(CommandPaletteViewModel.IsOpen) when _viewModel.IsOpen:
                _focusBeforeOpen = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
                Dispatcher.UIThread.Post(() =>
                {
                    QueryBox.Focus();
                    QueryBox.SelectAll();
                }, DispatcherPriority.Loaded);
                break;
            case nameof(CommandPaletteViewModel.IsOpen):
                // Back to the terminal / tree the palette was opened from (unless a session or dialog took the focus).
                var previous = _focusBeforeOpen;
                _focusBeforeOpen = null;
                Dispatcher.UIThread.Post(() =>
                {
                    var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
                    if (previous is Visual visual && visual.GetVisualRoot() is not null && (focused is null || ReferenceEquals(focused, QueryBox)))
                        previous.Focus();
                }, DispatcherPriority.Background);
                break;
            case nameof(CommandPaletteViewModel.Selected) when _viewModel.Selected is { } selected:
                ResultsList.ScrollIntoView(selected);
                break;
        }
    }

    private async void OnQueryKeyDown(object? sender, KeyEventArgs e)
    {
        if (_viewModel is null)
            return;
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                _viewModel.Close();
                break;
            case Key.Down:
                e.Handled = true;
                _viewModel.MoveSelection(+1);
                break;
            case Key.Up:
                e.Handled = true;
                _viewModel.MoveSelection(-1);
                break;
            case Key.PageDown:
                e.Handled = true;
                _viewModel.MoveSelection(+8);
                break;
            case Key.PageUp:
                e.Handled = true;
                _viewModel.MoveSelection(-8);
                break;
            case Key.Enter:
                e.Handled = true;
                await _viewModel.AcceptAsync(withOptions: e.KeyModifiers.HasFlag(KeyModifiers.Control));
                break;
        }
    }

    private async void OnResultTapped(object? sender, TappedEventArgs e)
    {
        if (_viewModel is null || e.Source is not Visual source)
            return;
        if (source.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is PaletteItem item)
            await _viewModel.AcceptAsync(item);
    }
}
