using System.Text.RegularExpressions;

namespace mRemoteNG.Core.Tools;

/// <summary>How variable values are escaped when they are substituted into a command line.</summary>
public enum ArgumentEscapingStyle
{
    /// <summary>
    /// The legacy mRemoteNG (Windows) rules: <c>%VAR%</c> escapes backslashes before quotes (MSVCRT command-line
    /// rules) and cmd.exe metacharacters with <c>^</c>; <c>%-VAR%</c> only the metacharacters; <c>%!VAR%</c> nothing.
    /// </summary>
    WindowsShell,

    /// <summary>
    /// For <see cref="CommandLineTokenizer"/> (Linux/macOS, where no shell is involved): <c>%VAR%</c> and
    /// <c>%-VAR%</c> backslash-escape <c>\</c>, <c>"</c> and <c>'</c> so the value stays one literal piece of an
    /// argument; <c>%!VAR%</c> inserts the value as-is (it may then add arguments).
    /// </summary>
    Posix,
}

/// <summary>
/// Replaces <c>%VARIABLE%</c> placeholders in external tool settings. A port of the legacy
/// <c>mRemoteNG.Tools.ExternalToolArgumentParser</c> with identical results for <see cref="ArgumentEscapingStyle.WindowsShell"/>:
/// <list type="bullet">
/// <item><c>%NAME%</c>, <c>%HOSTNAME%</c>, <c>%PORT%</c>, <c>%USERNAME%</c>, <c>%PASSWORD%</c>, <c>%DOMAIN%</c>,
/// <c>%DESCRIPTION%</c>, <c>%MACADDRESS%</c>, <c>%USERFIELD%</c> (and <c>%PROTOCOL%</c>), case-insensitive;</item>
/// <item>a <c>-</c> or <c>!</c> after the first <c>%</c> selects lighter escaping (see <see cref="ArgumentEscapingStyle"/>);</item>
/// <item>any other name is looked up as an environment variable; unknown names stay as they are;</item>
/// <item><c>\%NAME\%</c> is always read as the environment variable NAME; <c>^%NAME^%</c> yields a literal <c>%NAME%</c>.</item>
/// </list>
/// Without a connection every variable (environment variables included) is replaced by an empty string, as in the legacy parser.
/// </summary>
public sealed class ExternalToolArgumentParser
{
    private readonly ExternalToolVariables? _variables;
    private readonly ArgumentEscapingStyle _style;
    private readonly Func<string, string?> _environment;

    public ExternalToolArgumentParser(ExternalToolVariables? variables, ArgumentEscapingStyle? style = null, Func<string, string?>? environment = null)
    {
        _variables = variables;
        _style = style ?? DefaultStyle;
        _environment = environment ?? Environment.GetEnvironmentVariable;
    }

    /// <summary><see cref="ArgumentEscapingStyle.WindowsShell"/> on Windows, <see cref="ArgumentEscapingStyle.Posix"/> elsewhere.</summary>
    public static ArgumentEscapingStyle DefaultStyle =>
        OperatingSystem.IsWindows() ? ArgumentEscapingStyle.WindowsShell : ArgumentEscapingStyle.Posix;

    public string ParseArguments(string input)
    {
        if (string.IsNullOrEmpty(input))
            return input ?? string.Empty;
        var replacements = BuildReplacementList(input);
        return PerformReplacements(input, replacements);
    }

    private List<Replacement> BuildReplacementList(string input)
    {
        int index = 0;
        var replacements = new List<Replacement>();
        while (true)
        {
            int tokenStart = input.IndexOf('%', index);
            if (tokenStart == -1)
                break;

            int tokenEnd = input.IndexOf('%', tokenStart + 1);
            if (tokenEnd == -1)
                break;

            int tokenLength = tokenEnd - tokenStart + 1;
            int variableNameStart = tokenStart + 1;
            int variableNameLength = tokenLength - 2;
            bool isEnvironmentVariable = false;
            string variableName;

            if (tokenStart > 0)
            {
                char tokenStartPrefix = input[tokenStart - 1];
                char tokenEndPrefix = input[tokenEnd - 1];

                if (tokenStartPrefix == '\\' && tokenEndPrefix == '\\')
                {
                    isEnvironmentVariable = true;

                    // Add the first backslash to the token and drop the last one from the name.
                    tokenStart--;
                    tokenLength++;
                    variableNameLength--;
                }
                else if (tokenStartPrefix == '^' && tokenEndPrefix == '^')
                {
                    // Add the first caret to the token and drop the last one from the name.
                    tokenStart--;
                    tokenLength++;
                    variableNameLength--;

                    variableName = input.Substring(variableNameStart, variableNameLength);
                    replacements.Add(new Replacement(tokenStart, tokenLength, $"%{variableName}%"));

                    index = tokenEnd;
                    continue;
                }
            }

            string token = input.Substring(tokenStart, tokenLength);
            EscapeType escape = DetermineEscapeType(token);

            if (escape != EscapeType.All)
            {
                // Remove the escape character from the name.
                variableNameStart++;
                variableNameLength--;
            }

            if (variableNameLength <= 0)
            {
                index = tokenEnd;
                continue;
            }

            variableName = input.Substring(variableNameStart, variableNameLength);

            string? replacementValue = token;
            if (!isEnvironmentVariable)
                replacementValue = GetVariableReplacement(variableName, token);

            bool haveReplacement;
            if (replacementValue != token)
            {
                haveReplacement = true;
            }
            else
            {
                replacementValue = _environment(variableName);
                haveReplacement = replacementValue is not null;
            }

            if (haveReplacement)
            {
                char trailing = tokenEnd + 2 <= input.Length ? input[tokenEnd + 1] : '\0';
                replacementValue = Escape(replacementValue!, escape, trailing);
                replacements.Add(new Replacement(tokenStart, tokenLength, replacementValue));
                index = tokenEnd + 1;
            }
            else
            {
                index = tokenEnd;
            }
        }

        return replacements;
    }

    private string Escape(string value, EscapeType escape, char trailing)
    {
        if (_style == ArgumentEscapingStyle.Posix)
            return escape == EscapeType.None ? value : EscapePosix(value);

        if (escape == EscapeType.All)
        {
            value = EscapeBackslashes(value);
            if (trailing == '\'')
                value = EscapeBackslashesForTrailingQuote(value);
        }

        if (escape is EscapeType.All or EscapeType.ShellMetacharacters)
            value = EscapeShellMetacharacters(value);
        return value;
    }

    private static EscapeType DetermineEscapeType(string token) => token[1] switch
    {
        '-' => EscapeType.ShellMetacharacters,
        '!' => EscapeType.None,
        _ => EscapeType.All,
    };

    private string GetVariableReplacement(string variable, string original)
    {
        if (_variables is null)
            return string.Empty;
        return _variables.TryGet(variable, out string value) ? value : original;
    }

    private static string PerformReplacements(string input, List<Replacement> replacements)
    {
        string result = input;
        // Right to left so earlier offsets stay valid.
        for (int index = result.Length; index >= 0; index--)
        {
            foreach (var replacement in replacements)
            {
                if (replacement.Start != index)
                    continue;
                result = string.Concat(result.AsSpan(0, replacement.Start), replacement.Value, result.AsSpan(replacement.Start + replacement.Length));
            }
        }

        return result;
    }

    /// <summary>A run of backslashes followed by a double quote: double the backslashes and escape the quote.</summary>
    public static string EscapeBackslashes(string argument) =>
        string.IsNullOrEmpty(argument) ? argument : Regex.Replace(argument, "(\\\\*)\"", "$1$1\\\"");

    /// <summary>A run of backslashes at the end (which will be followed by a quote): double them.</summary>
    public static string EscapeBackslashesForTrailingQuote(string argument) =>
        string.IsNullOrEmpty(argument) ? argument : Regex.Replace(argument, "(\\\\*)$", "$1$1");

    /// <summary>Prefixes cmd.exe metacharacters with <c>^</c>.</summary>
    public static string EscapeShellMetacharacters(string argument) =>
        string.IsNullOrEmpty(argument) ? argument : Regex.Replace(argument, "([()%!^\"<>&|])", "^$1");

    /// <summary>Backslash-escapes the characters <see cref="CommandLineTokenizer"/> treats specially.</summary>
    public static string EscapePosix(string argument) =>
        string.IsNullOrEmpty(argument) ? argument : Regex.Replace(argument, "([\\\\\"'])", "\\$1");

    private enum EscapeType
    {
        All,
        ShellMetacharacters,
        None,
    }

    private readonly record struct Replacement(int Start, int Length, string Value);
}
