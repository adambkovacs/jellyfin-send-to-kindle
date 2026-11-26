using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Jellyfin.Plugin.SendToKindle.Delivery;

namespace Jellyfin.Plugin.SendToKindle.Tests;

public sealed class RemoteDeliveryClientTests
{
    private const string BaseAddress = "https://delivery.example.invalid";
    private static readonly Guid UserId = Guid.NewGuid();

    [Theory]
    [InlineData("options")]
    [InlineData("enqueue")]
    [InlineData("replay")]
    [InlineData("conflicting device")]
    [InlineData("inaccessible book")]
    [InlineData("unknown device")]
    public async Task MatchesPublishedCallerFixtures(string fixtureName)
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        JsonElement fixture = ReadFixture(fixtureName);
        HttpStatusCode httpStatus = (HttpStatusCode)fixture.GetProperty("status").GetInt32();
        JsonElement response = fixture.GetProperty("response");
        string expectedPath = fixture.GetProperty("path").GetString()!;
        string? expectedMethod = fixture.GetProperty("method").GetString();
        string token = Guid.NewGuid().ToString("N");
        var handler = new RecordingHandler(async (request, requestCancellationToken) =>
        {
            Assert.Equal(BaseAddress + expectedPath, request.RequestUri?.AbsoluteUri);
            Assert.Equal(expectedMethod, request.Method.Method);
            Assert.Equal(UserId.ToString("N"), Assert.Single(request.Headers.GetValues("X-Delivery-User")));
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.True(token == request.Headers.Authorization?.Parameter);

            if (fixture.TryGetProperty("body", out JsonElement expectedBody))
            {
                Assert.NotNull(request.Content);
                Assert.Equal("application/json", request.Content.Headers.ContentType?.MediaType);
                using var actualBody = JsonDocument.Parse(await request.Content.ReadAsStringAsync(requestCancellationToken));
                Assert.True(JsonElement.DeepEquals(expectedBody, actualBody.RootElement));
            }
            else
            {
                Assert.Null(request.Content);
            }

            return new HttpResponseMessage(httpStatus) { Content = JsonContent.Create(response) };
        });

        using var client = new RemoteDeliveryClient(BaseAddress, token, handler);
        if (fixtureName == "options")
        {
            Guid bookId = Guid.ParseExact(expectedPath.Split('/')[3], "N");
            var options = await client.GetOptionsAsync(UserId, bookId, cancellationToken);
            Assert.True(JsonElement.DeepEquals(response, JsonSerializer.SerializeToElement(options)));
        }
        else
        {
            JsonElement body = fixture.GetProperty("body");
            Task<DeliverySubmission> submission = client.SubmitAsync(
                UserId,
                Guid.ParseExact(body.GetProperty("bookId").GetString()!, "N"),
                body.GetProperty("deviceId").GetString()!,
                body.GetProperty("format").GetString()!,
                body.GetProperty("idempotencyKey").GetGuid(),
                cancellationToken);

            if (httpStatus is HttpStatusCode.OK or HttpStatusCode.Accepted)
            {
                var result = await submission;
                Assert.Equal(httpStatus == HttpStatusCode.OK, result.IsReplay);
                Assert.True(JsonElement.DeepEquals(response, JsonSerializer.SerializeToElement(result.Receipt)));
            }
            else
            {
                var error = await Assert.ThrowsAsync<DeliveryException>(() => submission);
                Assert.Equal((int)httpStatus, error.StatusCode);
                Assert.Equal(response.GetProperty("error").GetString(), error.Error);
            }
        }

        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task StatusReturnsCurrentReceiptForAuthenticatedUser()
    {
        Guid jobId = ReadFixture("enqueue").GetProperty("response").GetProperty("jobId").GetGuid();
        var handler = new RecordingHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal(BaseAddress + "/delivery/v1/jobs/" + jobId.ToString("D"), request.RequestUri?.AbsoluteUri);
            Assert.Equal(UserId.ToString("N"), Assert.Single(request.Headers.GetValues("X-Delivery-User")));
            Assert.Null(request.Content);

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { jobId, status = "smtp_accepted" }),
            });
        });
        using var client = new RemoteDeliveryClient(BaseAddress + "/delivery/", Guid.NewGuid().ToString("N"), handler);

        var receipt = await client.GetStatusAsync(UserId, jobId, TestContext.Current.CancellationToken);

        Assert.Equal(jobId.ToString("D"), receipt.JobId);
        Assert.Equal("smtp_accepted", receipt.Status);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("http://delivery.example.invalid")]
    [InlineData("https://delivery.example.invalid?query")]
    [InlineData("https://delivery.example.invalid#fragment")]
    public async Task InvalidOrMissingEndpointDoesNotMakeARequest(string? baseAddress)
    {
        var handler = new RecordingHandler((_, _) => throw new InvalidOperationException());
        using var client = new RemoteDeliveryClient(baseAddress, Guid.NewGuid().ToString("N"), handler);

        var error = await Assert.ThrowsAsync<DeliveryException>(
            () => client.GetOptionsAsync(UserId, Guid.NewGuid(), TestContext.Current.CancellationToken));

        Assert.Equal(503, error.StatusCode);
        Assert.Equal("unavailable", error.Error);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task MissingBearerTokenDoesNotMakeARequest()
    {
        var handler = new RecordingHandler((_, _) => throw new InvalidOperationException());
        using var client = new RemoteDeliveryClient(BaseAddress, null, handler);

        var error = await Assert.ThrowsAsync<DeliveryException>(
            () => client.GetOptionsAsync(UserId, Guid.NewGuid(), TestContext.Current.CancellationToken));

        Assert.Equal(503, error.StatusCode);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public void ProductionTransportDisablesRedirects()
    {
        using var transport = RemoteDeliveryClient.CreateTransport();
        Assert.False(transport.AllowAutoRedirect);
        Assert.Null(transport.SslOptions.RemoteCertificateValidationCallback);
    }

    [Fact]
    public async Task LostSubmissionResponseDoesNotTriggerAnAutomaticRetry()
    {
        var handler = new RecordingHandler((_, _) => throw new HttpRequestException("Simulated connection loss"));
        using var client = new RemoteDeliveryClient(BaseAddress, Guid.NewGuid().ToString("N"), handler);

        var error = await Assert.ThrowsAsync<DeliveryException>(() => client.SubmitAsync(
            UserId, Guid.NewGuid(), "paperwhite", "original", Guid.NewGuid(), TestContext.Current.CancellationToken));

        Assert.Equal(503, error.StatusCode);
        Assert.Equal("unavailable", error.Error);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Conflict)]
    public async Task UnexpectedResponseDoesNotExposeServiceBody(HttpStatusCode status)
    {
        var handler = new RecordingHandler((_, _) => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = JsonContent.Create(new { error = "Unexpected upstream detail" }),
        }));
        using var client = new RemoteDeliveryClient(BaseAddress, Guid.NewGuid().ToString("N"), handler);

        var error = await Assert.ThrowsAsync<DeliveryException>(
            () => client.GetStatusAsync(UserId, Guid.NewGuid(), TestContext.Current.CancellationToken));

        Assert.Equal(503, error.StatusCode);
        Assert.Equal("unavailable", error.Error);
        Assert.Equal("unavailable", error.Message);
    }

    private static JsonElement ReadFixture(string name)
    {
        using JsonDocument fixtures = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "delivery-v1.json")));
        return fixtures.RootElement.EnumerateArray().Single(fixture => fixture.GetProperty("name").GetString() == name).Clone();
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return send(request, cancellationToken);
        }
    }
}
