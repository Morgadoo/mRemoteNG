using Avalonia.Headless.XUnit;
using FluentAssertions;
using mRemoteNG.Avalonia.Diagnostics;
using mRemoteNG.Avalonia.Services;
using mRemoteNG.Core.Settings;
using Xunit;

namespace mRemoteNG.Avalonia.Tests;

/// <summary>
/// The <c>--smoke-test</c> script run against the headless main window with the CI connection file
/// (<c>ci/smoke/confCons.xml</c>). The CI job runs the same script in the published app on each OS.
/// </summary>
public class SmokeTestTests
{
    [AvaloniaFact]
    public async Task SmokeScript_PassesEveryStep_WithTheCiConnectionFile()
    {
        var window = TestHost.MainWindow;
        var vm = TestHost.ViewModel;
        var settings = AppServices.GetRequired<AppSettingsService>();
        var theme = settings.Current.Theme;
        var reportDirectory = Path.Combine(TestHost.ConfigDirectory, $"smoke-{Guid.NewGuid():N}");
        var smoke = new SmokeTest(reportDirectory, TestHost.Fixture("smoke-confCons.xml"));
        try
        {
            smoke.Prepare(redirectAppData: false);
            vm.ConnectionTree.LoadFromFile(smoke.ConnectionFile!);

            await smoke.RunAsync(window, vm);

            smoke.Report.Steps.Should().OnlyContain(s => s.Status == SmokeStatus.Passed,
                string.Join("; ", smoke.Report.Steps.Select(s => $"{s.Id}: {s.Status} {s.Error}")));
            smoke.Report.Steps.Select(s => s.Id).Should().Equal(
                "setup", "main-window", "connections", "session", "options", "command-palette",
                "theme-dark", "theme-light", "theme-dark-again");
            smoke.Report.ExpectedConnections.Should().Be(4);
            SmokeTest.ExitCodeFor(smoke.Report).Should().Be(SmokeTest.ExitPassed);
            // (The headless test renderer writes no PNG; the published app's renderer does, checked in CI.)

            smoke.Report.Complete(SmokeTest.ExitCodeFor(smoke.Report), 1);
            smoke.Report.Write(reportDirectory);
            File.ReadAllText(Path.Combine(reportDirectory, SmokeTest.ReportFileName)).Should().Contain("\"result\": \"passed\"");
            File.ReadAllText(Path.Combine(reportDirectory, SmokeTest.SummaryFileName)).Should().Contain("| Options window opens and closes (`options`) | ✅ passed |");
        }
        finally
        {
            await vm.Sessions.CloseAllSessionsAsync();
            settings.Update(s => s.Theme = theme);
            ThemeService.Instance.ApplySettings(settings.Current);
            vm.ConnectionTree.CreateNewTree();
        }
    }

    [Fact]
    public void ExitCode_FollowsTheWorstOutcome()
    {
        var report = SmokeReport.Create("r", null, "d");
        SmokeTest.ExitCodeFor(report).Should().Be(SmokeTest.ExitStepFailed, "a run without steps proves nothing");

        report.AddStep(new SmokeStep("a", "A") { Status = SmokeStatus.Passed });
        report.AddStep(new SmokeStep("b", "B") { Status = SmokeStatus.Skipped });
        SmokeTest.ExitCodeFor(report).Should().Be(SmokeTest.ExitPassed);

        report.AddStep(new SmokeStep("c", "C") { Status = SmokeStatus.Failed });
        SmokeTest.ExitCodeFor(report).Should().Be(SmokeTest.ExitStepFailed);

        report.Exceptions.Add(new SmokeException("test", "System.Exception", "boom", null));
        SmokeTest.ExitCodeFor(report).Should().Be(SmokeTest.ExitUnhandledException);
    }

    [Fact]
    public void Complete_MarksUnfinishedStepsFailed()
    {
        var report = SmokeReport.Create("r", null, "d");
        report.AddStep(new SmokeStep("hung", "Hangs"));

        report.Complete(SmokeTest.ExitTimeout, 120_000);

        report.Result.Should().Be("failed");
        report.Steps.Single().Should().BeEquivalentTo(new { Status = SmokeStatus.Failed, Error = "Did not finish." });
        report.ToMarkdown().Should().Contain("exit code 3").And.Contain("| Hangs (`hung`) | ❌ failed |");
    }
}
