using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace mRemoteNG.Avalonia.Controls;

/// <summary>
/// Keyboard behaviour shared by every dialog (docs/design-system.md §1.4, §6 Dialogs), switched on for each
/// <c>Window.dialog</c> by Themes/Dialogs.axaml:
/// <list type="bullet">
/// <item>the dialog opens with the keyboard focus in its first input (or the control marked
/// <see cref="IsInitialFocusProperty"/>) unless the dialog focused something itself;</item>
/// <item>Enter runs the button with <c>IsDefault</c> and Escape the one with <c>IsCancel</c> (Avalonia's buttons);
/// a dialog without a cancel button closes on Escape;</item>
/// <item>Escape in a non-empty search field clears it first.</item>
/// </list>
/// </summary>
public sealed class DialogAssist
{
    /// <summary>Turns the behaviour on for a window (set by the <c>Window.dialog</c> style).</summary>
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<DialogAssist, Window, bool>("IsEnabled");

    /// <summary>Marks the control that gets the focus when the dialog opens.</summary>
    public static readonly AttachedProperty<bool> IsInitialFocusProperty =
        AvaloniaProperty.RegisterAttached<DialogAssist, Control, bool>("IsInitialFocus");

    static DialogAssist()
    {
        IsEnabledProperty.Changed.AddClassHandler<Window>(OnIsEnabledChanged);
    }

    private DialogAssist()
    {
    }

    public static bool GetIsEnabled(Window window) => window.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(Window window, bool value) => window.SetValue(IsEnabledProperty, value);

    public static bool GetIsInitialFocus(Control control) => control.GetValue(IsInitialFocusProperty);

    public static void SetIsInitialFocus(Control control, bool value) => control.SetValue(IsInitialFocusProperty, value);

    private static void OnIsEnabledChanged(Window window, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            window.Opened += OnOpened;
            window.AddHandler(InputElement.KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
            window.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Bubble);
        }
        else
        {
            window.Opened -= OnOpened;
            window.RemoveHandler(InputElement.KeyDownEvent, OnPreviewKeyDown);
            window.RemoveHandler(InputElement.KeyDownEvent, OnKeyDown);
        }
    }

    private static void OnOpened(object? sender, EventArgs e)
    {
        if (sender is Window window)
            Dispatcher.UIThread.Post(() => FocusInitialControl(window), DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Focuses the marked control, else the first editable text field (search fields last), else the default
    /// button. Does nothing when a control of the dialog already has the focus. Returns true when something has it.
    /// </summary>
    public static bool FocusInitialControl(Window window)
    {
        if (window.FocusManager?.GetFocusedElement() is Visual focused && !ReferenceEquals(focused, window)
            && window.IsVisualAncestorOf(focused))
        {
            return true;
        }

        var visuals = window.GetVisualDescendants().OfType<Control>()
            .Where(c => c.IsEffectivelyVisible && c.IsEffectivelyEnabled)
            .ToList();

        var target = visuals.FirstOrDefault(GetIsInitialFocus)
                     ?? visuals.OfType<TextBox>().FirstOrDefault(t => IsEditable(t) && !t.Classes.Contains("search"))
                     ?? visuals.OfType<TextBox>().FirstOrDefault(IsEditable)
                     ?? (Control?)visuals.OfType<Button>().FirstOrDefault(b => b.IsDefault)
                     ?? visuals.FirstOrDefault(c => c.Focusable && c is not Window);
        if (target is null)
            return false;

        if (target.Focus(NavigationMethod.Tab) && target is TextBox box)
            box.SelectAll();
        return true;
    }

    private static bool IsEditable(TextBox box) => box.Focusable && !box.IsReadOnly;

    private static void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None
            && e.Source is Visual source
            && (source as TextBox ?? source.FindAncestorOfType<TextBox>()) is { } box
            && box.Classes.Contains("search") && !string.IsNullOrEmpty(box.Text))
        {
            box.Clear();
            e.Handled = true;
        }
    }

    private static void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || e.Key != Key.Escape || e.KeyModifiers != KeyModifiers.None || sender is not Window window)
            return;

        // Avalonia's IsCancel button closes the dialog itself; only dialogs without one need closing here.
        var hasCancel = window.GetVisualDescendants().OfType<Button>()
            .Any(b => b.IsCancel && b.IsEffectivelyVisible && b.IsEffectivelyEnabled);
        if (hasCancel)
            return;

        e.Handled = true;
        window.Close();
    }
}
