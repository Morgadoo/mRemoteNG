using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace mRemoteNG.Avalonia.Diagnostics;

public enum SmokeStatus
{
    Running,
    Passed,
    Failed,
    Skipped,
}

/// <summary>One scripted check of the smoke test.</summary>
public sealed class SmokeStep(string id, string description)
{
    public string Id { get; } = id;

    public string Description { get; } = description;

    public SmokeStatus Status { get; set; } = SmokeStatus.Running;

    public long DurationMs { get; set; }

    public string? Detail { get; set; }

    public string? Error { get; set; }

    public string? StackTrace { get; set; }
}

/// <summary>An exception that reached an unhandled-exception handler during the smoke test.</summary>
public sealed record SmokeException(string Source, string Type, string Message, string? Details);

/// <summary>The result of <c>--smoke-test</c>, written as <c>smoke-report.json</c> and a markdown summary.</summary>
public sealed class SmokeReport
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // a file for people and CI, not for embedding in HTML
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public string Result { get; private set; } = "running";

    public int? ExitCode { get; private set; }

    public string AppVersion { get; init; } = "";

    public string Os { get; init; } = "";

    public string OsPlatform { get; init; } = "";

    public string Architecture { get; init; } = "";

    public string Runtime { get; init; } = "";

    public string? WindowingPlatform { get; set; }

    public double? RenderScaling { get; set; }

    public DateTimeOffset StartedAt { get; init; }

    public long? StartupMs { get; set; }

    public long DurationMs { get; private set; }

    public int TimeoutSeconds { get; set; }

    public string? ConnectionFile { get; init; }

    public int? ExpectedConnections { get; set; }

    public string DataDirectory { get; init; } = "";

    /// <summary>Why the run ended early (global timeout).</summary>
    public string? Error { get; set; }

    public List<SmokeStep> Steps { get; } = [];

    public List<SmokeException> Exceptions { get; } = [];

    public List<string> Screenshots { get; } = [];

    /// <summary>The last entries of the app's log panel.</summary>
    public List<string> Log { get; } = [];

    public static SmokeReport Create(string reportDirectory, string? connectionFile, string dataDirectory) => new()
    {
        AppVersion = typeof(SmokeReport).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                     ?? typeof(SmokeReport).Assembly.GetName().Version?.ToString() ?? "unknown",
        Os = RuntimeInformation.OSDescription,
        OsPlatform = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsLinux() ? "linux" : "other",
        Architecture = RuntimeInformation.OSArchitecture.ToString(),
        Runtime = $"{RuntimeInformation.FrameworkDescription} ({RuntimeInformation.RuntimeIdentifier})",
        StartedAt = DateTimeOffset.Now,
        ConnectionFile = connectionFile,
        DataDirectory = dataDirectory,
    };

    public void AddStep(SmokeStep step) => Steps.Add(step);

    public void Complete(int exitCode, long durationMs)
    {
        ExitCode = exitCode;
        DurationMs = durationMs;
        Result = exitCode == SmokeTest.ExitPassed ? "passed" : "failed";
        foreach (var step in Steps.Where(s => s.Status == SmokeStatus.Running))
        {
            step.Status = SmokeStatus.Failed;
            step.Error ??= "Did not finish.";
        }
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>A markdown summary (for <c>$GITHUB_STEP_SUMMARY</c>): result line, steps table, exceptions.</summary>
    public string ToMarkdown()
    {
        var md = new StringBuilder();
        var icon = Result == "passed" ? "✅" : "❌";
        md.AppendLine($"### {icon} mRemoteNG smoke test {Result} on {OsPlatform} (exit code {ExitCode})");
        md.AppendLine();
        md.AppendLine($"`{AppVersion}` · {Os} · {Architecture} · {Runtime} · window: `{WindowingPlatform ?? "n/a"}` · startup {StartupMs?.ToString() ?? "?"} ms · total {DurationMs} ms");
        md.AppendLine();
        if (Error is not null)
            md.AppendLine($"**{Escape(Error)}**").AppendLine();
        md.AppendLine("| Step | Result | Time | Detail |");
        md.AppendLine("|------|--------|-----:|--------|");
        foreach (var step in Steps)
        {
            var mark = step.Status switch
            {
                SmokeStatus.Passed => "✅ passed",
                SmokeStatus.Skipped => "⏭️ skipped",
                _ => "❌ " + step.Status.ToString().ToLowerInvariant(),
            };
            md.AppendLine($"| {Escape(step.Description)} (`{step.Id}`) | {mark} | {step.DurationMs} ms | {Escape(step.Error ?? step.Detail ?? "")} |");
        }
        if (Exceptions.Count > 0)
        {
            md.AppendLine().AppendLine("**Unhandled exceptions**").AppendLine();
            foreach (var ex in Exceptions)
                md.AppendLine($"- {Escape(ex.Source)}: `{ex.Type}` {Escape(ex.Message)}");
        }
        if (Screenshots.Count > 0)
            md.AppendLine().AppendLine($"Screenshots (in the artifact): {string.Join(", ", Screenshots.Select(s => $"`{s}`"))}");
        return md.ToString();
    }

    public void Write(string directory)
    {
        File.WriteAllText(Path.Combine(directory, SmokeTest.ReportFileName), ToJson());
        File.WriteAllText(Path.Combine(directory, SmokeTest.SummaryFileName), ToMarkdown());
    }

    private static string Escape(string text) =>
        text.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
}
