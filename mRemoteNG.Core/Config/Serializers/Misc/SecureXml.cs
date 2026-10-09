using System.Xml;

namespace mRemoteNG.Core.Config.Serializers.Misc
{
    /// <summary>Loads untrusted XML without DTD processing or external entity resolution.</summary>
    internal static class SecureXml
    {
        public static XmlDocument Load(string xml)
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null
            };
            var doc = new XmlDocument { XmlResolver = null };
            using var stringReader = new StringReader(xml);
            using var reader = XmlReader.Create(stringReader, settings);
            doc.Load(reader);
            return doc;
        }
    }
}
