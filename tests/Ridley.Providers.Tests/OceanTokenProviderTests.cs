using System.Net;
using System.Text.Json;
using Ridley.Core;
using Ridley.Providers.OceanToken;

namespace Ridley.Providers.Tests;

public class OceanTokenProviderTests
{
    private static OceanTokenClient Client(Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> respond, OceanTokenOptions? options = null)
    {
        var http = new HttpClient(new FakeHandler(respond)) { Timeout = Timeout.InfiniteTimeSpan };
        return new OceanTokenClient(http, new FakeSecrets("sk-test"), new InMemoryUsageTracker(),
            options ?? new OceanTokenOptions { RetryBaseDelay = TimeSpan.FromMilliseconds(1) }, new ListLogger<OceanTokenClient>());
    }

    private static Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> Reply(string json) =>
        (_, _, _) => Task.FromResult(FakeHandler.Json(HttpStatusCode.OK, json));

    private static ModelRequest Ask(string text) => new([new ModelMessage(MessageRole.User, text)]);

    private static JsonElement Schema() => JsonDocument.Parse("""{"type":"object","properties":{"direction":{"type":"string"}},"required":["direction"]}""").RootElement.Clone();

    // ---- chat ----
    [Fact]
    public async Task Tool_call_arguments_string_becomes_untrusted_json()
    {
        var provider = new OceanTokenModelProvider(Client(Reply("""
            {"choices":[{"finish_reason":"tool_calls","message":{"role":"assistant","tool_calls":[
              {"id":"call_1","function":{"name":"scroll","arguments":"{\"direction\": \"down\", \"amount\": 3}"}}]}}],
             "usage":{"prompt_tokens":60,"completion_tokens":113,"completion_tokens_details":{"reasoning_tokens":95}}}
            """)));
        var response = await provider.CompleteAsync(Ask("scroll down"), default);
        var call = Assert.Single(response.ToolCalls);
        Assert.Equal("scroll", call.ToolName);
        Assert.Equal(JsonValueKind.Object, call.RawArguments.ValueKind);
        Assert.Equal("down", call.RawArguments.GetProperty("direction").GetString());
    }

    [Fact]
    public async Task Malformed_tool_arguments_cannot_look_like_an_object()
    {
        var provider = new OceanTokenModelProvider(Client(Reply("""
            {"choices":[{"finish_reason":"tool_calls","message":{"tool_calls":[{"function":{"name":"scroll","arguments":"{not json"}}]}}]}
            """)));
        var call = Assert.Single((await provider.CompleteAsync(Ask("x"), default)).ToolCalls);
        Assert.Equal(JsonValueKind.String, call.RawArguments.ValueKind); // a schema validator will reject this
    }

    [Fact]
    public async Task Reasoning_that_eats_the_whole_budget_is_an_error_not_an_empty_answer()
    {
        var provider = new OceanTokenModelProvider(Client(Reply("""{"choices":[{"finish_reason":"length","message":{"role":"assistant","content":null}}]}""")));
        var ex = await Assert.ThrowsAsync<ProviderException>(() => provider.CompleteAsync(Ask("x"), default));
        Assert.Contains("output budget", ex.Message);
    }

    [Fact]
    public async Task Plain_text_reply_is_returned()
    {
        var provider = new OceanTokenModelProvider(Client(Reply("""{"choices":[{"finish_reason":"stop","message":{"content":"Paris."}}]}""")));
        var response = await provider.CompleteAsync(Ask("capital of France?"), default);
        Assert.Equal("Paris.", response.Text);
        Assert.Empty(response.ToolCalls);
    }

    [Fact]
    public void Request_body_carries_tools_images_and_a_generous_token_limit()
    {
        var capture = new ScreenCapture([1, 2, 3], new Rect(0, 0, 10, 10), DateTimeOffset.UtcNow);
        var request = new ModelRequest(
            [new ModelMessage(MessageRole.System, "sys"), new ModelMessage(MessageRole.User, "what is this?")],
            [new ToolDefinition("scroll", "Scroll", Schema())],
            [capture]);

        var body = OceanTokenModelProvider.BuildBody(request, "google/gemini-3.8-flash", 1500, stream: false);

        Assert.Equal(1500, (int)body["max_tokens"]!);
        Assert.Equal("auto", (string)body["tool_choice"]!);
        Assert.Null(body["stream"]);
        Assert.Equal("sys", (string)body["messages"]![0]!["content"]!); // system message stays plain text
        var userParts = body["messages"]![1]!["content"]!.AsArray();
        Assert.Equal("image_url", (string)userParts[1]!["type"]!);
        Assert.StartsWith("data:image/png;base64,", (string)userParts[1]!["image_url"]!["url"]!);
        Assert.Equal("scroll", (string)body["tools"]![0]!["function"]!["name"]!);
    }

    [Fact]
    public async Task Streaming_yields_text_and_records_usage_from_the_final_chunk()
    {
        const string sse = "data: {\"choices\":[{\"delta\":{\"content\":\"1 \"}}]}\n\ndata: {\"choices\":[{\"delta\":{\"content\":\"2\"}}]}\n\n" +
                           "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":11,\"completion_tokens\":146}}\n\ndata: [DONE]\n\n";
        var usage = new InMemoryUsageTracker();
        var http = new HttpClient(new FakeHandler((_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse) })));
        var provider = new OceanTokenModelProvider(new OceanTokenClient(http, new FakeSecrets("sk-test"), usage, new OceanTokenOptions(), new ListLogger<OceanTokenClient>()));

        var text = new List<string>();
        await foreach (var delta in provider.StreamAsync(Ask("count"), default)) { text.Add(delta.TextChunk!); }

        Assert.Equal("1 2", string.Concat(text));
        Assert.Equal(11, usage.Recent(1).Single().PromptTokens);
    }

    // ---- speech to text ----
    [Fact]
    public void Wav_encoder_writes_a_valid_header()
    {
        var wav = WavEncoder.Encode(new AudioClip(new byte[3200], 16000, 1));
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(wav, 8, 4));
        Assert.Equal(16000, BitConverter.ToInt32(wav, 24));
        Assert.Equal(44 + 3200, wav.Length);
    }

    [Fact]
    public async Task Transcription_returns_text_and_records_audio_seconds()
    {
        var usage = new InMemoryUsageTracker();
        var http = new HttpClient(new FakeHandler(Reply("""{"text":" Open Chrome. ","usage":{"type":"duration","seconds":3.255}}"""))) ;
        var stt = new OceanTokenSpeechToText(new OceanTokenClient(http, new FakeSecrets("sk-test"), usage, new OceanTokenOptions(), new ListLogger<OceanTokenClient>()));
        var transcript = await stt.TranscribeAsync(new AudioClip(new byte[32000], 16000, 1), new SttOptions("en"), default);
        Assert.Equal("Open Chrome.", transcript.Text);
        Assert.Equal(3.255, usage.Recent(1).Single().AudioSecondsIn);
    }

    [Fact]
    public async Task Silence_gives_an_empty_transcript_not_an_error()
    {
        var stt = new OceanTokenSpeechToText(Client(Reply("""{"text":"","usage":{"seconds":2.0}}""")));
        Assert.Equal("", (await stt.TranscribeAsync(new AudioClip(new byte[64000], 16000, 1), new SttOptions(), default)).Text);
    }

    [Fact]
    public async Task Oversized_upload_is_refused_before_any_network_call()
    {
        var handler = new FakeHandler(Reply("{}"));
        var client = new OceanTokenClient(new HttpClient(handler), new FakeSecrets("sk-test"), new InMemoryUsageTracker(),
            new OceanTokenOptions { MaxUploadBytes = 1000 }, new ListLogger<OceanTokenClient>());
        var ex = await Assert.ThrowsAsync<ProviderException>(() => new OceanTokenSpeechToText(client).TranscribeAsync(new AudioClip(new byte[5000], 16000, 1), new SttOptions(), default));
        Assert.Equal(ErrorKind.Unsupported, ex.Kind);
        Assert.Equal(0, handler.Calls);
    }

    // ---- text to speech ----
    [Fact]
    public async Task Tts_without_a_configured_voice_says_so_instead_of_guessing()
    {
        var ex = await Assert.ThrowsAsync<ProviderException>(() => new OceanTokenTextToSpeech(Client(Reply("{}"))).SynthesizeAsync("hello", new TtsOptions(), default));
        Assert.Equal(ErrorKind.NotConfigured, ex.Kind);
    }

    [Theory]
    [InlineData("x-ai/grok-voice-tts-1.0", "mp3")]
    [InlineData("google/gemini-3.8-flash-lite-tts", "wav")]
    public async Task Tts_requests_each_models_native_format(string model, string expectedFormat)
    {
        string? sent = null;
        var http = new HttpClient(new FakeHandler(async (req, _, _) =>
        {
            sent = await req.Content!.ReadAsStringAsync();
            var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x41, 0x56, 0x45]) };
            r.Content.Headers.ContentType = new("audio/wav");
            return r;
        }));
        var client = new OceanTokenClient(http, new FakeSecrets("sk-test"), new InMemoryUsageTracker(),
            new OceanTokenOptions { TtsModel = model, TtsVoice = "eve" }, new ListLogger<OceanTokenClient>());
        var audio = await new OceanTokenTextToSpeech(client).SynthesizeAsync("Opening Chrome now.", new TtsOptions(), default);
        Assert.Contains($"\"response_format\":\"{expectedFormat}\"", sent);
        Assert.Equal(SynthesizedFormat.Wav, audio.Format); // trust the bytes, not what we asked for
    }

    [Fact]
    public void Audio_format_is_detected_from_the_bytes()
    {
        Assert.Equal(SynthesizedFormat.Wav, OceanTokenTextToSpeech.Detect([0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x41, 0x56, 0x45], null));
        Assert.Equal(SynthesizedFormat.Mp3, OceanTokenTextToSpeech.Detect([0xFF, 0xF3, 0xC4, 0xC4], "audio/mpeg"));
        Assert.Equal(SynthesizedFormat.Mp3, OceanTokenTextToSpeech.Detect([0x49, 0x44, 0x33, 4], null));
        Assert.Equal(SynthesizedFormat.Pcm16Mono24k, OceanTokenTextToSpeech.Detect([0, 0, 0, 0], "audio/pcm;rate=24000;channels=1"));
        Assert.Throws<ProviderException>(() => OceanTokenTextToSpeech.Detect([1, 2, 3, 4], "text/html"));
    }

    [Fact]
    public void Synthesised_audio_does_not_dump_bytes_into_logs()
    {
        Assert.DoesNotContain("1, 2", new SynthesizedAudio([1, 2, 3], SynthesizedFormat.Wav).ToString());
    }
}
