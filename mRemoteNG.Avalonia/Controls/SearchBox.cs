using System.Windows.Input;
using Avalonia.Controls;

namespace mRemoteNG.Avalonia.Controls;

/// <summary>
/// Support for search fields (<c>&lt;TextBox Classes="search"/&gt;</c>, docs/design-system.md §6): the style in
/// Themes/Controls.axaml adds a leading magnifier and a trailing clear button that runs <see cref="ClearCommand"/>
/// with the TextBox as parameter.
/// </summary>
public static class SearchBox
{
    /// <summary>Clears the TextBox passed as parameter and keeps the focus in it.</summary>
    public static ICommand ClearCommand { get; } = new ClearTextCommand();

    private sealed class ClearTextCommand : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => parameter is TextBox;

        public void Execute(object? parameter)
        {
            if (parameter is not TextBox box)
                return;
            box.Clear();
            box.Focus();
        }
    }
}
