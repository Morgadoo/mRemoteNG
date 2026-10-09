using System.Globalization;
using System.Text;
using System.Xml;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace mRemoteNG.Core.Tools;

/// <summary>
/// Reads and writes <c>extApps.xml</c>, the external tools file of the legacy app, in the same format:
/// <code>
/// &lt;?xml version="1.0" encoding="utf-8"?&gt;
/// &lt;Apps&gt;
///     &lt;App DisplayName="…" FileName="…" Arguments="…" WorkingDir="…" WaitForExit="False" TryToIntegrate="False" RunElevated="False" ShowOnToolbar="True" /&gt;
/// &lt;/Apps&gt;
/// </code>
/// Files are written byte-for-byte like the Windows app writes them (UTF-8 with BOM, CRLF, four-space indent), so a
/// file can move between the legacy app and this one. Two optional attributes are added only when set, and the
/// legacy loader ignores them: <c>Platform</c> (<see cref="ExternalToolPlatform"/>) and <c>Icon</c> (an image path).
/// </summary>
public sealed class ExternalToolsRepository
{
    public const string FileName = "extApps.xml";

    private readonly ILogger _logger;

    public ExternalToolsRepository(string filePath, ILogger<ExternalToolsRepository>? logger = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        FilePath = filePath;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    public string FilePath { get; }

    public bool Exists => File.Exists(FilePath);

    /// <summary>
    /// Loads the tools from <see cref="FilePath"/>; returns null when the file does not exist.
    /// </summary>
    /// <exception cref="XmlException">The file is not well-formed or its root element is not &lt;Apps&gt;.</exception>
    public List<ExternalTool>? Load()
    {
        if (!File.Exists(FilePath))
            return null;
        using var stream = File.OpenRead(FilePath);
        var tools = Read(stream);
        _logger.LogInformation("Loaded {Count} external tools from {Path}", tools.Count, FilePath);
        return tools;
    }

    /// <summary>Writes the tools to <see cref="FilePath"/> (creating the directory), via a temporary file.</summary>
    public void Save(IEnumerable<ExternalTool> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        string? directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        string temp = FilePath + ".tmp";
        using (var stream = File.Create(temp))
            Write(stream, tools);
        File.Move(temp, FilePath, overwrite: true);
        _logger.LogInformation("Saved external tools to {Path}", FilePath);
    }

    public static List<ExternalTool> Read(Stream stream)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
        };
        var document = new XmlDocument { XmlResolver = null };
        using (var reader = XmlReader.Create(stream, settings))
            document.Load(reader);

        var root = document.DocumentElement;
        if (root is null || root.Name != "Apps")
            throw new XmlException("Not an external tools file: the root element must be <Apps>.");

        var tools = new List<ExternalTool>();
        foreach (var element in root.ChildNodes.OfType<XmlElement>())
        {
            var tool = new ExternalTool
            {
                DisplayName = element.GetAttribute("DisplayName"),
                FileName = element.GetAttribute("FileName"),
                Arguments = element.GetAttribute("Arguments"),
                WorkingDir = element.GetAttribute("WorkingDir"),
            };

            // Older files lack some attributes; same order as the legacy loader (WaitForExit before TryToIntegrate).
            if (TryBool(element, "RunElevated", out bool runElevated))
                tool.RunElevated = runElevated;
            if (TryBool(element, "WaitForExit", out bool waitForExit))
                tool.WaitForExit = waitForExit;
            if (TryBool(element, "TryToIntegrate", out bool tryIntegrate))
                tool.TryIntegrate = tryIntegrate;
            if (TryBool(element, "ShowOnToolbar", out bool showOnToolbar))
                tool.ShowOnToolbar = showOnToolbar;
            if (element.HasAttribute("Platform")
                && Enum.TryParse(element.GetAttribute("Platform"), ignoreCase: true, out ExternalToolPlatform platform))
                tool.Platform = platform;
            tool.IconPath = element.GetAttribute("Icon");

            tools.Add(tool);
        }
        return tools;
    }

    public static void Write(Stream stream, IEnumerable<ExternalTool> tools)
    {
        // The legacy ExternalAppsSaver's XmlTextWriter settings (its escaping differs from XmlWriter.Create's, e.g.
        // tabs in attributes), with the line ending Windows gives it.
        using var text = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), leaveOpen: true)
        {
            NewLine = "\r\n",
        };
        var writer = new XmlTextWriter(text) { Formatting = Formatting.Indented, Indentation = 4 };
        writer.WriteStartDocument();
        writer.WriteStartElement("Apps");
        foreach (var tool in tools)
        {
            writer.WriteStartElement("App");
            writer.WriteAttributeString("DisplayName", tool.DisplayName);
            writer.WriteAttributeString("FileName", tool.FileName);
            writer.WriteAttributeString("Arguments", tool.Arguments);
            writer.WriteAttributeString("WorkingDir", tool.WorkingDir);
            writer.WriteAttributeString("WaitForExit", Bool(tool.WaitForExit));
            writer.WriteAttributeString("TryToIntegrate", Bool(tool.TryIntegrate));
            writer.WriteAttributeString("RunElevated", Bool(tool.RunElevated));
            writer.WriteAttributeString("ShowOnToolbar", Bool(tool.ShowOnToolbar));
            if (tool.Platform != ExternalToolPlatform.Any)
                writer.WriteAttributeString("Platform", tool.Platform.ToString());
            if (!string.IsNullOrEmpty(tool.IconPath))
                writer.WriteAttributeString("Icon", tool.IconPath);
            writer.WriteEndElement();
        }
        writer.WriteEndElement();
        writer.WriteEndDocument();
        writer.Flush();
    }

    // Convert.ToString(bool) in the legacy saver: "True"/"False".
    private static string Bool(bool value) => value.ToString(CultureInfo.InvariantCulture);

    private static bool TryBool(XmlElement element, string name, out bool value)
    {
        value = false;
        return element.HasAttribute(name) && bool.TryParse(element.GetAttribute(name).Trim(), out value);
    }
}
