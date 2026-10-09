using System.Globalization;
using System.Text;

namespace mRemoteNG.Core.Config.Import.ActiveDirectory
{
    /// <summary>How the importer binds to the directory.</summary>
    public enum LdapBindMode
    {
        /// <summary>Kerberos / NTLM; with no user name, the current Windows logon (single sign-on on domain members).</summary>
        Negotiate,

        /// <summary>Simple bind with a user name (DN, UPN or DOMAIN\user) and password; use with LDAPS on untrusted networks.</summary>
        Simple,

        Anonymous,
    }

    /// <summary>Where and how to connect to the directory.</summary>
    public sealed record LdapServerSettings
    {
        public const int LdapPort = 389;
        public const int LdapsPort = 636;

        /// <summary>Domain controller or domain name; empty for the current machine's domain (Windows only).</summary>
        public string Server { get; init; } = "";

        /// <summary>0 uses 389, or 636 with <see cref="UseSsl"/>.</summary>
        public int Port { get; init; }

        public bool UseSsl { get; init; }

        public LdapBindMode BindMode { get; init; } = LdapBindMode.Negotiate;

        public string? Username { get; init; }

        public string? Password { get; init; }

        public int EffectivePort => Port > 0 ? Port : UseSsl ? LdapsPort : LdapPort;
    }

    /// <summary>
    /// What to import: the computers below <see cref="BaseDn"/> (one level, or the whole subtree when
    /// <see cref="IncludeSubOus"/>), optionally only <see cref="SelectedComputers"/>, with OU folders when
    /// <see cref="CreateOuFolders"/>.
    /// </summary>
    public sealed record ActiveDirectoryImportRequest
    {
        public LdapServerSettings Server { get; init; } = new();

        /// <summary>Distinguished name of the OU, container or domain to import from.</summary>
        public string BaseDn { get; init; } = "";

        /// <summary>Also import computers in nested OUs (legacy "Import sub OUs").</summary>
        public bool IncludeSubOus { get; init; }

        /// <summary>Recreate nested OUs as folders; otherwise all computers go into one folder.</summary>
        public bool CreateOuFolders { get; init; } = true;

        /// <summary>Distinguished names of the computers to import; empty imports every computer found.</summary>
        public IReadOnlyList<string> SelectedComputers { get; init; } = [];

        /// <summary>
        /// The request as an RFC 4516-style LDAP URL, e.g.
        /// <c>ldap://dc1:389/OU%3DServers%2CDC%3Dcorp??sub??bindname=alice,x-bind=simple,x-folders=1</c>.
        /// The password is never included; pass it separately.
        /// </summary>
        public string ToUrl()
        {
            var url = new StringBuilder(Server.UseSsl ? "ldaps://" : "ldap://")
                .Append(Server.Server.Contains(':') ? $"[{Server.Server}]" : Server.Server);
            if (Server.Port > 0) url.Append(':').Append(Server.Port.ToString(CultureInfo.InvariantCulture));
            url.Append('/').Append(Uri.EscapeDataString(BaseDn))
                .Append("??").Append(IncludeSubOus ? "sub" : "one").Append("??");

            var extensions = new List<string>();
            if (!string.IsNullOrEmpty(Server.Username))
                extensions.Add("bindname=" + Uri.EscapeDataString(Server.Username));
            extensions.Add("x-bind=" + Server.BindMode.ToString().ToLowerInvariant());
            extensions.Add("x-folders=" + (CreateOuFolders ? "1" : "0"));
            extensions.AddRange(SelectedComputers.Select(dn => "x-dn=" + Uri.EscapeDataString(dn)));
            url.Append(string.Join(",", extensions));
            return url.ToString();
        }

        /// <summary>Parses <see cref="ToUrl"/>'s format; <paramref name="password"/> becomes the bind password.</summary>
        /// <exception cref="FormatException">Not an ldap:// or ldaps:// URL.</exception>
        public static ActiveDirectoryImportRequest Parse(string url, string? password = null)
        {
            ArgumentNullException.ThrowIfNull(url);
            var text = url.Trim();
            bool ssl;
            if (text.StartsWith("ldaps://", StringComparison.OrdinalIgnoreCase)) { ssl = true; text = text[8..]; }
            else if (text.StartsWith("ldap://", StringComparison.OrdinalIgnoreCase)) { ssl = false; text = text[7..]; }
            else throw new FormatException("An Active Directory source must be an ldap:// or ldaps:// URL.");

            var slash = text.IndexOf('/');
            var hostPort = slash < 0 ? text : text[..slash];
            var rest = slash < 0 ? "" : text[(slash + 1)..];

            string host;
            var port = 0;
            if (hostPort.StartsWith('['))
            {
                var close = hostPort.IndexOf(']');
                if (close < 0) throw new FormatException("Unterminated IPv6 address in the LDAP URL.");
                host = hostPort[1..close];
                if (hostPort.Length > close + 2 && hostPort[close + 1] == ':')
                    port = ParsePort(hostPort[(close + 2)..]);
            }
            else
            {
                var colon = hostPort.LastIndexOf(':');
                host = colon < 0 ? hostPort : hostPort[..colon];
                if (colon >= 0) port = ParsePort(hostPort[(colon + 1)..]);
            }

            var parts = rest.Split('?');
            string Part(int i) => parts.Length > i ? parts[i] : "";
            var baseDn = Uri.UnescapeDataString(Part(0));
            var includeSub = Part(2).Equals("sub", StringComparison.OrdinalIgnoreCase);

            string? user = null;
            var bind = LdapBindMode.Negotiate;
            var folders = true;
            var selected = new List<string>();
            foreach (var extension in Part(4).Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = extension.IndexOf('=');
                var key = (eq < 0 ? extension : extension[..eq]).TrimStart('!').ToLowerInvariant();
                var value = eq < 0 ? "" : Uri.UnescapeDataString(extension[(eq + 1)..]);
                switch (key)
                {
                    case "bindname": user = value; break;
                    case "x-bind": bind = Enum.TryParse<LdapBindMode>(value, ignoreCase: true, out var mode) ? mode : bind; break;
                    case "x-folders": folders = value != "0"; break;
                    case "x-dn": selected.Add(value); break;
                }
            }

            return new ActiveDirectoryImportRequest
            {
                Server = new LdapServerSettings
                {
                    Server = host,
                    Port = port,
                    UseSsl = ssl,
                    BindMode = bind,
                    Username = user,
                    Password = password,
                },
                BaseDn = baseDn,
                IncludeSubOus = includeSub,
                CreateOuFolders = folders,
                SelectedComputers = selected,
            };
        }

        private static int ParsePort(string text) =>
            int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is > 0 and <= 65535
                ? port
                : throw new FormatException($"\"{text}\" is not a valid LDAP port.");
    }
}
