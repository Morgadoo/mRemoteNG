using System.Text;

namespace mRemoteNG.Core.Tools;

/// <summary>
/// Splits an external tool's argument string into separate arguments on Linux and macOS, where programs are
/// started without a shell (so no globbing, variable expansion or redirection takes place):
/// <list type="bullet">
/// <item>unquoted whitespace separates arguments;</item>
/// <item><c>"…"</c> groups text (whitespace included); inside it <c>\"</c>, <c>\\</c> and <c>\'</c> are escapes;</item>
/// <item><c>'…'</c> groups text literally (no escapes);</item>
/// <item>outside quotes a backslash escapes the next <c>\</c>, <c>"</c>, <c>'</c> or whitespace character;</item>
/// <item>any other backslash is kept literally.</item>
/// </list>
/// <see cref="ExternalToolArgumentParser"/> with <see cref="ArgumentEscapingStyle.Posix"/> escapes variable values so
/// that they always come out of this tokenizer unchanged.
/// </summary>
public static class CommandLineTokenizer
{
    public static IReadOnlyList<string> Split(string? commandLine)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(commandLine))
            return result;

        var current = new StringBuilder();
        bool inArgument = false;
        char quote = '\0';

        for (int i = 0; i < commandLine.Length; i++)
        {
            char c = commandLine[i];
            char next = i + 1 < commandLine.Length ? commandLine[i + 1] : '\0';

            if (quote == '\'')
            {
                if (c == '\'')
                    quote = '\0';
                else
                    current.Append(c);
                continue;
            }

            if (quote == '"')
            {
                if (c == '\\' && next is '\\' or '"' or '\'')
                {
                    current.Append(next);
                    i++;
                }
                else if (c == '"')
                {
                    quote = '\0';
                }
                else
                {
                    current.Append(c);
                }
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                if (inArgument)
                {
                    result.Add(current.ToString());
                    current.Clear();
                    inArgument = false;
                }
                continue;
            }

            inArgument = true;
            if (c == '\\' && (next is '\\' or '"' or '\'' || (next != '\0' && char.IsWhiteSpace(next))))
            {
                current.Append(next);
                i++;
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else
            {
                current.Append(c);
            }
        }

        if (inArgument)
            result.Add(current.ToString());
        return result;
    }
}
