using System.Reactive.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using mRemoteNG.Avalonia.ViewModels;
using mRemoteNG.Avalonia.Views;
using mRemoteNG.Avalonia.Views.OptionsPages;
using mRemoteNG.Core.Settings;
using mRemoteNG.ExternalProviders;
using mRemoteNG.Platform.Security;
using mRemoteNG.Protocols.Abstractions;
using Xunit;

namespace mRemoteNG.Avalonia.Tests;

/// <summary>The Options → External Providers page and the provider wiring in the app's container.</summary>
public class ExternalProvidersOptionsTests
{
    [AvaloniaFact]
    public void Container_RegistersTheProviders_TheDialogPrompt_AndBothPreparationSteps()
    {
        _ = TestHost.MainWindow;
        var services = AppServices.Provider;

        services.GetServices<IExternalCredentialProvider>().Should().HaveCount(4);
        services.GetServices<IExternalAddressProvider>().Should().ContainSingle();
        services.GetRequiredService<IExternalProviderPrompt>().Should().BeOfType<Services.AvaloniaExternalProviderPrompt>();
        services.GetServices<IConnectionPreparationStep>().Select(s => s.Order).Should().Contain([100, 200]);
    }

    [AvaloniaFact]
    public void OptionsPage_Renders_AndTypedSecretsAreSavedEncrypted()
    {
        _ = TestHost.MainWindow;
        var vm = AppServices.GetRequired<OptionsWindowViewModel>();
        var window = new OptionsWindow { DataContext = vm };
        window.Show();
        try
        {
            vm.SelectedCategory = vm.Categories.Single(c => c.DisplayName == "External Providers");
            Dispatcher.UIThread.RunJobs();
            window.GetVisualDescendants().OfType<ExternalProvidersSettingsPage>().Should().ContainSingle();

            var page = vm.ExternalProviders;
            page.VaultUrl = "http://127.0.0.1:8299";
            page.VaultSecret.Value = "s.typed-token";

            vm.WorkingCopy.VaultSecretProtected.Should().NotBeEmpty().And.NotContain("typed-token");
            AppServices.GetRequired<ICryptoProvider>().Unprotect(vm.WorkingCopy.VaultSecretProtected).Should().Be("s.typed-token");
            page.VaultSecret.IsSaved.Should().BeTrue();

            // Switching from token to a login method must not reuse the token as a password.
            page.SelectedVaultAuthMethod = page.VaultAuthMethods.Single(m => m.Value == VaultAuthMethod.UserPass);
            vm.WorkingCopy.VaultSecretProtected.Should().BeEmpty();
            page.VaultSecretLabel.Should().Be("Password");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task TestButton_ReportsTheProviderError_FromTheUnsavedWorkingCopy()
    {
        _ = TestHost.MainWindow;
        var vm = AppServices.GetRequired<OptionsWindowViewModel>();
        var page = vm.ExternalProviders;
        page.DelineaUrl = string.Empty;

        await page.DelineaTest.TestCommand.Execute();

        page.DelineaTest.Failed.Should().BeTrue();
        page.DelineaTest.Status.Should().Contain("URL is not configured");
    }
}
