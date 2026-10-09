using System.DirectoryServices.Protocols;
using System.Net;

namespace mRemoteNG.Core.Config.Import.ActiveDirectory
{
    /// <summary>An OU, container or domain that can hold computers.</summary>
    public sealed record DirectoryContainer(string DistinguishedName, string Name, bool IsOrganizationalUnit);

    /// <summary>A computer account.</summary>
    public sealed record DirectoryComputer(
        string DistinguishedName,
        string Name,
        string? DnsHostName,
        string? Description,
        string? OperatingSystem)
    {
        /// <summary>What to connect to: the DNS name when the directory has one, otherwise the computer name.</summary>
        public string HostName => string.IsNullOrWhiteSpace(DnsHostName) ? Name : DnsHostName;
    }

    /// <summary>
    /// Browses Active Directory (or any LDAP directory with AD-style computer objects) with
    /// System.DirectoryServices.Protocols, which is wldap32 on Windows and OpenLDAP's libldap elsewhere.
    /// </summary>
    public sealed class ActiveDirectoryBrowser : IDirectoryComputerSource
    {
        private const int PageSize = 500;
        private static readonly string[] ComputerAttributes = ["cn", "name", "dNSHostName", "description", "operatingSystem"];

        private readonly LdapConnection _connection;
        private bool _bound;

        public ActiveDirectoryBrowser(LdapServerSettings settings)
        {
            ArgumentNullException.ThrowIfNull(settings);
            Settings = settings;

            var identifier = string.IsNullOrWhiteSpace(settings.Server)
                ? new LdapDirectoryIdentifier((string?)null, settings.EffectivePort)
                : new LdapDirectoryIdentifier(settings.Server.Trim(), settings.EffectivePort);
            var authType = settings.BindMode switch
            {
                LdapBindMode.Simple => AuthType.Basic,
                LdapBindMode.Anonymous => AuthType.Anonymous,
                _ => AuthType.Negotiate,
            };
            _connection = new LdapConnection(identifier) { AuthType = authType, Timeout = TimeSpan.FromSeconds(30) };
            if (authType != AuthType.Anonymous && !string.IsNullOrEmpty(settings.Username))
                _connection.Credential = CreateCredential(settings.Username, settings.Password);
            _connection.SessionOptions.ProtocolVersion = 3;
            // AD returns referrals to other partitions; following them needs the same credentials and is slow.
            _connection.SessionOptions.ReferralChasing = ReferralChasingOptions.None;
            if (settings.UseSsl)
                _connection.SessionOptions.SecureSocketLayer = true;
        }

        public LdapServerSettings Settings { get; }

        /// <summary>DOMAIN\user becomes a domain-qualified credential for Negotiate; anything else is passed as is.</summary>
        private static NetworkCredential CreateCredential(string username, string? password)
        {
            var backslash = username.IndexOf('\\');
            return backslash > 0
                ? new NetworkCredential(username[(backslash + 1)..], password, username[..backslash])
                : new NetworkCredential(username, password);
        }

        /// <summary>Binds with the configured credentials (done automatically by the other methods).</summary>
        /// <exception cref="LdapException">The server is unreachable or rejected the credentials.</exception>
        public void Bind()
        {
            if (_bound) return;
            _connection.Bind();
            _bound = true;
        }

        /// <summary>The domain's root DN from RootDSE (defaultNamingContext, or the first naming context).</summary>
        public string GetDefaultNamingContext()
        {
            Bind();
            var response = (SearchResponse)_connection.SendRequest(
                new SearchRequest("", "(objectClass=*)", SearchScope.Base, "defaultNamingContext", "namingContexts"));
            var entry = response.Entries.Cast<SearchResultEntry>().FirstOrDefault()
                ?? throw new InvalidOperationException("The directory did not return its root entry (RootDSE).");
            return First(entry, "defaultNamingContext") ?? First(entry, "namingContexts")
                ?? throw new InvalidOperationException("The directory does not publish a naming context.");
        }

        /// <summary>OUs and containers directly below <paramref name="dn"/>, sorted by name.</summary>
        public IReadOnlyList<DirectoryContainer> GetChildContainers(string dn)
        {
            const string filter = "(|(objectClass=organizationalUnit)(objectClass=container)(objectClass=builtinDomain))";
            return Search(dn, filter, SearchScope.OneLevel, ["ou", "cn", "name", "objectClass"])
                .Select(e =>
                {
                    var isOu = Values(e, "objectClass").Any(v => v.Equals("organizationalUnit", StringComparison.OrdinalIgnoreCase));
                    var name = First(e, "ou") ?? First(e, "name") ?? First(e, "cn") ?? RdnValue(e.DistinguishedName);
                    return new DirectoryContainer(e.DistinguishedName, name, isOu);
                })
                .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        /// <summary>Computer accounts below <paramref name="dn"/> (one level or the whole subtree), sorted by name.</summary>
        public IReadOnlyList<DirectoryComputer> GetComputers(string dn, bool includeSubtree)
        {
            return Search(dn, "(objectClass=computer)", includeSubtree ? SearchScope.Subtree : SearchScope.OneLevel, ComputerAttributes)
                .Select(e => new DirectoryComputer(
                    e.DistinguishedName,
                    First(e, "cn") ?? First(e, "name") ?? RdnValue(e.DistinguishedName),
                    First(e, "dNSHostName"),
                    First(e, "description"),
                    First(e, "operatingSystem")))
                .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        /// <summary>A paged search (AD returns at most 1000 entries per page otherwise).</summary>
        private List<SearchResultEntry> Search(string baseDn, string filter, SearchScope scope, string[] attributes)
        {
            Bind();
            var results = new List<SearchResultEntry>();
            var paging = new PageResultRequestControl(PageSize);
            var request = new SearchRequest(baseDn, filter, scope, attributes);
            request.Controls.Add(paging);
            while (true)
            {
                var response = (SearchResponse)_connection.SendRequest(request);
                results.AddRange(response.Entries.Cast<SearchResultEntry>());
                var page = response.Controls.OfType<PageResultResponseControl>().FirstOrDefault();
                if (page is null || page.Cookie.Length == 0) break;
                paging.Cookie = page.Cookie;
            }
            return results;
        }

        private static IEnumerable<string> Values(SearchResultEntry entry, string attribute)
        {
            var values = entry.Attributes[attribute];
            if (values is null) yield break;
            foreach (var value in values.GetValues(typeof(string)))
                yield return (string)value;
        }

        private static string? First(SearchResultEntry entry, string attribute) =>
            Values(entry, attribute).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

        /// <summary>The value of the first RDN: "CN=WEB01,OU=x" → "WEB01".</summary>
        internal static string RdnValue(string dn)
        {
            var first = SplitDn(dn).FirstOrDefault() ?? dn;
            var eq = first.IndexOf('=');
            return eq < 0 ? first : first[(eq + 1)..].Replace("\\,", ",").Trim();
        }

        /// <summary>Splits a DN into RDNs, honouring backslash-escaped commas.</summary>
        internal static IReadOnlyList<string> SplitDn(string dn)
        {
            var parts = new List<string>();
            var start = 0;
            for (var i = 0; i < dn.Length; i++)
            {
                if (dn[i] == '\\') { i++; continue; }
                if (dn[i] != ',') continue;
                parts.Add(dn[start..i].Trim());
                start = i + 1;
            }
            if (start < dn.Length) parts.Add(dn[start..].Trim());
            return parts;
        }

        public void Dispose() => _connection.Dispose();
    }
}
