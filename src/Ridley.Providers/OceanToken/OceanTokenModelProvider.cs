using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ridley.Core;

namespace Ridley.Providers.OceanToken;

/// <summary>
/// OpenAI-compatible chat completions via the OceanToken gateway. Output is raw model data: tool-call arguments are
/// returned as untrusted JSON and must pass <see cref="IToolArgumentValidator"/> before anything can execute.
/// </summary>
public sealed class OceanTokenModelProvider(OceanTokenClient client) : IModelProvider
{
    private const string Path = "/chat/completions";

    public string Name => "oceantoken";

    // Streaming exists, but Gemini through the gateway delivers its answer in one burst (measured), so callers
    // must not rely on incremental text for latency with that model.
    public ModelCapabilities Capabilities => ModelCapabilities.ToolCalling | ModelCapabilities.Vision | ModelCapabilities.Streaming;

    public async Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct)
    {
        var model = client.Options.ChatModel;
        var body = BuildBody(request, model, client.Options.ChatMaxOutputTokens, stream: false);
        var response = await client.PostJsonAsync(Path, "chat", model, body, client.Options.ChatTimeout, ct).ConfigureAwait(false);

        using var doc = JsonDocument.Parse(response.Body);
        var root = doc.RootElement;
        usageOf(root, out var usage);
        var (prompt, completion, reasoning) = OceanTokenClient.ParseTokens(usage);
        client.RecordSuccess("chat", model, response, prompt, completion, reasoning);

        if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
        {
            throw new ProviderException(ErrorKind.Failed, "The model returned no choices.");
        }

        var choice = choices[0];
        var message = choice.GetProperty("message");
        var finish = choice.TryGetProperty("finish_reason", out var f) ? f.GetString() : null;
        var text = message.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;

        var calls = new List<ModelToolCall>();
        if (message.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
        {
            foreach (var call in toolCalls.EnumerateArray())
            {
                var fn = call.GetProperty("function");
                var name = fn.GetProperty("name").GetString() ?? string.Empty;
                calls.Add(new ModelToolCall(name, ParseArguments(fn.TryGetProperty("arguments", out var a) ? a : default)));
            }
        }

        if (string.IsNullOrEmpty(text) && calls.Count == 0 && finish == "length")
        {
            // Measured failure mode: reasoning tokens consumed the whole output budget, leaving content=null.
            throw new ProviderException(ErrorKind.Failed,
                "The model used its whole output budget on reasoning and produced no answer. Increase ChatMaxOutputTokens.");
        }

        return new ModelResponse(text, calls);
    }

    public async IAsyncEnumerable<ModelDelta> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var model = client.Options.ChatModel;
        var body = BuildBody(request, model, client.Options.ChatMaxOutputTokens, stream: true);
        body["stream_options"] = new JsonObject { ["include_usage"] = true };

        var started = Stopwatch.GetTimestamp();
        using var response = await client.OpenStreamAsync(Path, "chat-stream", model, body, client.Options.ChatTimeout, ct).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream);

        JsonElement? usage = null;
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var payload = line[5..].Trim();
            if (payload == "[DONE]")
            {
                break;
            }

            using var chunk = JsonDocument.Parse(payload);
            if (chunk.RootElement.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
            {
                usage = u.Clone();
            }

            if (chunk.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0
                && choices[0].TryGetProperty("delta", out var delta)
                && delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String
                && content.GetString() is { Length: > 0 } piece)
            {
                yield return new ModelDelta(piece);
            }

            // NOTE: streamed tool-call fragments are not surfaced; ModelDelta cannot carry them (see evaluation report).
        }

        client.RecordStreamCompleted("chat-stream", model, response, Stopwatch.GetElapsedTime(started), usage);
    }

    private static void usageOf(JsonElement root, out JsonElement? usage) =>
        usage = root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object ? u : null;

    /// <summary>
    /// Gateway sends arguments as a JSON *string*. If it parses, return the object; if it does not, return the raw
    /// text as a JSON string value, which can never satisfy an object schema and is therefore rejected by validation.
    /// </summary>
    internal static JsonElement ParseArguments(JsonElement raw)
    {
        if (raw.ValueKind == JsonValueKind.Object)
        {
            return raw.Clone();
        }

        var text = raw.ValueKind == JsonValueKind.String ? raw.GetString() ?? string.Empty : string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return JsonSerializer.SerializeToElement(new { });
        }

        try
        {
            using var parsed = JsonDocument.Parse(text);
            return parsed.RootElement.Clone();
        }
        catch (JsonException)
        {
            return JsonSerializer.SerializeToElement(text);
        }
    }

    internal static JsonObject BuildBody(ModelRequest request, string model, int maxTokens, bool stream)
    {
        var messages = new JsonArray();
        var lastUserIndex = -1;
        for (var i = 0; i < request.Messages.Count; i++)
        {
            if (request.Messages[i].Role == MessageRole.User)
            {
                lastUserIndex = i;
            }
        }

        for (var i = 0; i < request.Messages.Count; i++)
        {
            var m = request.Messages[i];
            var role = m.Role switch { MessageRole.System => "system", MessageRole.User => "user", MessageRole.Assistant => "assistant", _ => "tool" };
            JsonNode content = m.Content;
            if (i == lastUserIndex && request.Images is { Count: > 0 })
            {
                var parts = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = m.Content } };
                foreach (var image in request.Images)
                {
                    parts.Add(new JsonObject
                    {
                        ["type"] = "image_url",
                        ["image_url"] = new JsonObject { ["url"] = "data:image/png;base64," + Convert.ToBase64String(image.PngBytes) },
                    });
                }

                content = parts;
            }

            messages.Add(new JsonObject { ["role"] = role, ["content"] = content });
        }

        var body = new JsonObject { ["model"] = model, ["messages"] = messages, ["max_tokens"] = maxTokens };
        if (stream)
        {
            body["stream"] = true;
        }
        if (request.Tools is { Count: > 0 })
        {
            var tools = new JsonArray();
            foreach (var tool in request.Tools)
            {
                tools.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = tool.Name,
                        ["description"] = tool.Description,
                        ["parameters"] = JsonNode.Parse(tool.JsonSchema.GetRawText()),
                    },
                });
            }

            body["tools"] = tools;
            body["tool_choice"] = "auto";
        }

        return body;
    }
}
