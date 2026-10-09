using Avalonia.Controls;
using mRemoteNG.Core.Localization;
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
        Title = Localizer.Get("HostKeyUnknownTitle");
        HeadingText.Text = Localizer.Format("HostKeyUnknownHeadingFormat", HostText.Text);
        ExplanationText.Text = Localizer.Format("HostKeyUnknownExplanationFormat", request.KnownHostsFile);
        OnceButton.IsVisible = true;
        AcceptButton.IsVisible = true;

        if (request.OtherKeyTypesKnown)
        {
            StoredPanel.IsVisible = true;
            StoredHeading.Text = Localizer.Get("HostKeyOtherTypeKnown");
            StoredText.Text = Describe(request.KnownEntries);
        }
    }

    private void ShowMismatch(HostKeyPromptRequest request)
    {
        Title = Localizer.Get("HostKeyChangedTitle");
        WarningBanner.IsVisible = true;
        HeadingText.Text = Localizer.Format("HostKeyMismatchHeadingFormat", request.HostKey.KeyType, HostText.Text);
        ExplanationText.Text = Localizer.Format("HostKeyMismatchExplanationFormat", request.KnownHostsFile);
        StoredPanel.IsVisible = true;
        StoredHeading.Text = Localizer.Get("HostKeyStoredKey");
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
