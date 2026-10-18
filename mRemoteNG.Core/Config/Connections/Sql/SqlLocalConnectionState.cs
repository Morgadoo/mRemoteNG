using System.Text;
using System.Xml;
using System.Xml.Linq;
using mRemoteNG.Core.Config.DataProviders;
using mRemoteNG.Core.Config.Serializers.Sql;
using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Container;
using mRemoteNG.Core.Tree;
using FrameColor = mRemoteNG.Core.Connection.ConnectionFrameColor;
using VaultEngine = mRemoteNG.Core.Connection.VaultOpenbaoSecretEngine;

namespace mRemoteNG.Core.Config.Connections.Sql;

/// <summary>
/// Per-user state kept beside a shared SQL connection database.
/// <list type="bullet">
///   <item>
///     <c>LocalConnectionProperties.xml</c> is the file the WinForms app already writes
///     (connection id, whether the session was open, whether a folder is expanded, whether it is a
///     favourite). It is reapplied after every database load, so each person keeps their own tree
///     state, and a file written by either app can be read by the other.
///   </item>
///   <item>
///     <c>SqlConnectionProperties.xml</c> is the fallback for the connection properties the legacy
///     <c>tblCons</c> schema had no columns for (tab colour, frame colour, Vault/OpenBao, the RD
///     Gateway access token and the RDP redirection-server flag). Those now have shared columns
///     (<see cref="SqlSchema.SharedLocalFallbackColumns"/>); the file only keeps the values a database
///     without the columns (read-only, not upgraded) cannot store, and only fills in rows whose column
///     is NULL (written by a client that does not know it).
///   </item>
/// </list>
/// </summary>
public sealed class SqlLocalConnectionState
{
    public const string LocalPropertiesFileName = "LocalConnectionProperties.xml";
    public const string SchemaGapFileName = "SqlConnectionProperties.xml";

    private readonly FileDataProvider _localFile;
    private readonly FileDataProvider _schemaGapFile;

    public SqlLocalConnectionState(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("A directory is required.", nameof(directory));

        DirectoryPath = directory;
        _localFile = new FileDataProvider(Path.Combine(directory, LocalPropertiesFileName));
        _schemaGapFile = new FileDataProvider(Path.Combine(directory, SchemaGapFileName));
    }

    public string DirectoryPath { get; }

    public string LocalPropertiesPath => _localFile.FilePath;

    public string SchemaGapPath => _schemaGapFile.FilePath;

    /// <summary>
    /// Overlays the saved per-user state and the properties the database cannot store onto
    /// <paramref name="model"/>. Missing or unreadable files leave the tree as it was loaded.
    /// </summary>
    /// <param name="storedInDatabase">
    /// Per connection id, the shared columns the database held a value for; those values win over the file.
    /// Null applies every value in the file.
    /// </param>
    public void Apply(ConnectionTreeModel model, IReadOnlyDictionary<string, IReadOnlySet<string>>? storedInDatabase = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        var local = ReadLocal();
        var gap = ReadSchemaGap();
        if (local.Count == 0 && gap.Count == 0)
            return;

        foreach (var node in model.GetRecursiveChildList())
        {
            if (local.TryGetValue(node.ConstantID, out var ui))
            {
                node.PleaseConnect = ui.Connected;
                node.Favorite = ui.Favorite;
                if (node is ContainerInfo container)
                    container.IsExpanded = ui.Expanded;
            }

            if (gap.TryGetValue(node.ConstantID, out var extra))
            {
                IReadOnlySet<string>? stored = null;
                storedInDatabase?.TryGetValue(node.ConstantID, out stored);
                ApplySchemaGap(node, extra, column => stored?.Contains(column) != true);
            }
        }
    }

    /// <summary>Writes both files from <paramref name="model"/>, replacing whatever was there.</summary>
    /// <param name="databaseColumns">
    /// The columns of the database's <c>tblCons</c>; properties with a column there are not written to the
    /// fallback file. Null (or empty, before the first load) writes them all.
    /// </param>
    public void Save(ConnectionTreeModel model, IReadOnlySet<string>? databaseColumns = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        var nodes = model.GetRecursiveChildList().ToList();
        _localFile.Save(WriteLocal(nodes));
        Func<string, bool> keepLocally = databaseColumns is { Count: > 0 }
            ? column => !databaseColumns.Contains(column)
            : _ => true;
        _schemaGapFile.Save(WriteSchemaGap(nodes, keepLocally));
    }

    // ── LocalConnectionProperties.xml (WinForms compatible) ───────────────

    private Dictionary<string, LocalUiState> ReadLocal()
    {
        var result = new Dictionary<string, LocalUiState>(StringComparer.Ordinal);
        var document = ReadDocument(_localFile);
        if (document is null)
            return result;

        foreach (var element in document.Descendants("Node"))
        {
            var id = element.Attribute("ConnectionId")?.Value;
            if (string.IsNullOrEmpty(id))
                continue;

            result[id] = new LocalUiState(
                BoolAttribute(element, "Connected"),
                BoolAttribute(element, "Expanded"),
                BoolAttribute(element, "Favorite"));
        }

        return result;
    }

    private static string WriteLocal(IReadOnlyList<ConnectionInfo> nodes)
    {
        var root = new XElement("LocalConnections");
        foreach (var node in nodes)
        {
            root.Add(new XElement("Node",
                new XAttribute("ConnectionId", node.ConstantID),
                new XAttribute("Connected", node.PleaseConnect),
                new XAttribute("Expanded", node is ContainerInfo container && container.IsExpanded),
                new XAttribute("Favorite", node.Favorite)));
        }

        return ToXml(root);
    }

    // ── Properties the legacy schema cannot store ─────────────────────────

    private Dictionary<string, SchemaGapState> ReadSchemaGap()
    {
        var result = new Dictionary<string, SchemaGapState>(StringComparer.Ordinal);
        var document = ReadDocument(_schemaGapFile);
        if (document is null)
            return result;

        foreach (var element in document.Descendants("Node"))
        {
            var id = element.Attribute("ConnectionId")?.Value;
            if (!string.IsNullOrEmpty(id))
                result[id] = SchemaGapState.FromElement(element);
        }

        return result;
    }

    private static string WriteSchemaGap(IReadOnlyList<ConnectionInfo> nodes, Func<string, bool> include)
    {
        var root = new XElement("SqlConnectionProperties", new XAttribute("Version", "1"));
        foreach (var node in nodes)
        {
            var element = SchemaGapState.Capture(node, include).ToElement(node.ConstantID);
            if (element is not null)
                root.Add(element);
        }

        return ToXml(root);
    }

    private static void ApplySchemaGap(ConnectionInfo node, SchemaGapState extra, Func<string, bool> fromFile)
    {
        if (extra.TabColor is not null && fromFile("TabColor"))
            node.TabColor = extra.TabColor;
        if (extra.ConnectionFrameColor is not null && fromFile("ConnectionFrameColor"))
            node.ConnectionFrameColor = extra.ConnectionFrameColor.Value;
        if (extra.InheritTabColor && fromFile("InheritTabColor"))
            node.Inheritance.TabColor = true;
        if (extra.InheritConnectionFrameColor && fromFile("InheritConnectionFrameColor"))
            node.Inheritance.ConnectionFrameColor = true;
        if (extra.VaultOpenbaoMount is not null && fromFile("VaultOpenbaoMount"))
            node.VaultOpenbaoMount = extra.VaultOpenbaoMount;
        if (extra.VaultOpenbaoRole is not null && fromFile("VaultOpenbaoRole"))
            node.VaultOpenbaoRole = extra.VaultOpenbaoRole;
        if (extra.VaultOpenbaoSecretEngine is not null && fromFile("VaultOpenbaoSecretEngine"))
            node.VaultOpenbaoSecretEngine = extra.VaultOpenbaoSecretEngine.Value;
        if (extra.RDGatewayAccessToken is not null && fromFile("RDGatewayAccessToken"))
            node.RDGatewayAccessToken = extra.RDGatewayAccessToken;
        if (extra.UseRedirectionServerName && fromFile("UseRedirectionServerName"))
            node.UseRedirectionServerName = true;
        if (extra.InheritUseRedirectionServerName && fromFile("InheritUseRedirectionServerName"))
            node.Inheritance.UseRedirectionServerName = true;
    }

    // ── XML ────────────────────────────────────────────────────────────────

    private static XDocument? ReadDocument(FileDataProvider file)
    {
        if (!File.Exists(file.FilePath))
            return null;

        try
        {
            using var reader = XmlReader.Create(new StringReader(file.Load()), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
            });
            return XDocument.Load(reader);
        }
        catch (Exception ex) when (ex is XmlException or IOException)
        {
            return null;
        }
    }

    private static string ToXml(XElement root)
    {
        var document = new XDocument(new XDeclaration("1.0", "utf-8", null), root);
        using var stream = new MemoryStream();
        var settings = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "    ",
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };
        using (var writer = XmlWriter.Create(stream, settings))
            document.Save(writer);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static bool BoolAttribute(XElement element, string name) =>
        bool.TryParse(element.Attribute(name)?.Value, out var value) && value;

    private readonly record struct LocalUiState(bool Connected, bool Expanded, bool Favorite);

    private readonly record struct SchemaGapState(
        string? TabColor,
        FrameColor? ConnectionFrameColor,
        bool InheritTabColor,
        bool InheritConnectionFrameColor,
        string? VaultOpenbaoMount,
        string? VaultOpenbaoRole,
        VaultEngine? VaultOpenbaoSecretEngine,
        string? RDGatewayAccessToken,
        bool UseRedirectionServerName,
        bool InheritUseRedirectionServerName)
    {
        public static SchemaGapState Capture(ConnectionInfo node, Func<string, bool> include)
        {
            var inheritance = node.Inheritance;
            var inheritTab = inheritance.TabColor && include("InheritTabColor");
            var inheritFrame = inheritance.ConnectionFrameColor && include("InheritConnectionFrameColor");
            var inheritRedirection = inheritance.UseRedirectionServerName && include("InheritUseRedirectionServerName");
            inheritance.DisableInheritance();
            try
            {
                return new SchemaGapState(
                    include("TabColor") ? NullIfEmpty(node.TabColor) : null,
                    node.ConnectionFrameColor == FrameColor.None || !include("ConnectionFrameColor") ? null : node.ConnectionFrameColor,
                    inheritTab,
                    inheritFrame,
                    include("VaultOpenbaoMount") ? NullIfEmpty(node.VaultOpenbaoMount) : null,
                    include("VaultOpenbaoRole") ? NullIfEmpty(node.VaultOpenbaoRole) : null,
                    node.VaultOpenbaoSecretEngine == VaultEngine.Kv || !include("VaultOpenbaoSecretEngine") ? null : node.VaultOpenbaoSecretEngine,
                    include("RDGatewayAccessToken") ? NullIfEmpty(node.RDGatewayAccessToken) : null,
                    node.UseRedirectionServerName && include("UseRedirectionServerName"),
                    inheritRedirection);
            }
            finally
            {
                inheritance.EnableInheritance();
            }
        }

        public static SchemaGapState FromElement(XElement element)
        {
            FrameColor? frame = Enum.TryParse(element.Attribute("ConnectionFrameColor")?.Value, ignoreCase: true, out FrameColor parsedFrame)
                && Enum.IsDefined(parsedFrame) && parsedFrame != FrameColor.None
                ? parsedFrame
                : null;
            VaultEngine? engine = Enum.TryParse(element.Attribute("VaultOpenbaoSecretEngine")?.Value, ignoreCase: true, out VaultEngine parsedEngine)
                && Enum.IsDefined(parsedEngine) && parsedEngine != VaultEngine.Kv
                ? parsedEngine
                : null;

            return new SchemaGapState(
                NullIfEmpty(element.Attribute("TabColor")?.Value),
                frame,
                BoolAttribute(element, "InheritTabColor"),
                BoolAttribute(element, "InheritConnectionFrameColor"),
                NullIfEmpty(element.Attribute("VaultOpenbaoMount")?.Value),
                NullIfEmpty(element.Attribute("VaultOpenbaoRole")?.Value),
                engine,
                NullIfEmpty(element.Attribute("RDGatewayAccessToken")?.Value),
                BoolAttribute(element, "UseRedirectionServerName"),
                BoolAttribute(element, "InheritUseRedirectionServerName"));
        }

        public XElement? ToElement(string connectionId)
        {
            if (IsEmpty)
                return null;

            var element = new XElement("Node", new XAttribute("ConnectionId", connectionId));
            if (TabColor is not null)
                element.Add(new XAttribute("TabColor", TabColor));
            if (ConnectionFrameColor is not null)
                element.Add(new XAttribute("ConnectionFrameColor", ConnectionFrameColor.Value.ToString()));
            if (InheritTabColor)
                element.Add(new XAttribute("InheritTabColor", true));
            if (InheritConnectionFrameColor)
                element.Add(new XAttribute("InheritConnectionFrameColor", true));
            if (VaultOpenbaoMount is not null)
                element.Add(new XAttribute("VaultOpenbaoMount", VaultOpenbaoMount));
            if (VaultOpenbaoRole is not null)
                element.Add(new XAttribute("VaultOpenbaoRole", VaultOpenbaoRole));
            if (VaultOpenbaoSecretEngine is not null)
                element.Add(new XAttribute("VaultOpenbaoSecretEngine", VaultOpenbaoSecretEngine.Value.ToString()));
            if (RDGatewayAccessToken is not null)
                element.Add(new XAttribute("RDGatewayAccessToken", RDGatewayAccessToken));
            if (UseRedirectionServerName)
                element.Add(new XAttribute("UseRedirectionServerName", true));
            if (InheritUseRedirectionServerName)
                element.Add(new XAttribute("InheritUseRedirectionServerName", true));
            return element;
        }

        private bool IsEmpty =>
            TabColor is null && ConnectionFrameColor is null && !InheritTabColor && !InheritConnectionFrameColor
            && VaultOpenbaoMount is null && VaultOpenbaoRole is null && VaultOpenbaoSecretEngine is null
            && RDGatewayAccessToken is null && !UseRedirectionServerName && !InheritUseRedirectionServerName;

        private static string? NullIfEmpty(string? value) =>
            string.IsNullOrEmpty(value) ? null : value;
    }
}
