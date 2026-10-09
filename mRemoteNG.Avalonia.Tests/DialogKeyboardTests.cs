using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using mRemoteNG.Avalonia.Controls;
using mRemoteNG.Avalonia.ViewModels;
using mRemoteNG.Avalonia.Views;
using mRemoteNG.Avalonia.Views.Dialogs;
using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Tree.Root;
using Xunit;
using CoreProtocol = mRemoteNG.Core.Connection.Protocol.ProtocolType;

namespace mRemoteNG.Avalonia.Tests;

/// <summary>
/// The shared dialog behaviour (docs/design-system.md §6 Dialogs): focus in the first input, Enter runs the default
/// button, Escape cancels, destructive confirmations use the danger button.
/// </summary>
public class DialogKeyboardTests
{
    private static void Settle()
    {
        for (var i = 0; i < 3; i++)
            Dispatcher.UIThread.RunJobs();
    }

    private static void Press(Window window, Key key, PhysicalKey physical)
    {
        window.KeyPress(key, RawInputModifiers.None, physical, null);
        Settle();
    }

    private static IInputElement? Focused(Window window) => window.FocusManager?.GetFocusedElement();

    [AvaloniaFact]
    public void QuickConnect_OpensInTheHostField_AndEscapeCancels()
    {
        var owner = TestHost.MainWindow;
        var dialog = new QuickConnectDialog();
        var result = dialog.ShowDialog<QuickConnectResult?>(owner);
        Settle();

        Focused(dialog).Should().BeSameAs(dialog.FindControl<TextBox>("HostnameBox"));

        Press(dialog, Key.Escape, PhysicalKey.Escape);
        result.IsCompleted.Should().BeTrue();
        result.Result.Should().BeNull();
    }

    [AvaloniaFact]
    public void TextPrompt_EnterConfirmsTheTypedText()
    {
        var owner = TestHost.MainWindow;
        var dialog = new TextPromptDialog("New folder", "Folder name:", false, null, "Servers");
        var result = dialog.ShowDialog<string?>(owner);
        Settle();

        var input = dialog.FindControl<TextBox>("InputBox")!;
        Focused(dialog).Should().BeSameAs(input, "the dialog opens with the focus in its first input");
        input.Text = "Web servers";
        Press(dialog, Key.Enter, PhysicalKey.Enter);

        result.IsCompleted.Should().BeTrue();
        result.Result.Should().Be("Web servers");
    }

    [AvaloniaFact]
    public void DestructiveConfirmation_NamesTheAction_WithTheDangerButton_AndEnterIsSafe()
    {
        var owner = TestHost.MainWindow;
        var answer = MessageDialog.ConfirmAsync(owner, "Delete", "Delete \"srv\"?", "Delete", "Cancel", confirmIsDefault: false);
        Settle();

        var dialog = owner.OwnedWindows.OfType<MessageDialog>().Single();
        var buttons = dialog.GetVisualDescendants().OfType<Button>().Where(b => b.Content is string).ToList();
        var delete = buttons.Single(b => Equals(b.Content, "Delete"));
        delete.Classes.Should().Contain(["accent", "danger"]);
        buttons.IndexOf(delete).Should().Be(buttons.Count - 1, "the confirming action is the rightmost button");

        Press(dialog, Key.Enter, PhysicalKey.Enter);
        answer.IsCompleted.Should().BeTrue();
        answer.Result.Should().BeFalse("Enter runs the default (cancelling) button of a destructive confirmation");
    }

    [AvaloniaFact]
    public void DialogWithoutCancelButton_ClosesOnEscape_AndSearchFieldsClearFirst()
    {
        _ = TestHost.MainWindow;
        var search = new TextBox { Classes = { "search" }, Text = "abc" };
        var window = new Window { Classes = { "dialog" }, Content = new StackPanel { Children = { search, new TextBox() } } };
        var closed = false;
        window.Closed += (_, _) => closed = true;
        window.Show();
        Settle();

        search.Focus();
        Press(window, Key.Escape, PhysicalKey.Escape);
        search.Text.Should().BeEmpty("Escape clears a non-empty search field first");
        closed.Should().BeFalse();

        Press(window, Key.Escape, PhysicalKey.Escape);
        closed.Should().BeTrue();
    }

    [AvaloniaFact]
    public void ConnectionEditor_OpensInTheNameField_AndEscapeCancels()
    {
        var owner = TestHost.MainWindow;
        var connection = new ConnectionInfo { Name = "srv", Hostname = "srv.local", Protocol = CoreProtocol.SSH2, Port = 22 };
        var vm = new ConnectionDialogViewModel(connection, new RootNodeInfo(RootNodeType.Connection), isNew: false);
        var dialog = new ConnectionDialog(vm);
        var result = dialog.ShowDialog<bool>(owner);
        Settle();

        Focused(dialog).Should().BeOfType<TextBox>().Which.Text.Should().Be("srv");
        vm.HeaderTitle.Should().Be("srv");
        vm.Pages.Single(p => p.Key == "display").IsVisible.Should().BeFalse();

        Press(dialog, Key.Escape, PhysicalKey.Escape);
        result.IsCompleted.Should().BeTrue();
        result.Result.Should().BeFalse();
    }

    [AvaloniaFact]
    public void Options_SearchFiltersThePages_BySettingLabels()
    {
        _ = TestHost.MainWindow;
        var window = new OptionsWindow();
        var vm = (OptionsWindowViewModel)window.DataContext!;
        window.Show();
        try
        {
            Settle();
            window.FindControl<TextBox>("SearchBox")!.IsFocused.Should().BeTrue("the search box is the first input");

            vm.SearchText = "keep-alive";
            vm.VisibleCategories.Select(c => c.Key).Should().Equal("connections");
            vm.SelectedCategory!.Key.Should().Be("connections");

            vm.SearchText = "no such setting";
            vm.HasNoMatches.Should().BeTrue();

            vm.SearchText = string.Empty;
            vm.VisibleCategories.Should().HaveCount(vm.Categories.Count);
            window.GetVisualDescendants().OfType<ListBoxItem>().Should().OnlyContain(i => i.Bounds.Height <= 34,
                "navigation rows stay compact in every theme");
        }
        finally
        {
            window.Close();
        }
    }
}
