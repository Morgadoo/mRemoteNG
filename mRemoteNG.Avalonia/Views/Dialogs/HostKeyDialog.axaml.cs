using Avalonia.Controls;
using mRemoteNG.Protocols.Ssh;

namespace mRemoteNG.Avalonia.Views.Dialogs;

/// <summary>
/// Shows an unknown or changed SSH host key. <see cref="Window.ShowDialog{TResult}(Window)"/>
/// returns a <see cref="HostKeyDecision"/>; closing the window rejects the key.
/// </summary>
public partial class HostKeyDialog : Window
{
    public HostKeyDialog()
    {
        InitializeComponent();
    }

    public HostKeyDialog(HostKeyPromptRequest request) : this()
    {
        var key = request.HostKey;
        HostText.Text = KnownHostsStore.FormatHost(key.Host, key.Port);
        KeyTypeText.Text = key.KeyType;
        FingerprintText.Text = key.Fingerprint;

        CancelButton.Click += (_, _) => Close(HostKeyDecision.Reject);
        OnceButton.Click += (_, _) => Close(HostKeyDecision.AcceptOnce);
        AcceptButton.Click += (_, _) => Close(HostKeyDecision.AcceptAndSave);
        ReplaceButton.Click += (_, _) => Close(HostKeyDecision.ReplaceAndSave);
        ConfirmReplaceBox.IsCheckedChanged += (_, _) => ReplaceButton.IsEnabled = ConfirmReplaceBox.IsChecked == true;

        if (request.Status == HostKeyStatus.Mismatch)
            ShowMismatch(request);
        else
            ShowUnknown(request);
    }

    private void ShowUnknown(HostKeyPromptRequest request)
    {
        Title = "Unknown SSH Host Key";
        HeadingText.Text = $"The authenticity of host '{HostText.Text}' can't be established.";
        ExplanationText.Text =
            "Compare the fingerprint below with the one published by the server's administrator. " +
            "\"Accept and save\" stores the key in " + request.KnownHostsFile +
            " so future connections are verified automatically.";
        OnceButton.IsVisible = true;
        AcceptButton.IsVisible = true;

        if (request.OtherKeyTypesKnown)
        {
            StoredPanel.IsVisible = true;
            StoredHeading.Text = "This host is already known with a different key type:";
            StoredText.Text = Describe(request.KnownEntries);
        }
    }

    private void ShowMismatch(HostKeyPromptRequest request)
    {
        Title = "SSH Host Key Changed";
        WarningBanner.IsVisible = true;
        HeadingText.Text = $"The {request.HostKey.KeyType} key presented by '{HostText.Text}' does not match the stored key.";
        ExplanationText.Text =
            "The connection has been blocked. Replacing the key updates " + request.KnownHostsFile +
            "; ~/.ssh/known_hosts is never modified.";
        StoredPanel.IsVisible = true;
        StoredHeading.Text = "Stored key:";
        StoredText.Text = Describe(request.ConflictingEntries);
        ConfirmReplaceBox.IsVisible = true;
        ReplaceButton.IsVisible = true;
        CancelButton.Classes.Add("accent");
        CancelButton.IsDefault = true;
    }

    private static string Describe(IEnumerable<KnownHostEntry> entries) =>
        string.Join(Environment.NewLine, entries
            .Where(e => e.Marker == KnownHostMarker.None)
            .Select(e => $"{e.KeyType}  {e.Fingerprint}  ({e.Source})"));
}
