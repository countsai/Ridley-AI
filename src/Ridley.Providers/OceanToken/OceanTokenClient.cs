using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Ridley.Core;

namespace Ridley.Providers.OceanToken;

/// <summary>Provider failure with a classified reason. Never carries request or response content.</summary>
public sealed class ProviderException(ErrorKind kind, string message, int? statusCode = null, Exception? inner = null)
    : Exception(message, inner)
{
    public ErrorKind Kind { get; } = kind;
    public int? StatusCode { get; } = statusCode;
}

public sealed record GatewayMeta(string? RequestId, decimal? Cost, decimal? KeySpend, decimal? KeyBudget);

public sealed record GatewayResponse(int Status, byte[] Body, string? ContentType, GatewayMeta Meta, TimeSpan Elapsed, int Attempts);

/// <summary>
/// The only class that talks to the gateway. Responsibilities: read the key from <see cref="ISecretStore"/>,
/// enforce per-request timeouts, retry transient failures a bounded number of times, honour cancellation,
/// record usage, and keep content out of logs and exceptions.
/// </summary>
public sealed class OceanTokenClient(
    HttpClient http,
    ISecretStore secrets,
    IUsageTracker usage,
    OceanTokenOptions options,
    ILogger<OceanTokenClient> log)
{
    private static readonly HashSet<HttpStatusCode> Transient =
    [
        HttpStatusCode.RequestTimeout, HttpStatusCode.TooManyRequests, HttpStatusCode.InternalServerError,
        HttpStatusCode.BadGateway, HttpStatusCode.ServiceUnavailable, HttpStatusCode.GatewayTimeout,
    ];

    public OceanTokenOptions Options => options;
    public IUsageTracker Usage => usage;

    public Task<GatewayResponse> PostJsonAsync(string path, string operation, string model, JsonNode body, TimeSpan timeout, CancellationToken ct)
    {
        var payload = body.ToJsonString();
        return SendAsync(() =>
        {
            var content = new StringContent(payload, new UTF8Encoding(false), "application/json");
            return content;
        }, path, operation, model, timeout, ct);
    }

    public Task<GatewayResponse> PostMultipartAsync(string path, string operation, string model, Func<MultipartFormDataContent> build, TimeSpan timeout, CancellationToken ct) =>
        SendAsync(build, path, operation, model, timeout, ct);

    /// <summary>Opens a response for streaming. Retries only apply before the first byte; a started stream is never replayed.</summary>
    public async Task<HttpResponseMessage> OpenStreamAsync(string path, string operation, string model, JsonNode body, TimeSpan headerTimeout, CancellationToken ct)
    {
        await GuardAsync(ct).ConfigureAwait(false);
        var key = await RequireKeyAsync(ct).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Post, options.BaseUrl.TrimEnd('/') + path)
        {
            Content = new StringContent(body.ToJsonString(), new UTF8Encoding(false), "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(headerTimeout);
        var started = Stopwatch.GetTimestamp();
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ProviderException(ErrorKind.Timeout, $"{operation} timed out waiting for the gateway.");
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderException(ErrorKind.Failed, $"{operation} could not reach the gateway ({ex.GetType().Name}).", inner: ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            response.Dispose();
            FinishRecord(operation, model, status, 1, Stopwatch.GetElapsedTime(started), ReadMeta(response.Headers), null, null, null, null, null);
            throw MapError(status, bytes, operation);
        }

        return response;
    }

    public void RecordStreamCompleted(string operation, string model, HttpResponseMessage response, TimeSpan elapsed, JsonElement? usageElement)
    {
        var meta = ReadMeta(response.Headers);
        var (prompt, completion, reasoning) = ParseTokens(usageElement);
        FinishRecord(operation, model, 200, 1, elapsed, meta, prompt, completion, reasoning, null, null);
    }

    private async Task<GatewayResponse> SendAsync(Func<HttpContent> makeContent, string path, string operation, string model, TimeSpan timeout, CancellationToken ct)
    {
        await GuardAsync(ct).ConfigureAwait(false);
        var key = await RequireKeyAsync(ct).ConfigureAwait(false);
        var maxAttempts = 1 + Math.Clamp(options.MaxRetries, 0, OceanTokenOptions.MaxRetryLimit);
        var overall = Stopwatch.StartNew();

        for (var attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);
            TimeSpan? retryAfter = null;
            string failure;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, options.BaseUrl.TrimEnd('/') + path) { Content = makeContent() };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeoutCts.Token).ConfigureAwait(false);
                var body = await response.Content.ReadAsByteArrayAsync(timeoutCts.Token).ConfigureAwait(false);
                var status = (int)response.StatusCode;
                var meta = ReadMeta(response.Headers);

                if (response.IsSuccessStatusCode)
                {
                    // Success is recorded by the caller (RecordSuccess) once it has parsed token/audio counts.
                    return new GatewayResponse(status, body, response.Content.Headers.ContentType?.ToString(), meta, overall.Elapsed, attempt);
                }

                if (!Transient.Contains(response.StatusCode) || attempt >= maxAttempts)
                {
                    FinishRecord(operation, model, status, attempt, overall.Elapsed, meta, null, null, null, null, null);
                    throw MapError(status, body, operation);
                }

                failure = $"HTTP {status}";
                retryAfter = response.Headers.RetryAfter?.Delta;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                if (attempt >= maxAttempts)
                {
                    throw new ProviderException(ErrorKind.Timeout, $"{operation} timed out after {attempt} attempt(s).");
                }

                failure = "timeout";
            }
            catch (HttpRequestException ex)
            {
                if (attempt >= maxAttempts)
                {
                    throw new ProviderException(ErrorKind.Failed, $"{operation} could not reach the gateway ({ex.GetType().Name}).", inner: ex);
                }

                failure = ex.GetType().Name;
            }

            var delay = retryAfter ?? Backoff(attempt);
            log.LogWarning("{Operation} attempt {Attempt}/{Max} failed ({Failure}); retrying in {DelayMs} ms", operation, attempt, maxAttempts, failure, (int)delay.TotalMilliseconds);
            await Task.Delay(delay, ct).ConfigureAwait(false);
        }
    }

    private TimeSpan Backoff(int attempt)
    {
        var exp = options.RetryBaseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1);
        var jitter = Random.Shared.NextDouble() * 0.25 * exp;
        return TimeSpan.FromMilliseconds(Math.Min(exp + jitter, options.RetryMaxDelay.TotalMilliseconds));
    }

    /// <summary>Refuses to start a request if the session spend limit has been reached.</summary>
    private Task GuardAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var totals = usage.Totals;
        if (totals.EstimatedSpendUsd >= options.SessionSpendLimitUsd)
        {
            throw new ProviderException(ErrorKind.Denied,
                $"Session spend limit reached ({totals.EstimatedSpendUsd:0.####} of {options.SessionSpendLimitUsd:0.####} USD). Requests are blocked until restart or the limit is raised.");
        }

        return Task.CompletedTask;
    }

    private async Task<string> RequireKeyAsync(CancellationToken ct)
    {
        var key = await secrets.GetAsync(OceanTokenOptions.ApiKeySecretName, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ProviderException(ErrorKind.NotConfigured,
                "No OceanToken API key is configured. Set RIDLEY__OceanToken__ApiKey or save the key in Ridley's settings.");
        }

        return key.Trim();
    }

    /// <summary>Records a successful call with the counts the caller parsed from the response.</summary>
    public void RecordSuccess(string operation, string model, GatewayResponse response,
        int? prompt = null, int? completion = null, int? reasoning = null, double? audioSeconds = null, int? inputChars = null) =>
        FinishRecord(operation, model, response.Status, response.Attempts, response.Elapsed, response.Meta, prompt, completion, reasoning, audioSeconds, inputChars);

    /// <summary>
    /// Writes one usage record. If the response had no cost header (Gemini TTS and streamed chat in testing),
    /// cost is derived from the change in the key's reported spend, which is only reliable while the key is
    /// not used concurrently elsewhere.
    /// </summary>
    private void FinishRecord(string operation, string model, int status, int attempts, TimeSpan latency, GatewayMeta meta,
        int? prompt, int? completion, int? reasoning, double? audioSeconds, int? inputChars)
    {
        var previousSpend = usage.LastKeySpendUsd;
        var cost = meta.Cost;
        if (cost is { } own && meta.KeySpend is { } now && previousSpend is { } prior)
        {
            // Spend that rose by more than this call cost belongs to earlier calls that reported no cost (streams).
            var gap = now - own - prior;
            if (gap > 0.0000001m)
            {
                usage.RecordUnattributedSpend(gap);
            }
        }

        var source = CostSource.Header;
        if (cost is null && meta.KeySpend is { } spend && previousSpend is { } before && spend >= before)
        {
            cost = spend - before;
            source = CostSource.DerivedFromSpendDelta;
        }
        else if (cost is null)
        {
            source = CostSource.Unknown;
        }

        usage.Record(new UsageRecord(DateTimeOffset.UtcNow, operation, model, status, attempts, latency, prompt, completion, reasoning,
            audioSeconds, inputChars, cost, source, meta.KeySpend, meta.KeyBudget, meta.RequestId));

        // Only counts, timings and money are logged. Never prompts, transcripts, audio, images or keys.
        log.LogInformation("{Operation} {Model} status={Status} attempts={Attempts} latencyMs={Latency} promptTok={Prompt} completionTok={Completion} costUsd={Cost} ({Source})",
            operation, model, status, attempts, (int)latency.TotalMilliseconds, prompt, completion, cost, source);
    }

    public static (int? Prompt, int? Completion, int? Reasoning) ParseTokens(JsonElement? usageElement)
    {
        if (usageElement is not { ValueKind: JsonValueKind.Object } u)
        {
            return (null, null, null);
        }

        int? Get(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : null;
        int? reasoning = u.TryGetProperty("completion_tokens_details", out var d) && d.ValueKind == JsonValueKind.Object ? Get(d, "reasoning_tokens") : null;
        return (Get(u, "prompt_tokens"), Get(u, "completion_tokens"), reasoning);
    }

    public static GatewayMeta ReadMeta(HttpResponseHeaders headers)
    {
        string? Header(string name) => headers.TryGetValues(name, out var v) ? v.FirstOrDefault() : null;
        decimal? Money(string name) => decimal.TryParse(Header(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
        return new GatewayMeta(Header("x-oceantoken-request-id"), Money("x-oceantoken-cost"), Money("x-oceantoken-key-spend"), Money("x-oceantoken-key-budget"));
    }

    /// <summary>Maps an error response to a typed exception. Only the gateway's error *code/message* is surfaced, never request content.</summary>
    internal static ProviderException MapError(int status, byte[] body, string operation)
    {
        string? message = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err) && err.TryGetProperty("message", out var m))
            {
                message = m.GetString();
            }
        }
        catch (JsonException)
        {
            // non-JSON error body: ignore, never echo it
        }

        var kind = status switch
        {
            401 or 403 => ErrorKind.Denied,
            404 => ErrorKind.NotFound,
            408 or 504 => ErrorKind.Timeout,
            400 or 422 => ErrorKind.Unsupported,
            402 => ErrorKind.Denied,
            _ => ErrorKind.Failed,
        };
        var friendly = status switch
        {
            401 => "The OceanToken API key was rejected.",
            402 => "The OceanToken key has no remaining budget.",
            403 => "The OceanToken key is not allowed to use this model or endpoint.",
            429 => "The gateway is rate limiting requests.",
            _ => $"The gateway rejected the {operation} request",
        };
        return new ProviderException(kind, message is null ? friendly : $"{friendly} ({Truncate(message, 200)})", status);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€šÃ‚Â¦";
}
