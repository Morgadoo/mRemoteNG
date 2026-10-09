using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace mRemoteNG.Core.Config.Serializers.Misc
{
    /// <summary>A concrete host alias from an OpenSSH client configuration.</summary>
    public sealed record OpenSshHostEntry(string Alias, string HostName, int Port, string User);

    /// <summary>
    /// Parses OpenSSH client configuration (<c>~/.ssh/config</c>) into one entry per concrete
    /// <c>Host</c> alias. Aliases containing wildcards or negations are not hosts and are skipped, but
    /// their blocks (like <c>Host *</c>) still supply values to matching aliases, with ssh's
    /// "first value wins" rule. Both <c>Keyword value</c> and <c>Keyword=value</c> forms, quoted
    /// arguments, <c>Include</c> (relative to <c>~/.ssh</c>, with wildcards) and <c>Match</c> blocks
    /// (ignored) are handled. Only HostName (with <c>%h</c>), Port and User are read.
    /// </summary>
    public static class OpenSshConfigParser
    {
        private const int MaxIncludeDepth = 16;

        private sealed class Block
        {
            public required List<string> Patterns { get; init; }
            public bool IsMatchBlock { get; init; }
            public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>Parses a configuration file and the files it includes.</summary>
        public static IReadOnlyList<OpenSshHostEntry> ParseFile(string path, ICollection<string>? warnings = null)
        {
            var fullPath = Path.GetFullPath(path);
            var sshDirectory = Path.GetDirectoryName(fullPath) ?? ".";
            var lines = new List<string>();
            ReadLines(fullPath, sshDirectory, lines, warnings, 0);
            return Parse(lines);
        }

        /// <summary>Parses configuration text. <c>Include</c> directives are resolved against <paramref name="sshDirectory"/>.</summary>
        public static IReadOnlyList<OpenSshHostEntry> Parse(string content, string? sshDirectory = null, ICollection<string>? warnings = null)
        {
            var lines = new List<string>();
            ExpandIncludes(SplitLines(content), sshDirectory ?? DefaultSshDirectory, lines, warnings, 0);
            return Parse(lines);
        }

        public static string DefaultSshDirectory =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");

        private static IReadOnlyList<OpenSshHostEntry> Parse(List<string> lines)
        {
            // Lines before the first Host/Match apply to every host.
            var blocks = new List<Block> { new() { Patterns = ["*"] } };
            var aliases = new List<string>();
            var seenAliases = new HashSet<string>(StringComparer.Ordinal);

            foreach (var line in lines)
            {
                if (!TrySplitDirective(line, out var keyword, out var arguments))
                    continue;

                if (keyword.Equals("Host", StringComparison.OrdinalIgnoreCase))
                {
                    var patterns = Tokenize(arguments);
                    blocks.Add(new Block { Patterns = patterns });
                    foreach (var pattern in patterns)
                    {
                        if (IsConcreteAlias(pattern) && seenAliases.Add(pattern))
                            aliases.Add(pattern);
                    }
                }
                else if (keyword.Equals("Match", StringComparison.OrdinalIgnoreCase))
                {
                    blocks.Add(new Block { Patterns = [], IsMatchBlock = true });
                }
                else
                {
                    var values = blocks[^1].Values;
                    if (!values.ContainsKey(keyword))
                        values[keyword] = Tokenize(arguments).FirstOrDefault() ?? "";
                }
            }

            var entries = new List<OpenSshHostEntry>();
            foreach (var alias in aliases)
            {
                string? hostName = null, user = null, port = null;
                foreach (var block in blocks)
                {
                    if (block.IsMatchBlock || !Matches(alias, block.Patterns))
                        continue;
                    hostName ??= block.Values.GetValueOrDefault("HostName");
                    user ??= block.Values.GetValueOrDefault("User");
                    port ??= block.Values.GetValueOrDefault("Port");
                }

                var resolvedHost = string.IsNullOrEmpty(hostName)
                    ? alias
                    : hostName.Replace("%h", alias).Replace("%%", "%");
                var resolvedPort = int.TryParse(port, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) && p is > 0 and <= 65535
                    ? p
                    : 22;
                entries.Add(new OpenSshHostEntry(alias, resolvedHost, resolvedPort, user ?? ""));
            }

            return entries;
        }

        private static void ReadLines(string path, string sshDirectory, List<string> output, ICollection<string>? warnings, int depth)
        {
            string content;
            try
            {
                content = File.ReadAllText(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings?.Add($"Could not read \"{path}\": {ex.Message}");
                return;
            }
            ExpandIncludes(SplitLines(content), sshDirectory, output, warnings, depth);
        }

        private static void ExpandIncludes(IEnumerable<string> lines, string sshDirectory, List<string> output, ICollection<string>? warnings, int depth)
        {
            foreach (var line in lines)
            {
                if (!TrySplitDirective(line, out var keyword, out var arguments)
                    || !keyword.Equals("Include", StringComparison.OrdinalIgnoreCase))
                {
                    output.Add(line);
                    continue;
                }

                if (depth >= MaxIncludeDepth)
                {
                    warnings?.Add("Include nesting is too deep; remaining includes were ignored.");
                    continue;
                }

                foreach (var pattern in Tokenize(arguments))
                {
                    foreach (var file in ResolveInclude(pattern, sshDirectory))
                        ReadLines(file, sshDirectory, output, warnings, depth + 1);
                }
            }
        }

        private static IEnumerable<string> ResolveInclude(string pattern, string sshDirectory)
        {
            var expanded = pattern.StartsWith("~/", StringComparison.Ordinal)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), pattern[2..])
                : pattern;
            if (!Path.IsPathRooted(expanded))
                expanded = Path.Combine(sshDirectory, expanded);

            var directory = Path.GetDirectoryName(expanded);
            var fileName = Path.GetFileName(expanded);
            if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(fileName) || !Directory.Exists(directory))
                return [];

            if (fileName.IndexOfAny(['*', '?']) < 0)
                return File.Exists(expanded) ? [expanded] : [];

            return Directory.EnumerateFiles(directory, fileName).Order(StringComparer.Ordinal).ToList();
        }

        private static IEnumerable<string> SplitLines(string content) =>
            content.TrimStart('﻿').Split(["\r\n", "\r", "\n"], StringSplitOptions.None);

        private static bool TrySplitDirective(string line, out string keyword, out string arguments)
        {
            keyword = arguments = "";
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] == '#')
                return false;

            var end = 0;
            while (end < trimmed.Length && !char.IsWhiteSpace(trimmed[end]) && trimmed[end] != '=')
                end++;
            keyword = trimmed[..end];

            var rest = trimmed[end..].TrimStart();
            if (rest.StartsWith('='))
                rest = rest[1..].TrimStart();
            arguments = rest;
            return keyword.Length > 0;
        }

        /// <summary>Splits arguments on whitespace, honouring double quotes.</summary>
        private static List<string> Tokenize(string arguments)
        {
            var tokens = new List<string>();
            var current = new StringBuilder();
            var inQuotes = false;
            var hasToken = false;
            foreach (var c in arguments)
            {
                if (c == '"')
                {
                    inQuotes = !inQuotes;
                    hasToken = true;
                }
                else if (char.IsWhiteSpace(c) && !inQuotes)
                {
                    if (hasToken)
                        tokens.Add(current.ToString());
                    current.Clear();
                    hasToken = false;
                }
                else if (c == '#' && !inQuotes && !hasToken)
                {
                    break;
                }
                else
                {
                    current.Append(c);
                    hasToken = true;
                }
            }
            if (hasToken)
                tokens.Add(current.ToString());
            return tokens;
        }

        private static bool IsConcreteAlias(string pattern) =>
            pattern.Length > 0 && pattern.IndexOfAny(['*', '?', '!']) < 0;

        /// <summary>ssh semantics: a block applies when any positive pattern matches and no negated one does.</summary>
        private static bool Matches(string alias, List<string> patterns)
        {
            var matched = false;
            foreach (var pattern in patterns)
            {
                if (pattern.StartsWith('!'))
                {
                    if (GlobMatches(alias, pattern[1..]))
                        return false;
                }
                else if (GlobMatches(alias, pattern))
                {
                    matched = true;
                }
            }
            return matched;
        }

        private static bool GlobMatches(string text, string pattern)
        {
            var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            return Regex.IsMatch(text, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
    }
}
