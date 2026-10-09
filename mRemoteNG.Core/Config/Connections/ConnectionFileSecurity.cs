using mRemoteNG.Core.Config.Serializers.Xml;
using mRemoteNG.Core.Security;

namespace mRemoteNG.Core.Config.Connections
{
    /// <summary>What to do with the connection file's master password.</summary>
    public enum MasterPasswordAction
    {
        /// <summary>Keep the current password (or keep having none).</summary>
        Keep,
        /// <summary>Set a new password (also when there was none).</summary>
        Set,
        /// <summary>Remove the password; the file is encrypted with the default key again.</summary>
        Remove,
    }

    /// <summary>The security settings requested in File ▸ Properties.</summary>
    public sealed record ConnectionFileSecurityChange
    {
        public MasterPasswordAction PasswordAction { get; init; }

        /// <summary>The current master password; required to change or remove an existing one.</summary>
        public string CurrentPassword { get; init; } = "";

        public string NewPassword { get; init; } = "";
        public string ConfirmPassword { get; init; } = "";

        public BlockCipherEngines Engine { get; init; } = BlockCipherEngines.AES;
        public BlockCipherModes Mode { get; init; } = BlockCipherModes.GCM;
        public int KeyDerivationIterations { get; init; } = 1000;
        public bool FullFileEncryption { get; init; }
    }

    /// <summary>
    /// Validates and applies changes to the open file's master password and cipher settings. Changes
    /// take effect on the next save (<see cref="ConnectionsService.SaveToFile"/>), in the same header
    /// format the legacy WinForms app reads (EncryptionEngine, BlockCipherMode, KdfIterations,
    /// FullFileEncryption, Protected).
    /// </summary>
    public static class ConnectionFileSecurity
    {
        public const int MinKeyDerivationIterations = 1000;
        public const int MaxKeyDerivationIterations = 50_000_000;

        /// <summary>Returns the problems with <paramref name="change"/>; empty when it can be applied.</summary>
        public static IReadOnlyList<string> Validate(ConnectionsService service, ConnectionFileSecurityChange change)
        {
            ArgumentNullException.ThrowIfNull(service);
            ArgumentNullException.ThrowIfNull(change);
            var root = service.ConnectionTreeModel?.RootNode;
            var errors = new List<string>();
            if (root is null)
            {
                errors.Add("No connection file is open.");
                return errors;
            }

            if (change.PasswordAction != MasterPasswordAction.Keep && root.IsPasswordProtected
                && change.CurrentPassword != root.PasswordString)
            {
                errors.Add("The current master password is not correct.");
            }

            if (change.PasswordAction == MasterPasswordAction.Set)
            {
                if (string.IsNullOrEmpty(change.NewPassword))
                    errors.Add("Enter the new master password.");
                else if (change.NewPassword != change.ConfirmPassword)
                    errors.Add("The new password and its confirmation do not match.");
                else if (change.NewPassword == root.DefaultPassword)
                    errors.Add("This password is reserved; choose another one.");
            }

            if (change.KeyDerivationIterations is < MinKeyDerivationIterations or > MaxKeyDerivationIterations)
                errors.Add($"Key derivation iterations must be between {MinKeyDerivationIterations:N0} and {MaxKeyDerivationIterations:N0}.");

            if (!Enum.IsDefined(change.Engine) || !Enum.IsDefined(change.Mode))
                errors.Add("Unknown cipher.");

            return errors;
        }

        /// <summary>
        /// Applies <paramref name="change"/> to the open tree and <see cref="ConnectionsService.Encryption"/>.
        /// Returns true when anything changed (the file then needs saving).
        /// </summary>
        /// <exception cref="InvalidOperationException">The change is not valid (see <see cref="Validate"/>).</exception>
        public static bool Apply(ConnectionsService service, ConnectionFileSecurityChange change)
        {
            var errors = Validate(service, change);
            if (errors.Count > 0)
                throw new InvalidOperationException(string.Join(" ", errors));

            var root = service.ConnectionTreeModel!.RootNode;
            var changed = false;
            switch (change.PasswordAction)
            {
                case MasterPasswordAction.Set when root.PasswordString != change.NewPassword || !root.IsPasswordProtected:
                    root.PasswordString = change.NewPassword;
                    changed = true;
                    break;
                case MasterPasswordAction.Remove when root.IsPasswordProtected:
                    root.PasswordString = "";
                    changed = true;
                    break;
            }

            var encryption = new ConnectionFileEncryption(change.Engine, change.Mode,
                change.KeyDerivationIterations, change.FullFileEncryption);
            if (encryption != service.Encryption)
            {
                service.Encryption = encryption;
                changed = true;
            }

            return changed;
        }
    }
}
