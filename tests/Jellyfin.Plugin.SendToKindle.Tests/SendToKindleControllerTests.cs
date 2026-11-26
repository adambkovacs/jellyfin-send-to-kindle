using System.Reflection;
using System.Security.Claims;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.SendToKindle.Controllers;
using Jellyfin.Plugin.SendToKindle.Delivery;
using Jellyfin.Plugin.SendToKindle.WebIntegration;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.SendToKindle.Tests;

public sealed class SendToKindleControllerTests : IDisposable
{
    private const string BookId = "11111111111111111111111111111111";
    private const string Key = "022d3cee-5833-4aa0-a49f-f9e9acf8954b";
    private const string JobId = "cfd75da4-20eb-4ec1-a267-38a4e4f71e87";
    private readonly string _bookPath = Path.GetTempFileName();
    private readonly User _user = new("test-user", "test-auth", "test-reset");
    private readonly DeliveryStub _delivery = new();
    private readonly SendToKindleController _controller;
    private bool _bookVisible = true;

    public SendToKindleControllerTests()
    {
        _user.SetPermission(PermissionKind.EnableContentDownloading, true);
        var library = CreateProxy<ILibraryManager>((method, arguments) =>
        {
            Assert.Equal(nameof(ILibraryManager.GetItemById), method.Name);
            Assert.Equal(typeof(Book), Assert.Single(method.GetGenericArguments()));
            Assert.Equal(Guid.Parse(BookId), arguments[0]);
            Assert.Same(_user, arguments[1]);
            return _bookVisible ? new DownloadableBook { Path = _bookPath } : null;
        });
        var users = CreateProxy<IUserManager>((method, arguments) =>
        {
            Assert.Equal(nameof(IUserManager.GetUserById), method.Name);
            Assert.Equal(_user.Id, Assert.Single(arguments));
            return _user;
        });

        _controller = new SendToKindleController(library, users, _delivery, new WebIntegrationStatus())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        Authenticate(isApiKey: false);
    }

    [Fact]
    public void Controller_RequiresElevation()
    {
        Assert.Equal(Policies.RequiresElevation, typeof(SendToKindleController).GetCustomAttribute<AuthorizeAttribute>()?.Policy);
    }

    [Fact]
    public async Task Options_RejectsApiKeyEvenWithAnIdentityClaim()
    {
        Authenticate(isApiKey: true);

        Assert.Equal(401, Assert.IsType<ObjectResult>(await _controller.GetOptions(BookId, TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(Guid.Empty, _delivery.UserId);
    }

    [Fact]
    public async Task Options_RejectsMissingPhysicalFile()
    {
        File.Delete(_bookPath);

        Assert.Equal(404, Assert.IsType<ObjectResult>(await _controller.GetOptions(BookId, TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(Guid.Empty, _delivery.UserId);
    }

    [Fact]
    public async Task Enqueue_ReplayUsesTheSameKeyAndCurrentBookAuthorization()
    {
        _controller.Request.Headers["X-Delivery-User"] = "22222222222222222222222222222222";
        EnqueueSendRequest request = new(BookId, "paperwhite", "original", Key);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        var first = Assert.IsType<AcceptedAtActionResult>(await _controller.Enqueue(request, cancellationToken));
        var replay = Assert.IsType<OkObjectResult>(await _controller.Enqueue(request, cancellationToken));
        Assert.Equal(new DeliveryReceipt(JobId, "queued"), first.Value);
        Assert.Equal(first.Value, replay.Value);
        Assert.Equal(_user.Id, _delivery.UserId);
        Assert.Equal(new[] { Guid.Parse(Key), Guid.Parse(Key) }, _delivery.SubmittedKeys);

        _bookVisible = false;
        Assert.Equal(404, Assert.IsType<ObjectResult>(await _controller.Enqueue(request, cancellationToken)).StatusCode);
        Assert.Equal(2, _delivery.SubmittedKeys.Count);
    }

    [Fact]
    public async Task Status_RechecksCurrentDownloadPermissionAndUsesSessionUser()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Assert.IsType<OkObjectResult>(await _controller.GetJob(JobId, cancellationToken));
        Assert.Equal(_user.Id, _delivery.UserId);
        Assert.Equal(Guid.Parse(JobId), _delivery.StatusJobId);

        _user.SetPermission(PermissionKind.EnableContentDownloading, false);
        _delivery.UserId = Guid.Empty;
        Assert.Equal(404, Assert.IsType<ObjectResult>(await _controller.GetJob(JobId, cancellationToken)).StatusCode);
        Assert.Equal(Guid.Empty, _delivery.UserId);
    }

    public void Dispose() => File.Delete(_bookPath);

    private void Authenticate(bool isApiKey)
    {
        _controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("Jellyfin-UserId", _user.Id.ToString("N")), new Claim("Jellyfin-IsApiKey", isApiKey.ToString())],
            "Test"));
    }

    private static T CreateProxy<T>(Func<MethodInfo, object?[], object?> invoke) where T : class
    {
        T proxy = DispatchProxy.Create<T, StubProxy>();
        ((StubProxy)(object)proxy).Handler = invoke;
        return proxy;
    }

    public class StubProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[], object?> Handler { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args!);
    }

    private sealed class DownloadableBook : Book
    {
        public override bool CanDownload() => true;
    }

    private sealed class DeliveryStub : IDeliveryClient
    {
        public Guid UserId { get; set; }

        public Guid StatusJobId { get; private set; }

        public List<Guid> SubmittedKeys { get; } = [];

        public Task<DeliveryOptions> GetOptionsAsync(Guid userId, Guid bookId, CancellationToken cancellationToken)
        {
            UserId = userId;
            return Task.FromResult(new DeliveryOptions([], ["original"]));
        }

        public Task<DeliverySubmission> SubmitAsync(
            Guid userId, Guid bookId, string deviceId, string format, Guid idempotencyKey, CancellationToken cancellationToken)
        {
            UserId = userId;
            bool isReplay = SubmittedKeys.Contains(idempotencyKey);
            SubmittedKeys.Add(idempotencyKey);
            return Task.FromResult(new DeliverySubmission(new DeliveryReceipt(JobId, "queued"), isReplay));
        }

        public Task<DeliveryReceipt> GetStatusAsync(Guid userId, Guid jobId, CancellationToken cancellationToken)
        {
            UserId = userId;
            StatusJobId = jobId;
            return Task.FromResult(new DeliveryReceipt(JobId, "queued"));
        }
    }
}
