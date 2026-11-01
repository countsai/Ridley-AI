// OceanToken integration spike. NOT part of the product build (not in Ridley.sln).
//   store-key <envFile>                 : move OCEANTOKEN_API_KEY from an env file into the DPAPI secret store
//   live <outDir> <wav> <png>           : exercise the real provider classes against the gateway
// Prints only timings, counts, costs and model *outputs*. Never the key, and never prompts/audio/images.
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Ridley.Core;
using Ridley.Providers.OceanToken;
using Ridley.Storage;

var command = args.Length > 0 ? args[0] : "";
var secrets = new DpapiSecretStore(new SqliteDatabase(SqliteDatabase.DefaultPath));

if (command == "store-key")
{
    var line = File.ReadAllLines(args[1]).FirstOrDefault(l => l.StartsWith("OCEANTOKEN_API_KEY=", StringComparison.Ordinal));
    var key = line?[(line.IndexOf('=') + 1)..].Trim().Trim('"');
    if (string.IsNullOrEmpty(key)) { Console.WriteLine("No OCEANTOKEN_API_KEY found."); return 1; }
    await secrets.SetAsync(OceanTokenOptions.ApiKeySecretName, key, default);
    var readBack = await secrets.GetAsync(OceanTokenOptions.ApiKeySecretName, default);
    Console.WriteLine($"Stored in DPAPI store. Round-trip ok: {readBack == key}. (key length {key.Length}, not displayed)");
    return 0;
}

if (command != "live") { Console.WriteLine("usage: store-key <envFile> | live <outDir> <wav> <png>"); return 2; }

var outDir = args[1]; var wavPath = args[2]; var pngPath = args[3];
Directory.CreateDirectory(outDir);
using var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; }).SetMinimumLevel(LogLevel.Information));
var usage = new InMemoryUsageTracker();
var options = new OceanTokenOptions { TtsVoice = "eve", SessionSpendLimitUsd = 0.25m };
using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
OceanTokenClient MakeClient(ISecretStore s, OceanTokenOptions? o = null) => new(http, s, usage, o ?? options, loggerFactory.CreateLogger<OceanTokenClient>());
var client = MakeClient(secrets);
var chat = new OceanTokenModelProvider(client);
var stt = new OceanTokenSpeechToText(client);
var tts = new OceanTokenTextToSpeech(client);

async Task Step(string name, Func<Task> body)
{
    Console.WriteLine($"\n=== {name}");
    var sw = Stopwatch.StartNew();
    try { await body(); Console.WriteLine($"   -> OK in {sw.ElapsedMilliseconds} ms"); }
    catch (ProviderException ex) { Console.WriteLine($"   -> ProviderException kind={ex.Kind} status={ex.StatusCode} after {sw.ElapsedMilliseconds} ms: {ex.Message}"); }
    catch (OperationCanceledException) { Console.WriteLine($"   -> Cancelled after {sw.ElapsedMilliseconds} ms (OperationCanceledException, as designed)"); }
}

var scrollSchema = JsonDocument.Parse("""{"type":"object","properties":{"direction":{"type":"string","enum":["up","down"]},"amount":{"type":"integer","minimum":1,"maximum":20}},"required":["direction"]}""").RootElement.Clone();
var tools = new[] { new ToolDefinition("scroll", "Scroll the active window", scrollSchema) };

await Step("chat: plain", async () =>
{
    var r = await chat.CompleteAsync(new ModelRequest([new ModelMessage(MessageRole.User, "What is the capital of France? One word.")]), default);
    Console.WriteLine($"   text: {r.Text}");
});

await Step("chat: tool call", async () =>
{
    var r = await chat.CompleteAsync(new ModelRequest([new ModelMessage(MessageRole.User, "Scroll down a bit")], tools), default);
    foreach (var c in r.ToolCalls) Console.WriteLine($"   tool: {c.ToolName} args(raw, UNVALIDATED): {c.RawArguments.GetRawText()}");
    if (r.ToolCalls.Count == 0) Console.WriteLine($"   no tool call; text: {r.Text}");
});

await Step("chat: image input (synthetic screenshot)", async () =>
{
    var shot = new ScreenCapture(await File.ReadAllBytesAsync(pngPath), new Rect(0, 0, 800, 450), DateTimeOffset.UtcNow);
    var r = await chat.CompleteAsync(new ModelRequest([new ModelMessage(MessageRole.User, "List the button labels you can see, comma separated.")], Images: [shot]), default);
    Console.WriteLine($"   text: {r.Text}");
});

await Step("chat: streaming", async () =>
{
    var first = -1L; var sw = Stopwatch.StartNew(); var parts = 0; var text = "";
    await foreach (var d in chat.StreamAsync(new ModelRequest([new ModelMessage(MessageRole.User, "Count from 1 to 6, separated by spaces.")]), default))
    { if (first < 0) first = sw.ElapsedMilliseconds; parts++; text += d.TextChunk; }
    Console.WriteLine($"   chunks={parts} first-chunk={first} ms total={sw.ElapsedMilliseconds} ms text='{text}'");
});

await Step("stt: transcribe synthetic speech", async () =>
{
    var bytes = await File.ReadAllBytesAsync(wavPath);
    var clip = new AudioClip(bytes[44..], 16000, 1);
    var t = await stt.TranscribeAsync(clip, new SttOptions("en"), default);
    Console.WriteLine($"   transcript: {t.Text}");
});

foreach (var (model, voice) in new[] { ("x-ai/grok-voice-tts-1.0", "eve"), ("google/gemini-3.8-flash-lite-tts", "Kore") })
{
    await Step($"tts: {model} voice={voice}", async () =>
    {
        var c = MakeClient(secrets, options with { TtsModel = model, TtsVoice = voice });
        var audio = await new OceanTokenTextToSpeech(c).SynthesizeAsync("Opening Chrome now.", new TtsOptions(), default);
        var ext = audio.Format == SynthesizedFormat.Mp3 ? "mp3" : audio.Format == SynthesizedFormat.Wav ? "wav" : "pcm";
        var path = Path.Combine(outDir, $"spike-{model.Replace('/', '_')}.{ext}");
        await File.WriteAllBytesAsync(path, audio.Data);
        Console.WriteLine($"   {audio} -> {path}");
    });
}

await Step("tts: missing voice -> NotConfigured (no network call)", async () =>
    await new OceanTokenTextToSpeech(MakeClient(secrets, options with { TtsVoice = null })).SynthesizeAsync("hi", new TtsOptions(), default));

await Step("auth: wrong key -> mapped error, not retried", async () =>
    await new OceanTokenModelProvider(MakeClient(new StaticSecret("sk-this-is-not-a-real-key"))).CompleteAsync(new ModelRequest([new ModelMessage(MessageRole.User, "hi")]), default));

await Step("cancellation: cancel chat after 300 ms", async () =>
{
    using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
    await chat.CompleteAsync(new ModelRequest([new ModelMessage(MessageRole.User, "Write a 300 word essay about rivers.")]), cts.Token);
});

await Step("budget guard: limit lowered below current spend -> Denied locally", async () =>
    await new OceanTokenModelProvider(MakeClient(secrets, options with { SessionSpendLimitUsd = 0.000001m })).CompleteAsync(new ModelRequest([new ModelMessage(MessageRole.User, "hi")]), default));

Console.WriteLine("\n=== USAGE (this process)");
Console.WriteLine($"{"operation",-12} {"model",-48} {"status",6} {"ms",6} {"att",3} {"promptTok",9} {"complTok",8} {"reasonTok",9} {"costUSD",11} source");
foreach (var r in usage.Recent(100))
    Console.WriteLine($"{r.Operation,-12} {r.Model,-48} {r.Status,6} {(int)r.Latency.TotalMilliseconds,6} {r.Attempts,3} {r.PromptTokens,9} {r.CompletionTokens,8} {r.ReasoningTokens,9} {r.CostUsd,11:0.########} {r.CostSource}");
var t = usage.Totals;
Console.WriteLine($"\nTOTAL calls={t.Calls} known-cost={t.KnownCostUsd:0.########} USD, calls-with-unknown-cost={t.CallsWithUnknownCost}, key-budget={usage.Recent(1).LastOrDefault()?.KeyBudgetUsd}, key-spend-now={usage.LastKeySpendUsd}");
return 0;

sealed class StaticSecret(string value) : ISecretStore
{
    public Task<string?> GetAsync(string name, CancellationToken ct) => Task.FromResult<string?>(value);
    public Task SetAsync(string name, string v, CancellationToken ct) => Task.CompletedTask;
    public Task DeleteAsync(string name, CancellationToken ct) => Task.CompletedTask;
}
