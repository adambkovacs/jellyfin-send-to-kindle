using System.Text.Json;
using System.Xml.Serialization;
using Jellyfin.Plugin.SendToKindle.Configuration;

namespace Jellyfin.Plugin.SendToKindle.Tests;

public sealed class ConfigurationCompatibilityTests
{
    [Fact]
    public void LegacySettingsLoadWithoutExposingRemovedFields()
    {
        const string legacyConfig = """
            <PluginConfiguration>
                <SmtpHost>removed</SmtpHost>
                <SmtpPassword>removed</SmtpPassword>
                <SenderAddress>removed</SenderAddress>
                <KindleAddress>removed</KindleAddress>
                <KccExecutable>removed</KccExecutable>
            </PluginConfiguration>
            """;

        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        using var input = new StringReader(legacyConfig);
        var config = Assert.IsType<PluginConfiguration>(serializer.Deserialize(input));

        using var output = new StringWriter();
        serializer.Serialize(output, config);
        Assert.DoesNotContain("removed", output.ToString());
        Assert.DoesNotContain("removed", JsonSerializer.Serialize(config));
        Assert.DoesNotContain("KindleAddress", JsonSerializer.Serialize(config));
        Assert.DoesNotContain("SmtpPassword", JsonSerializer.Serialize(config));
    }
}
