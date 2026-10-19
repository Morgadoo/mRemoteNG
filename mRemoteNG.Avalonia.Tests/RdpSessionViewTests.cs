using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using FluentAssertions;
using mRemoteNG.Avalonia.Views.Sessions;
using mRemoteNG.Protocols.Rdp;
using Xunit;

namespace mRemoteNG.Avalonia.Tests;

/// <summary>
/// The RDP session tab's message panel. Its icon comes from a style, and a bad style value there used to
/// throw out of ContentPresenter.MeasureOverride and kill the whole window as soon as a session opened.
/// </summary>
public class RdpSessionViewTests
{
    [AvaloniaFact]
    public void MessagePanel_LaysOutWithoutThrowing()
    {
        _ = TestHost.MainWindow;
        var view = new RdpSessionView(embeddingCandidate: false);
        view.ShowMessage("Connecting to server.example…\nWindows Remote Desktop will open in a separate window.",
            showBringToFront: true, showDisconnect: true);

        var window = new Window { Width = 900, Height = 600, Content = view };
        var act = () =>
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            view.Measure(new Size(900, 600));
            view.Arrange(new Rect(0, 0, 900, 600));
            Dispatcher.UIThread.RunJobs();
        };

        act.Should().NotThrow();
        view.Bounds.Width.Should().BeGreaterThan(0);
        window.Close();
    }

    [AvaloniaFact]
    public async Task OpeningAnRdpSession_DoesNotCrashTheMainWindow()
    {
        await SessionTestSupport.ResetAsync();
        var window = TestHost.MainWindow;
        var connection = SessionTestSupport.Connection("portal", 3389, protocol: mRemoteNG.Core.Connection.Protocol.ProtocolType.RDP);
        connection.Hostname = "server.example";

        Exception? crash = null;
        try
        {
            var tab = await TestHost.ViewModel.Sessions.OpenConnectionAsync(connection, AppServices.GetRequired<mRemoteNG.Protocols.Abstractions.IProtocolFactory>());
            tab.Should().NotBeNull();
            for (var i = 0; i < 50; i++)
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(20);
            }
        }
        catch (Exception ex)
        {
            crash = ex;
        }

        crash.Should().BeNull();
        window.IsVisible.Should().BeTrue();
        await SessionTestSupport.ResetAsync();
    }
}
