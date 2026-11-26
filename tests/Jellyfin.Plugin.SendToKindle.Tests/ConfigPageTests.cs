using System.Reflection;

namespace Jellyfin.Plugin.SendToKindle.Tests;

public sealed class ConfigPageTests
{
    [Fact]
    public void ConfigPage_DoesNotUseTemplatePlaceholders()
    {
        string contents = ReadConfigPage();

        Assert.DoesNotContain("${", contents, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfigPage_UsesWriteOnlyFieldsAndTheDedicatedAdminApi()
    {
        string contents = ReadConfigPage();
        Assert.Contains("SendToKindle/Admin/Settings", contents, StringComparison.Ordinal);
        Assert.Contains("data-field=\"password\" type=\"password\"", contents, StringComparison.Ordinal);
        Assert.Contains("data-field=\"googleKey\" type=\"password\"", contents, StringComparison.Ordinal);
        Assert.DoesNotContain("getPluginConfiguration", contents, StringComparison.Ordinal);
        Assert.DoesNotContain("updatePluginConfiguration", contents, StringComparison.Ordinal);
    }

    private static string ReadConfigPage()
    {
        Assembly assembly = typeof(Plugin).Assembly;
        using Stream stream = assembly.GetManifestResourceStream(
            "Jellyfin.Plugin.SendToKindle.Configuration.configPage.html")
            ?? throw new InvalidOperationException("Embedded configuration page was not found.");
        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }
}
