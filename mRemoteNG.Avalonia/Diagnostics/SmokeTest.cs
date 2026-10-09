using System.Diagnostics;
using System.Net;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Xml;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using mRemoteNG.Avalonia.Services;
using mRemoteNG.Avalonia.ViewModels;
using mRemoteNG.Avalonia.ViewModels.Docking;
using mRemoteNG.Avalonia.Views;
using mRemoteNG.Core.App;
using mRemoteNG.Core.Config;
using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Container;
using mRemoteNG.Core.Settings;
using mRemoteNG.Platform.Settings;
using mRemoteNG.Protocols.Abstractions;
using CoreProtocolType = mRemoteNG.Core.Connection.Protocol.ProtocolType;

namespace mRemoteNG.Avalonia.Diagnostics;

/// <summary>
/// End-to-end smoke test of the real app, for CI: <c>mRemoteNG.Avalonia --smoke-test &lt;report-dir&gt; [connection-file]</c>.
/// <para>
/// The app starts normally (DI, platform services, settings, theme, main window), but with its data kept in
/// <c>&lt;report-dir&gt;/smoke-data</c> (portable-mode override; the user's settings are never touched) and the
/// connection file copied there. Once the main window is open a short script runs on the UI thread: window laid
/// out, tree populated, a RAW session to a loopback echo listener started here (works on every OS without
/// external servers) reaching Connected, the Options window and the command palette opened and closed, and
/// the theme switched Dark → Light → Dark with a <see cref="RenderTargetBitmap"/> of the main window saved after
/// each switch. <c>smoke-report.json</c> and <c>smoke-report.md</c> are written to the report directory.
/// </para>
/// <para>
/// Exit codes: 0 passed, 1 a step failed, 2 unhandled exception, 3 global timeout, 4 bad arguments or setup.
/// Environment: <c>MRNG_SMOKE_TIMEOUT_SECONDS</c> (default 120); <c>MRNG_SMOKE_FAIL_STEP</c> injects a failure
/// for testing the harness: a step id makes that step throw, <c>unhandled</c> throws on the dispatcher,
/// <c>hang</c> blocks the script until the global timeout.
/// </para>
/// </summary>
public sealed class SmokeTest
{
    public const int ExitPassed = 0;
    public const int ExitStepFailed = 1;
    public const int ExitUnhandledException = 2;
    public const int ExitTimeout = 3;
    public const int ExitSetupFailed = 4;

    public const string ReportFileName = "smoke-report.json";
    public const string SummaryFileName = "smoke-report.md";
    public const string DataFolderName = "smoke-data";
    public const string FailStepVariable = "MRNG_SMOKE_FAIL_STEP";
    public const string TimeoutVariable = "MRNG_SMOKE_TIMEOUT_SECONDS";
    public const string EchoProbe = "mremoteng-smoke-ping";

    private const string DataMarkerFileName = ".mremoteng-smoke-data";
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan UiTimeout = TimeSpan.FromSeconds(15);

    private readonly object _sync = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly string? _failStep;
    private Timer? _watchdog;
    private Window? _window;
    private int _finished;
    private string? _currentStep;

    public SmokeTest(string reportDirectory, string? connectionFile, string? failStep = null)
    {
        ReportDirectory = Path.GetFullPath(reportDirectory);
        DataDirectory = Path.Combine(ReportDirectory, DataFolderName);
        SourceConnectionFile = connectionFile is null ? null : Path.GetFullPath(Environment.ExpandEnvironmentVariables(connectionFile));
        _failStep = string.IsNullOrWhiteSpace(failStep) ? null : failStep.Trim();
        Report = SmokeReport.Create(ReportDirectory, SourceConnectionFile, DataDirectory);
    }

    /// <summary>The smoke test of this process (null in a normal run).</summary>
    public static SmokeTest? Current { get; private set; }

    public string ReportDirectory { get; }

    /// <summary>Isolated data directory (settings, credentials, log) inside the report directory.</summary>
    public string DataDirectory { get; }

    /// <summary>The connection file given on the command line.</summary>
    public string? SourceConnectionFile { get; }

    /// <summary>The copy of <see cref="SourceConnectionFile"/> the app opens (so a save can't change the original).</summary>
    public string? ConnectionFile { get; private set; }

    public SmokeReport Report { get; }

    /// <summary>
    /// Called first thing in <c>Main</c> for <c>--smoke-test</c>: prepares the isolated data directory, routes
    /// all app data there, installs the exception handlers and starts the global timeout. Exits the process
    /// (code 4, with a report when possible) when the arguments or the directories are unusable.
    /// </summary>
    public static SmokeTest Start(StartupArguments arguments)
    {
        if (arguments.SmokeTestReportDirectory is null)
        {
            Console.Error.WriteLine("mRemoteNG: usage: --smoke-test <report-dir> [connection-file]");
            Environment.Exit(ExitSetupFailed);
        }

        var smoke = new SmokeTest(arguments.SmokeTestReportDirectory!, arguments.ConnectionFile,
            Environment.GetEnvironmentVariable(FailStepVariable));
        Current = smoke;
        Console.WriteLine($"mRemoteNG smoke test: report in {smoke.ReportDirectory}");
        try
        {
            smoke.Prepare();
        }
        catch (Exception ex)
        {
            smoke.Report.AddStep(new SmokeStep("setup", "Prepare the report and isolated data directories")
            {
                Status = SmokeStatus.Failed,
                Error = ex.Message,
            });
            smoke.Finish(ExitSetupFailed, desktop: null);
        }

        smoke.InstallProcessHandlers();
        smoke.StartWatchdog(ReadTimeout());
        return smoke;
    }

    private static TimeSpan ReadTimeout() =>
        int.TryParse(Environment.GetEnvironmentVariable(TimeoutVariable), out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.FromSeconds(120);

    /// <summary>
    /// Creates the report and data directories and copies the connection file. With
    /// <paramref name="redirectAppData"/> (the real run) all app data is routed to the data directory.
    /// </summary>
    public void Prepare(bool redirectAppData = true)
    {
        var setup = new SmokeStep("setup", "Prepare the report and isolated data directories");
        var started = _clock.Elapsed;
        Directory.CreateDirectory(ReportDirectory);
        foreach (var stale in new[] { ReportFileName, SummaryFileName }
                     .Concat(Directory.EnumerateFiles(ReportDirectory, "main-window*.png").Select(Path.GetFileName)))
            File.Delete(Path.Combine(ReportDirectory, stale!));

        // Only a directory this harness created is wiped: never delete a folder someone else owns.
        if (Directory.Exists(DataDirectory))
        {
            if (!File.Exists(Path.Combine(DataDirectory, DataMarkerFileName)) && Directory.EnumerateFileSystemEntries(DataDirectory).Any())
                throw new IOException($"{DataDirectory} exists and was not created by the smoke test; choose another report directory.");
            Directory.Delete(DataDirectory, recursive: true);
        }
        Directory.CreateDirectory(DataDirectory);
        File.WriteAllText(Path.Combine(DataDirectory, DataMarkerFileName), "Created by mRemoteNG --smoke-test; deleted on the next run.\n");

        // Everything the app stores (settings, credentials, key file, known_hosts, log) goes here.
        if (redirectAppData)
        {
            AppDataLocation.UsePortableDirectory(DataDirectory);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", DataDirectory);
        }

        if (SourceConnectionFile is not null)
        {
            if (!File.Exists(SourceConnectionFile))
                throw new FileNotFoundException($"Connection file not found: {SourceConnectionFile}", SourceConnectionFile);
            ConnectionFile = Path.Combine(DataDirectory, Path.GetFileName(SourceConnectionFile));
            File.Copy(SourceConnectionFile, ConnectionFile, overwrite: true);
            Report.ExpectedConnections = CountConnections(ConnectionFile);
        }

        setup.Status = SmokeStatus.Passed;
        setup.DurationMs = (long)(_clock.Elapsed - started).TotalMilliseconds;
        setup.Detail = SourceConnectionFile is null
            ? $"Data in {DataDirectory}; no connection file given"
            : $"Data in {DataDirectory}; {Report.ExpectedConnections} connection(s) in {Path.GetFileName(SourceConnectionFile)}";
        Report.AddStep(setup);
    }

    /// <summary>Connections (not folders) in an unencrypted mRemoteNG XML file, counted independently of the app's loader.</summary>
    public static int? CountConnections(string path)
    {
        var doc = new XmlDocument();
        doc.Load(path);
        if (doc.DocumentElement?.GetAttribute("FullFileEncryption") == "true")
            return null;
        return doc.SelectNodes("//Node[@Type='Connection']")?.Count ?? 0;
    }

    private void InstallProcessHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            RecordException("AppDomain.UnhandledException", e.ExceptionObject as Exception);
            // The runtime ends the process after this handler: write the report and exit with our code first.
            Finish(ExitUnhandledException, desktop: null);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            RecordException("TaskScheduler.UnobservedTaskException", e.Exception);
            e.SetObserved();
        };
    }

    private void StartWatchdog(TimeSpan timeout)
    {
        Report.TimeoutSeconds = (int)timeout.TotalSeconds;
        _watchdog = new Timer(_ =>
        {
            lock (_sync)
                Report.Error = $"Global timeout: the smoke test did not finish within {timeout.TotalSeconds:0} s"
                               + (_currentStep is null ? "." : $" (running step \"{_currentStep}\").");
            SnapshotFromWatchdog();
            Finish(ExitTimeout, desktop: null);
        }, null, timeout, Timeout.InfiniteTimeSpan);
    }

    /// <summary>After a timeout: the log and a picture of the window, if the UI thread still responds within 5 s.</summary>
    private void SnapshotFromWatchdog()
    {
        if (_window is not { } window)
            return;
        try
        {
            Dispatcher.UIThread.InvokeAsync(async () =>
            {
                if (window.DataContext is MainWindowViewModel vm)
                    CaptureLog(vm);
                await RenderAsync(window, "main-window.png");
            }).Wait(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"mRemoteNG smoke test: no snapshot after the timeout: {ex.Message}");
        }
    }

    /// <summary>Settings for an unattended run: dark theme to start from, no prompts, no update check.</summary>
    public static void ConfigureSettings(AppSettingsService settings) =>
        settings.Update(s =>
        {
            s.Theme = ThemeMode.Dark;
            s.ThemeName = string.Empty;
            s.CheckForUpdatesOnStartup = false;
            s.ConfirmCloseConnection = ConfirmCloseEnum.Never;
            s.SingleInstance = false;
            s.StartMinimized = false;
            s.AlwaysShowPanelSelectionDlg = false;
        });

    /// <summary>Runs the script once the main window is open, then exits the app with the result.</summary>
    public void Attach(IClassicDesktopStyleApplicationLifetime desktop, MainWindow window)
    {
        _window = window;
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            RecordException("Dispatcher.UIThread.UnhandledException", e.Exception);
            e.Handled = true;
        };

        window.Opened += async (_, _) =>
        {
            Report.StartupMs = (long)(DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds;
            try
            {
                if (_failStep == "unhandled")
                    Dispatcher.UIThread.Post(() => throw new InvalidOperationException($"Injected unhandled exception ({FailStepVariable}=unhandled)."));
                await RunAsync(window, (MainWindowViewModel)window.DataContext!);
            }
            catch (Exception ex)
            {
                RecordException("smoke script", ex);
            }

            // A failed run still leaves a picture of its final state.
            if (!File.Exists(Path.Combine(ReportDirectory, "main-window.png")))
            {
                try
                {
                    await RenderAsync(window, "main-window.png");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"mRemoteNG smoke test: could not render the main window: {ex.Message}");
                }
            }
            Finish(null, desktop);
        };
    }

    /// <summary>The scripted checks (UI thread). Each step records its own result; later steps still run after a failure.</summary>
    public async Task RunAsync(Window window, MainWindowViewModel vm)
    {
        Report.WindowingPlatform = window.PlatformImpl?.GetType().FullName;
        Report.RenderScaling = window.RenderScaling;
        ConnectionInfo? echoConnection = null;
        SmokeEchoServer? echo = null;

        try
        {
            await StepAsync("main-window", "Main window shown and laid out", async () =>
            {
                await WaitUntilAsync(() => window.IsVisible && window.Bounds is { Width: > 200, Height: > 150 }, UiTimeout, "the main window to be visible and laid out");
                var trees = window.GetVisualDescendants().OfType<TreeView>().Count();
                if (trees == 0)
                    throw new InvalidOperationException("The connection tree view is not in the window.");
                if (!window.GetVisualDescendants().OfType<Menu>().Any())
                    throw new InvalidOperationException("The main menu is not in the window.");
                return $"{window.Bounds.Width:0}x{window.Bounds.Height:0} at scaling {window.RenderScaling:0.##}; {window.GetVisualDescendants().Count()} visuals";
            });

            await StepAsync("connections", "Connection file loaded into the tree", async () =>
            {
                if (ConnectionFile is null)
                    return Skip("no connection file given");
                await WaitUntilAsync(() => CountTreeConnections(vm) > 0 && vm.ConnectionTree.CurrentFilePath is not null, UiTimeout,
                    $"the tree to show the connections of {Path.GetFileName(ConnectionFile)}");
                var count = CountTreeConnections(vm);
                if (Report.ExpectedConnections is { } expected && count != expected)
                    throw new InvalidOperationException($"The tree has {count} connection(s); the file has {expected}.");
                if (!PathsEqual(vm.ConnectionTree.CurrentFilePath, ConnectionFile))
                    throw new InvalidOperationException($"The open file is {vm.ConnectionTree.CurrentFilePath}, not {ConnectionFile}.");
                await NextFrameAsync();
                var realized = window.GetVisualDescendants().OfType<TreeViewItem>().Count();
                if (realized == 0)
                    throw new InvalidOperationException("The tree view shows no items.");
                return $"{count} connection(s); {realized} tree item(s) realized";
            });

            await StepAsync("session", "RAW session to a loopback echo listener reaches Connected", async () =>
            {
                if (vm.ConnectionTree.Root is null || ConnectionFile is null)
                    return Skip("no connection file given");
                echoConnection = vm.ConnectionTree.Root.GetRecursiveChildList()
                    .FirstOrDefault(c => c is not ContainerInfo && c.Protocol == CoreProtocolType.RAW && IsLoopback(c.Hostname))
                    ?? throw new InvalidOperationException("The connection file has no RAW connection to 127.0.0.1/localhost.");

                echo = SmokeEchoServer.Start();
                echoConnection.Port = echo.Port;
                var started = _clock.Elapsed;
                await vm.ConnectionTree.ConnectAsync(echoConnection, null);
                SessionTabViewModel? session = null;
                await WaitUntilAsync(() =>
                {
                    session = vm.Sessions.Sessions.FirstOrDefault(s => ReferenceEquals(s.Connection, echoConnection));
                    if (session?.State == ConnectionState.Error)
                        throw new InvalidOperationException($"The session failed: {session.StatusText}");
                    return session?.State == ConnectionState.Connected;
                }, ConnectTimeout, $"\"{echoConnection.Name}\" to report Connected");
                var connectedMs = (_clock.Elapsed - started).TotalMilliseconds;

                if (session!.Protocol is not ITerminalProtocol terminal)
                    throw new InvalidOperationException($"{session.Protocol.GetType().Name} is not a terminal protocol.");
                await terminal.SendInputAsync(System.Text.Encoding.UTF8.GetBytes(EchoProbe + "\r"));
                await echo.WaitForAsync(EchoProbe, TimeSpan.FromSeconds(10));
                return $"\"{echoConnection.Name}\" connected to 127.0.0.1:{echo.Port} in {connectedMs:0} ms; typed input reached the listener";
            });

            await StepAsync("options", "Options window opens and closes", async () =>
            {
                // The menu command opens it over the desktop lifetime's main window; the headless test host has no
                // desktop lifetime, so there the window is shown the same way directly.
                var run = Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime
                    ? vm.OpenOptionsCommand.Execute().ToTask()
                    : new OptionsWindow().ShowDialog(window);
                OptionsWindow? options = null;
                await WaitUntilAsync(() =>
                {
                    options = window.OwnedWindows.OfType<OptionsWindow>().FirstOrDefault();
                    return options is { IsVisible: true, Bounds.Width: > 100 };
                }, UiTimeout, "the Options window to open");
                await NextFrameAsync();
                var size = $"{options!.Bounds.Width:0}x{options.Bounds.Height:0}";
                options.Close();
                await WithTimeout(run, UiTimeout, "the Options command to complete");
                await WaitUntilAsync(() => !window.OwnedWindows.OfType<OptionsWindow>().Any(), UiTimeout, "the Options window to close");
                return $"Opened at {size} and closed";
            });

            await StepAsync("command-palette", "Command palette opens, finds a connection and closes", async () =>
            {
                await vm.OpenCommandPaletteCommand.Execute().ToTask();
                await WaitUntilAsync(() => vm.Palette.IsOpen, UiTimeout, "the command palette to open");
                var query = echoConnection?.Name ?? "Options";
                vm.Palette.Query = query;
                await WaitUntilAsync(() => vm.Palette.Results.Any(r => r.Title == query), UiTimeout, $"the palette to list \"{query}\"");
                var results = vm.Palette.Results.Count;
                vm.Palette.Close();
                await WaitUntilAsync(() => !vm.Palette.IsOpen, UiTimeout, "the command palette to close");
                return $"\"{query}\": {results} result(s)";
            });

            await StepAsync("theme-dark", "Dark theme applied and rendered", async () =>
            {
                if (!vm.IsDarkTheme)
                    await vm.ToggleThemeCommand.Execute().ToTask();
                return await ExpectThemeAndRenderAsync(window, ThemeVariant.Dark, "main-window-dark.png");
            });

            await StepAsync("theme-light", "Switched to the light theme and rendered", async () =>
            {
                await vm.ToggleThemeCommand.Execute().ToTask();
                return await ExpectThemeAndRenderAsync(window, ThemeVariant.Light, "main-window-light.png");
            });

            await StepAsync("theme-dark-again", "Switched back to the dark theme and rendered", async () =>
            {
                await vm.ToggleThemeCommand.Execute().ToTask();
                return await ExpectThemeAndRenderAsync(window, ThemeVariant.Dark, "main-window.png");
            });
        }
        finally
        {
            // Surface unobserved task exceptions from the run before the result is decided.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            CaptureLog(vm);
            echo?.Dispose();
        }
    }

    private async Task<string> ExpectThemeAndRenderAsync(Window window, ThemeVariant variant, string fileName)
    {
        await WaitUntilAsync(() => ThemeService.Instance.EffectiveVariant == variant && Application.Current?.ActualThemeVariant == variant,
            UiTimeout, $"the {variant} theme to apply");
        var (size, colors) = await RenderAsync(window, fileName);
        if (colors is null)
            return $"{fileName}: {size} (this renderer cannot read pixels back; blank check skipped)";
        if (colors < 8)
            throw new InvalidOperationException($"{fileName} looks blank ({colors} distinct sampled colours).");
        return $"{fileName}: {size}, {colors} distinct sampled colours";
    }

    /// <summary>Renders <paramref name="window"/> into a PNG in the report directory (no screen grabber needed).</summary>
    public async Task<(string Size, int? SampledColors)> RenderAsync(Window window, string fileName)
    {
        await NextFrameAsync();
        var scaling = window.RenderScaling;
        var pixels = PixelSize.FromSize(window.Bounds.Size, scaling);
        using var bitmap = new RenderTargetBitmap(pixels, new Vector(96 * scaling, 96 * scaling));
        bitmap.Render(window);
        var path = Path.Combine(ReportDirectory, fileName);
        bitmap.Save(path);
        var colors = CountSampledColors(bitmap, pixels);
        if (colors is not null && !(File.Exists(path) && new FileInfo(path).Length > 0))
            throw new IOException($"{fileName} was not written.");
        if (File.Exists(path))
        {
            lock (_sync)
            {
                if (!Report.Screenshots.Contains(fileName))
                    Report.Screenshots.Add(fileName);
            }
        }
        return ($"{pixels.Width}x{pixels.Height} px", colors);
    }

    /// <summary>Distinct colours on a 64x64 grid (a blank render has one or two); null when pixels can't be read.</summary>
    private static int? CountSampledColors(Bitmap bitmap, PixelSize size)
    {
        var stride = size.Width * 4;
        var buffer = new byte[stride * size.Height];
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(new PixelRect(size), handle.AddrOfPinnedObject(), buffer.Length, stride);
        }
        catch (NotSupportedException)
        {
            return null; // the headless test renderer
        }
        finally
        {
            handle.Free();
        }

        var colors = new HashSet<int>();
        for (var y = 0; y < size.Height; y += Math.Max(1, size.Height / 64))
        {
            for (var x = 0; x < size.Width; x += Math.Max(1, size.Width / 64))
                colors.Add(BitConverter.ToInt32(buffer, y * stride + x * 4));
        }
        return colors.Count;
    }

    private async Task StepAsync(string id, string description, Func<Task<string>> body)
    {
        var step = new SmokeStep(id, description);
        lock (_sync)
        {
            _currentStep = id;
            Report.AddStep(step);
        }
        var started = _clock.Elapsed;
        try
        {
            if (_failStep == "hang" && id == "main-window")
                await Task.Delay(Timeout.Infinite);
            if (string.Equals(_failStep, id, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Injected failure ({FailStepVariable}={id}).");

            var detail = await body();
            lock (_sync)
            {
                step.Detail = detail;
                step.Status = detail.StartsWith(SkipPrefix, StringComparison.Ordinal) ? SmokeStatus.Skipped : SmokeStatus.Passed;
            }
        }
        catch (Exception ex)
        {
            lock (_sync)
            {
                step.Status = SmokeStatus.Failed;
                step.Error = $"{ex.GetType().Name}: {ex.Message}";
                step.StackTrace = ex.StackTrace;
            }
        }
        finally
        {
            lock (_sync)
            {
                step.DurationMs = (long)(_clock.Elapsed - started).TotalMilliseconds;
                _currentStep = null;
            }
            Console.WriteLine($"  [{step.Status.ToString().ToUpperInvariant(),-7}] {step.Id} ({step.DurationMs} ms) {step.Error ?? step.Detail}");
        }
    }

    private const string SkipPrefix = "skipped: ";

    private static string Skip(string reason) => SkipPrefix + reason;

    /// <summary>0 when every step passed or was skipped and nothing was thrown unhandled.</summary>
    public static int ExitCodeFor(SmokeReport report)
    {
        if (report.Exceptions.Count > 0)
            return ExitUnhandledException;
        if (report.Steps.Count == 0 || report.Steps.Any(s => s.Status != SmokeStatus.Passed && s.Status != SmokeStatus.Skipped))
            return ExitStepFailed;
        return ExitPassed;
    }

    private void RecordException(string source, Exception? exception)
    {
        lock (_sync)
        {
            Report.Exceptions.Add(new SmokeException(source, exception?.GetType().FullName ?? "unknown",
                exception?.Message ?? "(no exception object)", exception?.ToString()));
        }
        Console.Error.WriteLine($"mRemoteNG smoke test: {source}: {exception}");
    }

    private void CaptureLog(MainWindowViewModel vm)
    {
        lock (_sync)
        {
            Report.Log.Clear();
            Report.Log.AddRange(vm.LogPanel.Entries.TakeLast(200).Select(e => e.ToString()));
        }
    }

    /// <summary>
    /// Writes the report (once) and ends the process with <paramref name="forcedExitCode"/>, or the code the
    /// report's steps and exceptions give when null.
    /// </summary>
    private void Finish(int? forcedExitCode, IClassicDesktopStyleApplicationLifetime? desktop)
    {
        if (Interlocked.Exchange(ref _finished, 1) == 1)
            return;
        _watchdog?.Dispose();

        int exitCode;
        lock (_sync)
        {
            exitCode = forcedExitCode ?? ExitCodeFor(Report);
            Report.Complete(exitCode, (long)_clock.Elapsed.TotalMilliseconds);
            try
            {
                Report.Write(ReportDirectory);
                Console.WriteLine($"mRemoteNG smoke test {Report.Result}: exit code {exitCode}; report {Path.Combine(ReportDirectory, ReportFileName)}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"mRemoteNG smoke test: could not write the report: {ex}");
            }
        }

        Environment.ExitCode = exitCode;
        if (desktop is null)
            Environment.Exit(exitCode);

        // Exercise the normal shutdown, but never let a stuck window or thread keep CI waiting.
        _ = new Timer(_ => Environment.Exit(exitCode), null, TimeSpan.FromSeconds(15), Timeout.InfiniteTimeSpan);
        desktop!.Shutdown(exitCode);
    }

    private static int CountTreeConnections(MainWindowViewModel vm) =>
        vm.ConnectionTree.Root?.GetRecursiveChildList().Count(c => c is not ContainerInfo) ?? 0;

    private static bool IsLoopback(string? host) =>
        host is { Length: > 0 }
        && (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address)));

    private static bool PathsEqual(string? a, string? b) =>
        a is not null && b is not null
        && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b),
            OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    /// <summary>Lets layout and rendering catch up.</summary>
    private static async Task NextFrameAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        await Task.Delay(100);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string what)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > timeout)
                throw new TimeoutException($"Timed out after {timeout.TotalSeconds:0} s waiting for {what}.");
            await Task.Delay(50);
        }
    }

    private static async Task WithTimeout(Task task, TimeSpan timeout, string what)
    {
        if (await Task.WhenAny(task, Task.Delay(timeout)) != task)
            throw new TimeoutException($"Timed out after {timeout.TotalSeconds:0} s waiting for {what}.");
        await task;
    }
}
