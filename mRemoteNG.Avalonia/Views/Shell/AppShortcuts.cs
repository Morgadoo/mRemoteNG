using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.VisualTree;
using mRemoteNG.Avalonia.Localization;
using mRemoteNG.Avalonia.Views.Sessions;
using mRemoteNG.Protocols.Ssh;

namespace mRemoteNG.Avalonia.Views.Shell;

/// <summary>Where the keyboard focus is, as far as application shortcuts are concerned.</summary>
public enum ShortcutFocus
{
    /// <summary>Tree, lists, buttons, nothing focused: every shortcut works.</summary>
    Window,

    /// <summary>A text field (quick connect, search, palette…): text-editing keys stay with the field.</summary>
    TextInput,

    /// <summary>A terminal (SSH, Telnet, local shell…): it receives every key except the app chords.</summary>
    Terminal,

    /// <summary>Any other session view (VNC…): the remote machine receives every key.</summary>
    RemoteSession,
}

/// <summary>
/// The rule that decides which application shortcuts may take a key away from the focused control.
/// <para>
/// Avalonia runs a window's <see cref="KeyBinding"/>s <b>before</b> the focused control sees the key (even before
/// tunnelling handlers), so an unguarded Ctrl+D binding would swallow EOF in a terminal and Delete would delete the
/// selected connection while typing in a text box. Every app shortcut is therefore registered through
/// <see cref="AppShortcuts"/>, whose binding only claims the key when <see cref="Allows"/> says so — otherwise the
/// key goes to the focused control as if no binding existed:
/// </para>
/// <list type="bullet">
///   <item><b>Window</b> (tree, buttons, nothing focused): every shortcut.</item>
///   <item><b>Text field</b>: every shortcut except text-editing keys — keys without a modifier (Delete…),
///         Shift+key, and Ctrl(+Shift)+A/C/V/X/Y/Z/Insert/Delete/Backspace/arrows/Home/End.</item>
///   <item><b>Terminal</b>: only Ctrl+Shift+&lt;key&gt; app chords (Ctrl+Shift+P palette, Ctrl+Shift+T tree,
///         Ctrl+Shift+L log, Ctrl+Shift+N/S…) except Ctrl+Shift+C/V (the terminal's copy/paste), and F11. Plain
///         Ctrl+letters (Ctrl+C/D/E/K/N/O/Q/S/J…) are control characters and belong to the shell.</item>
///   <item><b>Remote session</b> (VNC…): only F11; everything else goes to the remote machine.</item>
/// </list>
/// Session navigation (Ctrl+Tab, Ctrl+Shift+Tab, Ctrl+1…9) is handled separately by
/// <see cref="SessionKeyboard"/> and works everywhere; Alt+F4 is left to the operating system. An RDP session is a
/// native window: its keys never reach the application.
/// </summary>
public static class ShortcutPolicy
{
    private static readonly HashSet<Key> TextEditingKeys =
    [
        Key.A, Key.C, Key.V, Key.X, Key.Y, Key.Z, Key.Insert, Key.Delete, Key.Back,
        Key.Left, Key.Right, Key.Up, Key.Down, Key.Home, Key.End,
    ];

    /// <summary>Classifies the focused element (walking up its visual ancestors).</summary>
    public static ShortcutFocus FocusOf(object? focused)
    {
        var visual = focused as Visual;
        var textInput = false;
        for (var current = visual; current is not null; current = current.GetVisualParent())
        {
            switch (current)
            {
                case TerminalView:
                    return ShortcutFocus.Terminal;
                case TextBox:
                    textInput = true;
                    break;
                case SessionContentHost:
                    return textInput ? ShortcutFocus.TextInput
                        : visual is Button or ToggleButton ? ShortcutFocus.Window
                        : ShortcutFocus.RemoteSession;
            }
        }
        return textInput ? ShortcutFocus.TextInput : ShortcutFocus.Window;
    }

    /// <summary>True when <paramref name="gesture"/> may run its app command while the focus is in <paramref name="focus"/>.</summary>
    public static bool Allows(KeyGesture gesture, ShortcutFocus focus) => focus switch
    {
        ShortcutFocus.TextInput => !IsTextEditing(gesture),
        ShortcutFocus.Terminal => IsAppChord(gesture) || gesture is { Key: Key.F11, KeyModifiers: KeyModifiers.None },
        ShortcutFocus.RemoteSession => gesture is { Key: Key.F11, KeyModifiers: KeyModifiers.None },
        _ => true,
    };

    /// <summary>Ctrl+Shift+key, except the terminal's own Ctrl+Shift+C / Ctrl+Shift+V.</summary>
    public static bool IsAppChord(KeyGesture gesture) =>
        gesture.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift) && gesture.Key is not (Key.C or Key.V);

    private static bool IsTextEditing(KeyGesture gesture)
    {
        var modifiers = gesture.KeyModifiers;
        if (modifiers is KeyModifiers.None or KeyModifiers.Shift)
            return gesture.Key is not (>= Key.F1 and <= Key.F24);
        if (modifiers is KeyModifiers.Control or (KeyModifiers.Control | KeyModifiers.Shift))
            return TextEditingKeys.Contains(gesture.Key);
        return false;
    }
}

/// <summary>
/// Turns the shortcuts shown in the menus into working window key bindings (a menu item's InputGesture is only a
/// label in Avalonia), guarded by <see cref="ShortcutPolicy"/>. Also lists the menu commands for the command
/// palette.
/// </summary>
public static class AppShortcuts
{
    /// <summary>Gestures shown in menus that something else handles: the OS (Alt+F4) and <see cref="SessionKeyboard"/>.</summary>
    private static readonly KeyGesture[] HandledElsewhere =
    [
        new(Key.F4, KeyModifiers.Alt),
        new(Key.Tab, KeyModifiers.Control),
        new(Key.Tab, KeyModifiers.Control | KeyModifiers.Shift),
    ];

    /// <summary>
    /// Adds a guarded key binding to <paramref name="window"/> for every menu item below <paramref name="menu"/>
    /// that shows an InputGesture. The item's command and parameter are read when the key is pressed, so bindings
    /// that resolve later (or change) are followed. Returns the bindings added.
    /// </summary>
    public static IReadOnlyList<KeyBinding> RegisterMenu(Window window, Menu menu)
    {
        var added = new List<KeyBinding>();
        foreach (var (item, _) in MenuItems(menu))
        {
            if (item.InputGesture is not { } gesture || HandledElsewhere.Any(g => g.Equals(gesture)))
                continue;
            if (window.KeyBindings.Any(b => gesture.Equals(b.Gesture)))
                continue;
            var binding = new KeyBinding
            {
                Gesture = gesture,
                Command = new GuardedCommand(window, gesture, () => item.Command, () => item.CommandParameter),
            };
            window.KeyBindings.Add(binding);
            added.Add(binding);
        }
        return added;
    }

    /// <summary>Adds a guarded key binding for a shortcut that is not in a menu (e.g. a second gesture for a command).</summary>
    public static KeyBinding Register(Window window, KeyGesture gesture, Func<ICommand?> command, object? parameter = null)
    {
        var binding = new KeyBinding { Gesture = gesture, Command = new GuardedCommand(window, gesture, command, () => parameter) };
        window.KeyBindings.Add(binding);
        return binding;
    }

    /// <summary>True when <paramref name="gesture"/> may run an app command with the current focus of <paramref name="window"/>.</summary>
    public static bool IsAllowed(Window window, KeyGesture gesture) =>
        ShortcutPolicy.Allows(gesture, ShortcutPolicy.FocusOf(window.FocusManager?.GetFocusedElement()));

    /// <summary>Every menu item below <paramref name="menu"/> with the path of headers above it (access keys removed).</summary>
    public static IEnumerable<(MenuItem Item, IReadOnlyList<string> Path)> MenuItems(Menu menu) =>
        Walk(menu.Items.OfType<MenuItem>(), []);

    private static IEnumerable<(MenuItem, IReadOnlyList<string>)> Walk(IEnumerable<MenuItem> items, IReadOnlyList<string> path)
    {
        foreach (var item in items)
        {
            var label = HeaderText(item);
            yield return (item, path);
            var children = item.Items.OfType<MenuItem>().ToList();
            if (children.Count > 0)
            {
                foreach (var child in Walk(children, [.. path, label]))
                    yield return child;
            }
        }
    }

    /// <summary>A menu header as plain text: no access-key underscore, no trailing "..." / "…".</summary>
    public static string HeaderText(MenuItem item)
    {
        var text = TrExtension.StripUnderscoreAccessKey(item.Header as string ?? item.Header?.ToString() ?? string.Empty).Trim();
        if (text.EndsWith("...", StringComparison.Ordinal))
            text = text[..^3];
        return text.TrimEnd('…', ' ', ':');
    }

    /// <summary>A gesture as shown to users ("Ctrl+Shift+N", "Ctrl+,").</summary>
    public static string Format(KeyGesture gesture) => gesture.ToString("p", null);

    private sealed class GuardedCommand(Window window, KeyGesture gesture, Func<ICommand?> command, Func<object?> parameter) : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? _) =>
            IsAllowed(window, gesture) && command() is { } target && target.CanExecute(parameter());

        public void Execute(object? _)
        {
            if (command() is { } target && target.CanExecute(parameter()))
                target.Execute(parameter());
        }
    }
}
