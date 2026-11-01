using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Ridley.Core;
using Ridley.Providers.OceanToken;

namespace Ridley.Providers.Tests;

internal sealed class FakeSecrets(string? key) : ISecretStore
{
    public int Reads { get; private set; }
    public Task<string?> GetAsync(string name, CancellationToken ct) { Reads++; return Task.FromResult(name == OceanTokenOptions.ApiKeySecretName ? key : null); }
    public Task SetAsync(string name, string value, CancellationToken ct) => Task.CompletedTask;
    public Task DeleteAsync(string name, CancellationToken ct) => Task.CompletedTask;
}

internal sealed class ListLogger<T> : ILogger<T>
{
    public List<string> Lines { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Lines.Add(formatter(state, exception) + (exception is null ? "" : " | " + exception));
}

internal sealed class FakeHandler(Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    public int Calls { get; private set; }
    public List<HttpRequestMessage> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Calls++;
        Requests.Add(request);
        return await respond(request, Calls, ct);
    }

    public static HttpResponseMessage Json(HttpStatusCode status, string json, params (string, string)[] headers)
    {
        var r = new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        foreach (var (k, v) in headers) { r.Headers.TryAddWithoutValidation(k, v); }
        return r;
    }
}

public class OceanTokenClientTests
{
    private const string Key = "sk-unit-test-key-1234567890";
    private const string Secret = "my private prompt about salary";

    private static (OceanTokenClient client, FakeHandler handler, InMemoryUsageTracker usage, ListLogger<OceanTokenClient> log) Create(
        Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> respond,
        OceanTokenOptions? options = null, string? key = Key)
    {
        var handler = new FakeHandler(respond);
        var usage = new InMemoryUsageTracker();
        var log = new ListLogger<OceanTokenClient>();
        var opts = options ?? new OceanTokenOptions { RetryBaseDelay = TimeSpan.FromMilliseconds(1), RetryMaxDelay = TimeSpan.FromMilliseconds(5) };
        var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        return (new OceanTokenClient(http, new FakeSecrets(key), usage, opts, log), handler, usage, log);
    }

    private static System.Text.Json.Nodes.JsonObject Body() => new() { ["model"] = "m", ["input"] = Secret };
    private static readonly TimeSpan Fast = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Sends_bearer_key_in_header_and_never_in_the_url()
    {
        var (client, handler, _, _) = Create((_, _, _) => Task.FromResult(FakeHandler.Json(HttpStatusCode.OK, "{}")));
        await client.PostJsonAsync("/chat/completions", "chat", "m", Body(), Fast, default);
        var request = handler.Requests.Single();
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal(Key, request.Headers.Authorization.Parameter);
        Assert.DoesNotContain(Key, request.RequestUri!.ToString());
    }

    [Fact]
    public async Task Missing_key_fails_fast_without_any_network_call()
    {
        var (client, handler, _, _) = Create((_, _, _) => Task.FromResult(FakeHandler.Json(HttpStatusCode.OK, "{}")), key: null);
        var ex = await Assert.ThrowsAsync<ProviderException>(() => client.PostJsonAsync("/x", "chat", "m", Body(), Fast, default));
        Assert.Equal(ErrorKind.NotConfigured, ex.Kind);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Transient_failure_is_retried_then_succeeds()
    {
        var (client, handler, _, _) = Create((_, n, _) => Task.FromResult(n < 3
            ? FakeHandler.Json(HttpStatusCode.ServiceUnavailable, "{}")
            : FakeHandler.Json(HttpStatusCode.OK, "{}")));
        var response = await client.PostJsonAsync("/x", "chat", "m", Body(), Fast, default);
        Assert.Equal(3, handler.Calls);
        Assert.Equal(3, response.Attempts);
    }

    [Fact]
    public async Task Retries_are_bounded()
    {
        var (client, handler, _, _) = Create((_, _, _) => Task.FromResult(FakeHandler.Json(HttpStatusCode.TooManyRequests, "{}")));
        var ex = await Assert.ThrowsAsync<ProviderException>(() => client.PostJsonAsync("/x", "chat", "m", Body(), Fast, default));
        Assert.Equal(3, handler.Calls); // 1 try + MaxRetries(2)
        Assert.Equal(429, ex.StatusCode);
    }

    [Fact]
    public async Task Retry_limit_cannot_be_configured_past_the_hard_cap()
    {
        var options = new OceanTokenOptions { MaxRetries = 1000, RetryBaseDelay = TimeSpan.FromMilliseconds(1), RetryMaxDelay = TimeSpan.FromMilliseconds(2) };
        var (client, handler, _, _) = Create((_, _, _) => Task.FromResult(FakeHandler.Json(HttpStatusCode.BadGateway, "{}")), options);
        await Assert.ThrowsAsync<ProviderException>(() => client.PostJsonAsync("/x", "chat", "m", Body(), Fast, default));
        Assert.Equal(1 + OceanTokenOptions.MaxRetryLimit, handler.Calls);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    public async Task Client_errors_are_not_retried(int status)
    {
        var (client, handler, _, _) = Create((_, _, _) => Task.FromResult(FakeHandler.Json((HttpStatusCode)status, "{\"error\":{\"message\":\"nope\"}}")));
        var ex = await Assert.ThrowsAsync<ProviderException>(() => client.PostJsonAsync("/x", "chat", "m", Body(), Fast, default));
        Assert.Equal(1, handler.Calls);
        Assert.Equal(status, ex.StatusCode);
    }

    [Fact]
    public async Task Error_messages_never_contain_the_key_or_the_request_content()
    {
        var (client, _, _, log) = Create((_, _, _) => Task.FromResult(FakeHandler.Json(HttpStatusCode.BadRequest, "{\"error\":{\"message\":\"bad model\"}}")));
        var ex = await Assert.ThrowsAsync<ProviderException>(() => client.PostJsonAsync("/x", "chat", "m", Body(), Fast, default));
        Assert.DoesNotContain(Key, ex.ToString());
        Assert.DoesNotContain(Secret, ex.ToString());
        Assert.DoesNotContain(log.Lines, l => l.Contains(Key) || l.Contains(Secret));
    }

    [Fact]
    public async Task Timeout_is_reported_as_Timeout_after_bounded_attempts()
    {
        var options = new OceanTokenOptions { MaxRetries = 1, RetryBaseDelay = TimeSpan.FromMilliseconds(1), RetryMaxDelay = TimeSpan.FromMilliseconds(2) };
        var (client, handler, _, _) = Create(async (_, _, ct) => { await Task.Delay(Timeout.Infinite, ct); return null!; }, options);
        var ex = await Assert.ThrowsAsync<ProviderException>(() => client.PostJsonAsync("/x", "chat", "m", Body(), TimeSpan.FromMilliseconds(50), default));
        Assert.Equal(ErrorKind.Timeout, ex.Kind);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task User_cancellation_stops_immediately_and_is_not_retried_or_wrapped()
    {
        using var cts = new CancellationTokenSource();
        var (client, handler, _, _) = Create(async (_, _, ct) => { await Task.Delay(Timeout.Infinite, ct); return null!; });
        var pending = client.PostJsonAsync("/x", "chat", "m", Body(), TimeSpan.FromSeconds(30), cts.Token);
        await Task.Delay(50);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Already_cancelled_token_makes_no_call()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var (client, handler, _, _) = Create((_, _, _) => Task.FromResult(FakeHandler.Json(HttpStatusCode.OK, "{}")));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.PostJsonAsync("/x", "chat", "m", Body(), Fast, cts.Token));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Usage_uses_the_cost_header_and_logs_only_counts()
    {
        var (client, _, usage, log) = Create((_, _, _) => Task.FromResult(FakeHandler.Json(HttpStatusCode.OK, "{}",
            ("x-oceantoken-cost", "0.0004219"), ("x-oceantoken-key-spend", "1.5"), ("x-oceantoken-key-budget", "10.0"), ("x-oceantoken-request-id", "req-1"))));
        var response = await client.PostJsonAsync("/x", "chat", "google/gemini-3.8-flash", Body(), Fast, default);
        client.RecordSuccess("chat", "google/gemini-3.8-flash", response, prompt: 60, completion: 113, reasoning: 95);

        var record = usage.Recent(1).Single();
        Assert.Equal(0.0004219m, record.CostUsd);
        Assert.Equal(CostSource.Header, record.CostSource);
        Assert.Equal(10.0m, record.KeyBudgetUsd);
        Assert.Equal(95, record.ReasoningTokens);
        Assert.Equal(0.0004219m, usage.Totals.KnownCostUsd);
        Assert.DoesNotContain(log.Lines, l => l.Contains(Secret) || l.Contains(Key));
    }

    [Fact]
    public async Task Missing_cost_header_is_derived_from_the_spend_delta_and_labelled_as_such()
    {
        var spends = new Queue<string>(["1.5000", "1.5125"]);
        var (client, _, usage, _) = Create((_, _, _) => Task.FromResult(FakeHandler.Json(HttpStatusCode.OK, "{}", ("x-oceantoken-key-spend", spends.Dequeue()))));
        var first = await client.PostJsonAsync("/x", "tts", "m", Body(), Fast, default);
        client.RecordSuccess("tts", "m", first, inputChars: 10);
        var second = await client.PostJsonAsync("/x", "tts", "m", Body(), Fast, default);
        client.RecordSuccess("tts", "m", second, inputChars: 10);

        var records = usage.Recent(2);
        Assert.Equal(CostSource.Unknown, records[0].CostSource); // nothing to diff against yet
        Assert.Equal(CostSource.DerivedFromSpendDelta, records[1].CostSource);
        Assert.Equal(0.0125m, records[1].CostUsd);
    }

    [Fact]
    public async Task Session_spend_limit_blocks_further_requests()
    {
        var options = new OceanTokenOptions { SessionSpendLimitUsd = 0.01m };
        var (client, handler, _, _) = Create((_, _, _) => Task.FromResult(FakeHandler.Json(HttpStatusCode.OK, "{}", ("x-oceantoken-cost", "0.02"))), options);
        var response = await client.PostJsonAsync("/x", "chat", "m", Body(), Fast, default);
        client.RecordSuccess("chat", "m", response);

        var ex = await Assert.ThrowsAsync<ProviderException>(() => client.PostJsonAsync("/x", "chat", "m", Body(), Fast, default));
        Assert.Equal(ErrorKind.Denied, ex.Kind);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public void Gateway_error_codes_map_to_honest_kinds()
    {
        Assert.Equal(ErrorKind.Denied, OceanTokenClient.MapError(401, [], "chat").Kind);
        Assert.Equal(ErrorKind.Denied, OceanTokenClient.MapError(402, [], "chat").Kind);
        Assert.Equal(ErrorKind.NotFound, OceanTokenClient.MapError(404, [], "tts").Kind);
        Assert.Equal(ErrorKind.Unsupported, OceanTokenClient.MapError(400, [], "tts").Kind);
        Assert.Contains("rejected", OceanTokenClient.MapError(401, "not json"u8.ToArray(), "chat").Message);
    }
}
