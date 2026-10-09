using System.Collections.ObjectModel;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using mRemoteNG.Platform.Security;
using mRemoteNG.Platform.Settings;

namespace mRemoteNG.Core.Credential
{
    /// <summary>
    /// Credential store backed by an XML file in the settings directory:
    /// <code>
    /// &lt;Credentials version="1"&gt;
    ///   &lt;Credential id="…" title="…" username="…" domain="…" password="AESGCM:…" /&gt;
    /// &lt;/Credentials&gt;
    /// </code>
    /// Passwords are encrypted with the platform <see cref="ICryptoProvider"/> (DPAPI on Windows,
    /// AES-256-GCM with a 0600 key file on Linux/macOS). The file is written atomically with mode 0600.
    /// Passwords this platform cannot decrypt (e.g. a DPAPI blob copied to Linux) are kept as-is
    /// so a save never destroys them; the record shows an empty password until it is re-entered.
    /// </summary>
    public sealed class FileCredentialRepository : ICredentialRepository, ICredentialLookup
    {
        public const string DefaultFileName = "credentials.xml";
        private const int FormatVersion = 1;

        private readonly ICryptoProvider _crypto;
        private readonly ILogger _logger;
        private readonly object _sync = new();
        private readonly ObservableCollection<ICredentialRecord> _records = [];
        private readonly Dictionary<Guid, string> _undecryptablePasswords = [];

        public FileCredentialRepository(string filePath, ICryptoProvider crypto, ILogger? logger = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
            FilePath = Path.GetFullPath(filePath);
            _crypto = crypto ?? throw new ArgumentNullException(nameof(crypto));
            _logger = logger ?? NullLogger.Instance;
            CredentialRecords = new ReadOnlyObservableCollection<ICredentialRecord>(_records);
        }

        public string FilePath { get; }

        public string Title { get; set; } = "Local credentials";

        public bool IsLoaded { get; private set; }

        public ReadOnlyObservableCollection<ICredentialRecord> CredentialRecords { get; }

        /// <summary>Ids of records whose stored password could not be decrypted on this machine.</summary>
        public IReadOnlyCollection<Guid> UndecryptableRecordIds
        {
            get
            {
                lock (_sync)
                    return _undecryptablePasswords.Keys.ToList();
            }
        }

        /// <summary>Raised after the stored records changed (load, save or replace).</summary>
        public event EventHandler? Changed;

        public void LoadCredentials()
        {
            lock (_sync)
            {
                _records.Clear();
                _undecryptablePasswords.Clear();
                IsLoaded = true;

                if (!File.Exists(FilePath))
                    return;

                try
                {
                    foreach (var record in ReadFile())
                        _records.Add(record);
                }
                catch (Exception ex) when (ex is XmlException or InvalidDataException or FormatException)
                {
                    _records.Clear();
                    _undecryptablePasswords.Clear();
                    var backup = $"{FilePath}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}";
                    try
                    {
                        File.Move(FilePath, backup);
                    }
                    catch (Exception moveEx) when (moveEx is IOException or UnauthorizedAccessException)
                    {
                        backup = "(backup failed)";
                    }

                    _logger.LogError(ex, "Credential file {Path} is corrupt; it was moved to {Backup}", FilePath, backup);
                }
            }

            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void SaveCredentials()
        {
            lock (_sync)
            {
                var root = new XElement("Credentials", new XAttribute("version", FormatVersion));
                foreach (var record in _records)
                {
                    root.Add(new XElement("Credential",
                        new XAttribute("id", record.Id.ToString("D")),
                        new XAttribute("title", record.Title ?? string.Empty),
                        new XAttribute("username", record.Username ?? string.Empty),
                        new XAttribute("domain", record.Domain ?? string.Empty),
                        new XAttribute("password", EncryptPassword(record))));
                }

                var document = new XDocument(new XDeclaration("1.0", "utf-8", null), root);
                AtomicFile.WriteAllText(FilePath, $"{document.Declaration}\n{document}\n", AtomicFile.OwnerOnly);
                IsLoaded = true;
            }

            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void AddCredential(ICredentialRecord credential)
        {
            ArgumentNullException.ThrowIfNull(credential);
            lock (_sync)
            {
                if (_records.Any(r => r.Id == credential.Id))
                    throw new ArgumentException($"A credential with id {credential.Id} already exists.", nameof(credential));
                _records.Add(credential);
            }
        }

        public void RemoveCredential(ICredentialRecord credential)
        {
            ArgumentNullException.ThrowIfNull(credential);
            lock (_sync)
            {
                var existing = _records.FirstOrDefault(r => r.Id == credential.Id);
                if (existing is not null)
                    _records.Remove(existing);
                _undecryptablePasswords.Remove(credential.Id);
            }
        }

        /// <summary>
        /// Replaces the whole set (as edited in the credential manager) and saves it.
        /// Records are copied, so later edits to <paramref name="records"/> do not leak in.
        /// </summary>
        public void ReplaceAllAndSave(IEnumerable<ICredentialRecord> records)
        {
            ArgumentNullException.ThrowIfNull(records);
            var copies = records.Select(Copy).ToList();
            if (copies.GroupBy(r => r.Id).Any(g => g.Count() > 1))
                throw new ArgumentException("Credential ids must be unique.", nameof(records));

            lock (_sync)
            {
                _records.Clear();
                foreach (var record in copies)
                    _records.Add(record);

                foreach (var id in _undecryptablePasswords.Keys.ToList())
                {
                    if (copies.All(r => r.Id != id))
                        _undecryptablePasswords.Remove(id);
                }
            }

            SaveCredentials();
        }

        public ICredentialRecord? GetCredentialRecord(Guid id)
        {
            lock (_sync)
                return _records.FirstOrDefault(r => r.Id == id);
        }

        public ICredentialRecord? FindByTitle(string title)
        {
            lock (_sync)
                return _records.FirstOrDefault(r => string.Equals(r.Title, title, StringComparison.OrdinalIgnoreCase));
        }

        public IReadOnlyList<ICredentialRecord> GetAll()
        {
            lock (_sync)
                return _records.ToList();
        }

        private static CredentialRecord Copy(ICredentialRecord source) => new(source.Id)
        {
            Title = source.Title ?? string.Empty,
            Username = source.Username ?? string.Empty,
            Password = source.Password ?? string.Empty,
            Domain = source.Domain ?? string.Empty,
        };

        private List<CredentialRecord> ReadFile()
        {
            var readerSettings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            XDocument document;
            using (var reader = XmlReader.Create(FilePath, readerSettings))
                document = XDocument.Load(reader);

            if (document.Root?.Name.LocalName != "Credentials")
                throw new InvalidDataException("Not a credential file.");

            var records = new List<CredentialRecord>();
            foreach (var element in document.Root.Elements("Credential"))
            {
                var id = Guid.Parse((string?)element.Attribute("id") ?? throw new InvalidDataException("Credential without id."));
                if (records.Any(r => r.Id == id))
                {
                    _logger.LogWarning("Skipping duplicate credential id {Id}", id);
                    continue;
                }

                var record = new CredentialRecord(id)
                {
                    Title = (string?)element.Attribute("title") ?? string.Empty,
                    Username = (string?)element.Attribute("username") ?? string.Empty,
                    Domain = (string?)element.Attribute("domain") ?? string.Empty,
                    Password = DecryptPassword(id, (string?)element.Attribute("password") ?? string.Empty),
                };
                records.Add(record);
            }

            return records;
        }

        private string DecryptPassword(Guid id, string stored)
        {
            if (stored.Length == 0)
                return string.Empty;

            if (_crypto.CanDecrypt(stored))
            {
                try
                {
                    return _crypto.Unprotect(stored);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Could not decrypt the password of credential {Id}", id);
                }
            }
            else
            {
                _logger.LogWarning("The password of credential {Id} was encrypted on another platform and cannot be decrypted here", id);
            }

            _undecryptablePasswords[id] = stored;
            return string.Empty;
        }

        private string EncryptPassword(ICredentialRecord record)
        {
            var password = record.Password ?? string.Empty;

            // Keep a password we could not decrypt unless the user typed a new one.
            if (password.Length == 0 && _undecryptablePasswords.TryGetValue(record.Id, out var original))
                return original;

            _undecryptablePasswords.Remove(record.Id);
            return password.Length == 0 ? string.Empty : _crypto.Protect(password);
        }
    }
}
