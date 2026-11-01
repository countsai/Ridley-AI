namespace Ridley.Providers.OceanToken;

/// <summary>Everything here is configuration, not code: model IDs, timeouts, limits. No secrets.</summary>
public sealed record OceanTokenOptions
{
    public const string ApiKeySecretName = "OceanToken:ApiKey";

    public string BaseUrl { get; init; } = "https://api.oceantoken.ai/v1";

    // Verified against the live gateway (see docs/OCEANTOKEN_EVALUATION.md).
    public string ChatModel { get; init; } = "google/gemini-3.8-flash";
    public string SttModel { get; init; } = "nvidia/nemotron-3.5-asr-streaming-multilingual-0.6b";
    public string TtsModel { get; init; } = "x-ai/grok-voice-tts-1.0";

    /// <summary>TTS requires a voice and the gateway offers no way to list them; it must be configured.</summary>
    public string? TtsVoice { get; init; }

    /// <summary>
    /// Reasoning models spend hidden tokens before answering (measured: 60-450 on trivial prompts). A small
    /// limit returns empty content with finish_reason=length, so the default is deliberately generous.
    /// </summary>
    public int ChatMaxOutputTokens { get; init; } = 1500;

    public TimeSpan ChatTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan SttTimeout { get; init; } = TimeSpan.FromSeconds(45);
    public TimeSpan TtsTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Retries after the first attempt. Bounded: never more than <see cref="MaxRetryLimit"/>.</summary>
    public int MaxRetries { get; init; } = 2;
    public const int MaxRetryLimit = 4;
    public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromMilliseconds(500);
    public TimeSpan RetryMaxDelay { get; init; } = TimeSpan.FromSeconds(8);

    /// <summary>
    /// Client-side backstop in addition to the server-side key budget: once this much spend is recorded in the
    /// current process, further requests are refused. Aborting a request does NOT stop the charge (measured).
    /// </summary>
    public decimal SessionSpendLimitUsd { get; init; } = 0.50m;

    public long MaxUploadBytes { get; init; } = 25L * 1024 * 1024;
}
