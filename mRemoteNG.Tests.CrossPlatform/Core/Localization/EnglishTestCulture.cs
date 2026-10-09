using System.Globalization;
using System.Runtime.CompilerServices;

namespace mRemoteNG.Tests.CrossPlatform.Core.Localization;

/// <summary>
/// Tests assert English UI texts (e.g. host status summaries); run them in English whatever the machine's
/// language is. Tests of other languages pass a culture explicitly or set it for their own thread.
/// </summary>
internal static class EnglishTestCulture
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
    }
}
