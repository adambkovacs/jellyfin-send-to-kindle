using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.SendToKindle.Delivery;
using Jellyfin.Plugin.SendToKindle.Settings;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.SendToKindle.Tests;

public sealed class KindleSettingsTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("kindle-settings-tests-");

    [Theory]
    [InlineData(false, DeliveryMode.Smtp)]
    [InlineData(true, DeliveryMode.Service)]
    public void InitialModePreservesConfiguredService(bool hasService, DeliveryMode expected)
    {
        var store = new KindleSettingsStore(_directory.FullName,
            name => hasService && name.StartsWith("KINDLE_DELIVERY_", StringComparison.Ordinal)
                ? Guid.NewGuid().ToString("N") : null);
        Assert.Equal(expected, store.Read().Mode);
    }

    [Fact]
    public void SavePersistsPrivatelyAndRejectsStaleEdits()
    {
        KindleSettingsStore store = CreateStore();
        string testValue = Guid.NewGuid().ToString("N");
        KindleSettings initial = CreateSettings(Guid.Empty, "smtp.example.invalid", testValue, testValue);

        Assert.Null(store.Save(initial, clearPassword: false, clearGoogleKey: false));
        Assert.Equal("settings_changed", store.Save(initial, clearPassword: true, clearGoogleKey: true));
        KindleSettings saved = CreateStore().Read();
        Assert.True(testValue == saved.Smtp.Password && testValue == saved.GoogleBooksApiKey);
        Assert.NotEqual(Guid.Empty, saved.Revision);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(Assert.Single(_directory.GetFiles()).FullName));
        }
    }

    [Fact]
    public void ChangingSmtpIdentityDropsOldPasswordAndReplacingDeviceAddressRotatesId()
    {
        KindleSettingsStore store = CreateStore();
        var initial = CreateSettings(Guid.Empty, "smtp.example.invalid", Guid.NewGuid().ToString("N"), null);
        Assert.Null(store.Save(initial, clearPassword: false, clearGoogleKey: false));
        KindleSettings saved = store.Read();
        string recipientId = Assert.Single(saved.Recipients).Id;
        var changed = CreateSettings(saved.Revision, "relay.example.invalid", null, null, recipientId);

        Assert.Null(store.Save(changed, clearPassword: false, clearGoogleKey: false));
        Assert.Null(store.Read().Smtp.Password);
        Assert.NotEqual(recipientId, Assert.Single(store.Read().Recipients).Id);
    }

    [Fact]
    public void LegacyRecipientGrantSurvivesOlderDashboardSavesAndCanBeExplicitlyCleared()
    {
        KindleSettingsStore store = CreateStore();
        Guid revision = Guid.NewGuid();
        Guid originalUser = Guid.NewGuid();
        Guid secondUser = Guid.NewGuid();
        string recipientId = Guid.NewGuid().ToString("D");
        string address = SyntheticAddress();
        var smtp = new SmtpSettings { Host = "smtp.example.invalid", Sender = SyntheticAddress() };
        string path = Path.Combine(_directory.FullName, "send-to-kindle-private-settings.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            Revision = revision,
            Mode = DeliveryMode.Smtp,
            Smtp = smtp,
            Recipients = new[] { new { Id = recipientId, Label = "Reader", Address = address, JellyfinUserId = originalUser } },
        }));

        Assert.Equal(originalUser, Assert.Single(Assert.Single(store.Read().Recipients).GrantedUsers));
        RecipientSettings CreateRecipient(IReadOnlyList<Guid>? grants) => new()
        {
            Id = recipientId,
            Label = "Reader",
            Address = address,
            AllowedUserIds = grants,
        };

        KindleSettings CreateUpdate(IReadOnlyList<Guid>? grants) => new()
        {
            Revision = store.Read().Revision,
            Mode = DeliveryMode.Smtp,
            Smtp = smtp,
            Recipients = [CreateRecipient(grants)],
        };

        Assert.Null(store.Save(CreateUpdate([originalUser, secondUser]), false, false));
        Assert.Null(store.Save(CreateUpdate(null), false, false));
        RecipientSettings saved = Assert.Single(CreateStore().Read().Recipients);
        Assert.Equal(recipientId, saved.Id);
        Assert.Equal(new[] { originalUser, secondUser }, saved.GrantedUsers);
        Assert.Null(saved.JellyfinUserId);

        Assert.Equal("invalid_settings", store.Save(CreateUpdate([Guid.Empty]), false, false));
        Assert.Null(store.Save(CreateUpdate([]), false, false));
        Assert.Empty(Assert.Single(CreateStore().Read().Recipients).GrantedUsers);
    }

    [Fact]
    public void WorkLookupDefaultsOffAndOlderDashboardSavesPreserveConsent()
    {
        var store = CreateStore();
        string path = Path.Combine(_directory.FullName, "send-to-kindle-private-settings.json");
        var smtp = new SmtpSettings { Host = "smtp.example.invalid", Sender = SyntheticAddress() };
        File.WriteAllText(path, JsonSerializer.Serialize(new { Mode = DeliveryMode.Smtp, Smtp = smtp, GoogleBooksEnabled = true }));
        Assert.True(store.Read().GoogleBooksEnabled);
        Assert.False(store.Read().GoogleBooksWorkLookupEnabled);

        KindleSettings Update(bool? workLookupEnabled) => new()
        {
            Revision = store.Read().Revision,
            Mode = DeliveryMode.Smtp,
            Smtp = smtp,
            GoogleBooksEnabled = true,
            GoogleBooksWorkLookupEnabled = workLookupEnabled,
        };

        Assert.Null(store.Save(Update(true), false, false));
        Assert.True(CreateStore().Read().GoogleBooksWorkLookupEnabled);
        Assert.Null(store.Save(Update(null), false, false));
        Assert.True(CreateStore().Read().GoogleBooksWorkLookupEnabled);
        Assert.Null(store.Save(Update(false), false, false));
        Assert.False(CreateStore().Read().GoogleBooksWorkLookupEnabled);
    }

    [Fact]
    public async Task AdminReadRedactsSecretsAndNonAdminOrApiKeyCannotTestConnection()
    {
        KindleSettingsStore store = CreateStore();
        string testValue = Guid.NewGuid().ToString("N");
        Assert.Null(store.Save(CreateSettings(Guid.Empty, "smtp.example.invalid", testValue, testValue), false, false));

        IUserManager users = DispatchProxy.Create<IUserManager, UserProxy>();
        var user = new User("synthetic", "synthetic", "synthetic");
        ((UserProxy)(object)users).User = user;
        var tester = new ConnectionStub();
        var controller = new KindleSettingsController(users, store, tester)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        var cancellation = TestContext.Current.CancellationToken;
        controller.HttpContext.User = Principal(user.Id, isApiKey: false);

        Assert.Equal(403, Assert.IsType<StatusCodeResult>(controller.Get()).StatusCode);
        Assert.Equal(403, Assert.IsType<StatusCodeResult>(await controller.TestConnection(cancellation)).StatusCode);
        user.SetPermission(PermissionKind.IsAdministrator, true);
        var admin = Assert.IsType<ContentResult>(controller.Get());
        string json = Assert.IsType<string>(admin.Content);
        Assert.False(json.Contains(testValue, StringComparison.Ordinal));
        using JsonDocument response = JsonDocument.Parse(json);
        Assert.Equal(20 * 1024 * 1024, response.RootElement.GetProperty("smtp").GetProperty("maxAttachmentBytes").GetInt64());
        Assert.True(response.RootElement.GetProperty("smtp").GetProperty("passwordConfigured").GetBoolean());
        Assert.True(response.RootElement.GetProperty("googleBooksKeyConfigured").GetBoolean());
        Assert.False(response.RootElement.GetProperty("googleBooksWorkLookupEnabled").GetBoolean());
        Assert.Empty(response.RootElement.GetProperty("recipients")[0].GetProperty("allowedUserIds").EnumerateArray());

        byte[] body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            settings = new
            {
                store.Read().Revision,
                mode = DeliveryMode.Smtp,
                smtp = new SmtpSettings { Host = "smtp.example.invalid", Sender = SyntheticAddress() },
                googleBooksEnabled = true,
                googleBooksWorkLookupEnabled = true,
            },
            clearSmtpPassword = false,
            clearGoogleBooksKey = false,
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        controller.Request.ContentType = "application/json";
        using var requestBody = new MemoryStream(body);
        controller.Request.Body = requestBody;
        var updated = Assert.IsType<ContentResult>(await controller.Put(cancellation));
        Assert.Equal(200, updated.StatusCode);
        string updatedJson = Assert.IsType<string>(updated.Content);
        Assert.False(updatedJson.Contains(testValue, StringComparison.Ordinal));
        using JsonDocument updatedResponse = JsonDocument.Parse(updatedJson);
        Assert.True(updatedResponse.RootElement.GetProperty("googleBooksWorkLookupEnabled").GetBoolean());

        controller.HttpContext.User = Principal(user.Id, isApiKey: true);
        Assert.Equal(403, Assert.IsType<StatusCodeResult>(controller.Get()).StatusCode);
        Assert.Equal(403, Assert.IsType<StatusCodeResult>(await controller.TestConnection(cancellation)).StatusCode);
        Assert.Equal(0, tester.Calls);
    }

    private KindleSettingsStore CreateStore()
    {
        return new KindleSettingsStore(_directory.FullName, _ => null);
    }

    private static KindleSettings CreateSettings(Guid revision, string host, string? password, string? key, string id = "")
    {
        return new KindleSettings
        {
            Revision = revision,
            Mode = DeliveryMode.Smtp,
            Smtp = new SmtpSettings { Host = host, Sender = SyntheticAddress(), Password = password },
            Recipients = [new RecipientSettings { Id = id, Label = "Reader", Address = SyntheticAddress() }],
            GoogleBooksApiKey = key,
        };
    }

    private static string SyntheticAddress()
    {
        return Guid.NewGuid().ToString("N") + "@example.invalid";
    }

    private static ClaimsPrincipal Principal(Guid id, bool isApiKey)
    {
        return new ClaimsPrincipal(new ClaimsIdentity(
            [new("Jellyfin-UserId", id.ToString("N")), new("Jellyfin-IsApiKey", isApiKey.ToString())], "test"));
    }

    public void Dispose()
    {
        _directory.Delete(recursive: true);
    }

    public class UserProxy : DispatchProxy
    {
        public User? User { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            return targetMethod?.Name == nameof(IUserManager.GetUserById) && args?[0] is Guid id && User?.Id == id
                ? User
                : throw new InvalidOperationException();
        }
    }

    private sealed class ConnectionStub : ISmtpConnectionTester
    {
        public int Calls { get; private set; }

        public Task CheckConnectionAsync(SmtpSettings settings, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.CompletedTask;
        }
    }
}
