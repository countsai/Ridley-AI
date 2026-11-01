using System.Text.Json;
using Ridley.Core;

namespace Ridley.Providers.OceanToken;

/// <summary>Minimal RIFF/WAVE writer for PCM16 so push-to-talk audio can be uploaded.</summary>
public static class WavEncoder
{
    public static byte[] Encode(AudioClip clip)
    {
        var data = clip.Pcm16;
        using var ms = new MemoryStream(44 + data.Length);
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8);
        w.Write(36 + data.Length);
        w.Write("WAVEfmt "u8);
        w.Write(16);
        w.Write((short)1);
        w.Write((short)clip.Channels);
        w.Write(clip.SampleRate);
        w.Write(clip.SampleRate * clip.Channels * 2);
        w.Write((short)(clip.Channels * 2));
        w.Write((short)16);
        w.Write("data"u8);
        w.Write(data.Length);
        w.Write(data);
        return ms.ToArray();
    }
}

/// <summary>
/// Batch speech-to-text. The gateway has no live-streaming endpoint (measured), so this is upload-then-transcribe,
/// which suits push-to-talk. An empty transcript is a valid result (silence) and is returned as such.
/// </summary>
public sealed class OceanTokenSpeechToText(OceanTokenClient client) : ISpeechToTextProvider
{
    public string Name => "oceantoken";

    public async Task<Transcript> TranscribeAsync(AudioClip audio, SttOptions options, CancellationToken ct)
    {
        var wav = WavEncoder.Encode(audio);
        if (wav.LongLength > client.Options.MaxUploadBytes)
        {
            throw new ProviderException(ErrorKind.Unsupported, "That recording is too long to upload (limit 25 MB).");
        }

        var model = client.Options.SttModel;
        var response = await client.PostMultipartAsync("/audio/transcriptions", "stt", model, () =>
        {
            var form = new MultipartFormDataContent
            {
                { new StringContent(model), "model" },
                { new ByteArrayContent(wav) { Headers = { ContentType = new("audio/wav") } }, "file", "speech.wav" },
            };
            if (!string.IsNullOrWhiteSpace(options.Language))
            {
                form.Add(new StringContent(options.Language), "language");
            }

            return form;
        }, client.Options.SttTimeout, ct).ConfigureAwait(false);

        using var doc = JsonDocument.Parse(response.Body);
        var root = doc.RootElement;
        var text = root.TryGetProperty("text", out var t) ? t.GetString() ?? string.Empty : string.Empty;
        double? seconds = root.TryGetProperty("usage", out var u) && u.TryGetProperty("seconds", out var s) && s.TryGetDouble(out var d) ? d : null;
        client.RecordSuccess("stt", model, response, audioSeconds: seconds ?? (double)audio.Pcm16.Length / (audio.SampleRate * audio.Channels * 2));
        return new Transcript(text.Trim(), options.Language, null);
    }
}

public enum SynthesizedFormat { Wav, Mp3, Pcm16Mono24k }

/// <summary>Audio returned by synthesis. Playback is a separate concern (see evaluation report).</summary>
public sealed record SynthesizedAudio(byte[] Data, SynthesizedFormat Format)
{
    public override string ToString() => $"SynthesizedAudio({Format}, {Data.Length} bytes)";
}

/// <summary>
/// Text-to-speech synthesis only. It deliberately does NOT implement ITextToSpeechProvider: that contract promises
/// to speak (play) the text and cancel playback, which a synthesis API cannot do on its own.
/// Measured limits: no real audio streaming, voice required and not discoverable, and cancelling a request does
/// not avoid the charge, so callers should synthesise sentence by sentence and cancel playback, not requests.
/// </summary>
public sealed class OceanTokenTextToSpeech(OceanTokenClient client)
{
    public async Task<SynthesizedAudio> SynthesizeAsync(string text, TtsOptions options, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var voice = options.Voice ?? client.Options.TtsVoice;
        if (string.IsNullOrWhiteSpace(voice))
        {
            throw new ProviderException(ErrorKind.NotConfigured, "No text-to-speech voice is configured. The gateway requires one and cannot list them.");
        }

        var model = client.Options.TtsModel;
        // The gateway rejects formats a model lacks, but silently returns WAV when asked for MP3 on Gemini TTS,
        // so ask for each model's native format and detect what actually came back.
        var requested = model.StartsWith("x-ai/", StringComparison.Ordinal) ? "mp3" : "wav";
        var body = new System.Text.Json.Nodes.JsonObject
        {
            ["model"] = model,
            ["input"] = text,
            ["voice"] = voice,
            ["response_format"] = requested,
        };

        var response = await client.PostJsonAsync("/audio/speech", "tts", model, body, client.Options.TtsTimeout, ct).ConfigureAwait(false);
        client.RecordSuccess("tts", model, response, inputChars: text.Length);
        return new SynthesizedAudio(response.Body, Detect(response.Body, response.ContentType));
    }

    internal static SynthesizedFormat Detect(byte[] data, string? contentType)
    {
        if (data.Length >= 12 && data[0] == 'R' && data[1] == 'I' && data[2] == 'F' && data[3] == 'F')
        {
            return SynthesizedFormat.Wav;
        }

        if (data.Length >= 3 && ((data[0] == 'I' && data[1] == 'D' && data[2] == '3') || (data[0] == 0xFF && (data[1] & 0xE0) == 0xE0)))
        {
            return SynthesizedFormat.Mp3;
        }

        if (contentType?.Contains("pcm", StringComparison.OrdinalIgnoreCase) == true)
        {
            return SynthesizedFormat.Pcm16Mono24k;
        }

        throw new ProviderException(ErrorKind.Failed, "The gateway returned audio in an unrecognised format.");
    }
}
