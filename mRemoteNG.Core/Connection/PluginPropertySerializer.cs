using System.Text.Encodings.Web;
using System.Text.Json;

namespace mRemoteNG.Core.Connection
{
    /// <summary>
    /// The <c>PluginData</c> format of the legacy app: a flat JSON object of string values
    /// (<c>PluginConnectionDataSerializer</c>), stored in XML, CSV and SQL.
    /// </summary>
    public static class PluginPropertySerializer
    {
        private static readonly JsonSerializerOptions Options = new()
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        public static string Serialize(IReadOnlyDictionary<string, string> properties)
        {
            if (properties.Count == 0)
                return string.Empty;
            var ordered = properties.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(p => p.Key, p => p.Value);
            return JsonSerializer.Serialize(ordered, Options);
        }

        /// <summary>Returns no entries for empty or malformed data rather than failing the whole load.</summary>
        public static IReadOnlyDictionary<string, string> Deserialize(string? serialized)
        {
            if (string.IsNullOrWhiteSpace(serialized))
                return new Dictionary<string, string>();

            try
            {
                using var document = JsonDocument.Parse(serialized);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    return new Dictionary<string, string>();

                var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    result[property.Name] = property.Value.ValueKind switch
                    {
                        JsonValueKind.String => property.Value.GetString() ?? string.Empty,
                        JsonValueKind.Null => string.Empty,
                        _ => property.Value.GetRawText(),
                    };
                }
                return result;
            }
            catch (JsonException)
            {
                return new Dictionary<string, string>();
            }
        }
    }
}
