# OceanToken Provider Evaluation (integration spike)

Date: 2026-11-02 · Status: **spike complete, awaiting CTO approval.** No Phase 2 work started. Permission gate and authorised-execution architecture untouched.

All results below were measured against `https://api.oceantoken.ai/v1` with the real key, not taken from model names or marketing. Credentials are redacted (`sk-***`). Test inputs were synthetic (locally generated speech, a drawn image); no personal data, real screen content or microphone audio was sent. Samples are small (n=1 unless stated, n=5 for latency), so treat figures as indicative.

## 1. Verified model IDs

`GET /v1/models` → 200, **513 models**. All six selected IDs are listed. The listing returns only `id, object, created, owned_by`, with **no capability, price or voice metadata**, so listing proves nothing about features; each was tested separately.

| Role | Model ID | Listed | Works through gateway |
|---|---|---|---|
| LLM primary | `google/gemini-3.8-flash` | yes | chat, tools, structured output, image, stream |
| LLM alt | `qwen/qwen3.8-flash` | yes | same |
| STT primary | `nvidia/nemotron-3.5-asr-streaming-multilingual-0.6b` | yes | batch upload only |
| STT alt | `qwen/qwen3-asr-0.6b` | yes | batch upload only |
| TTS primary | `google/gemini-3.8-flash-lite-tts` | yes | yes, voice required |
| TTS alt | `x-ai/grok-voice-tts-1.0` | yes | yes, voice required |

## 2. Endpoint compatibility

OpenAI-compatible: `POST /chat/completions`, `POST /audio/transcriptions` (multipart), `POST /audio/speech`. Not present: `/realtime`, `/audio/transcriptions/stream`, `/audio/stream`, `/audio/voices`, `/voices`, `/models/{id}` (all 404 "Unknown endpoint"); WebSocket upgrade on `/realtime` → 403.

Every response carries `x-oceantoken-request-id`, `x-oceantoken-duration-ms`, `x-oceantoken-model`, `x-oceantoken-key-budget`, `x-oceantoken-key-spend`, and usually `x-oceantoken-cost`.

## 3. LLM results (Gemini primary, Qwen alternative)

### Reasoning models: output budget trap
```
POST /v1/chat/completions
{"model":"google/gemini-3.8-flash","messages":[{"role":"user","content":"Reply with exactly: pong"}],"max_tokens":20}
→ 200 {"choices":[{"finish_reason":"length","message":{"role":"assistant","content":null}}],
       "usage":{"prompt_tokens":5,"completion_tokens":17,"completion_tokens_details":{"reasoning_tokens":17}}}
```
Hidden reasoning consumed the whole budget; the answer was `null`. With `max_tokens:500` the reply is `pong` after 63-86 reasoning tokens. `reasoning_effort` (`minimal`/`low`/`none`) is accepted but made no meaningful difference (63-86 tokens). **Default output budget set to 1500 and a length-with-no-content result is raised as an error, never returned as an empty answer.** Qwen returns its reasoning separately in `message.reasoning_content`.

### Tool calling: works on both
```
{"model":"google/gemini-3.8-flash","messages":[{"role":"user","content":"Scroll down a bit"}],
 "tools":[{"type":"function","function":{"name":"scroll","description":"Scroll the active window",
   "parameters":{"type":"object","properties":{"direction":{"type":"string","enum":["up","down"]},"amount":{"type":"integer","minimum":1,"maximum":20}},"required":["direction"]}}},
  {"type":"function","function":{"name":"open_application", ...}}],"tool_choice":"auto","max_tokens":800}
→ 200 finish_reason=tool_calls
  message.tool_calls[0] = {"id":"call_9c93…__thought__AY89a1…(~700 chars)","function":{"name":"scroll","arguments":"{\"direction\": \"down\", \"amount\": 3}"}}
```

| Prompt | Gemini 3.8 Flash | Qwen 3.8 Flash |
|---|---|---|
| "Scroll down a bit" | `scroll{down,3}` | `scroll{down,3}` |
| "What is the capital of France?" | answered, no tool | answered, no tool |
| "Open Chrome and then scroll down" | **only** `open_application{"Google Chrome"}` | **only** `open_application{"Google Chrome"}` |
| "Open the thing" (ambiguous) | **asked a clarifying question** | **guessed** `open_application{"Files"}` |

Findings that shape the design:
- `arguments` is a **JSON string**, not an object: it must be parsed, then validated. Malformed text is surfaced as a non-object value that fails any schema (tested).
- **One tool call per response for a compound request**, on both models. Multi-step plans need our own loop (UNDERSTAND→PLAN→…), not a single model call.
- Gemini tool-call IDs are ~700 characters (embed a `thought_signature`). A tool-result turn echoing the ID verbatim worked (200, "I've scrolled down a bit."). IDs must be stored and echoed unmodified, never truncated or logged.
- Ambiguity: Gemini clarified, Qwen guessed (one sample each). Do not rely on the model to ask; the planner needs its own deterministic ambiguity rule.

### Structured output: works on both
`response_format:{"type":"json_schema","json_schema":{"name":"intent","strict":true,"schema":{…}}}` → Gemini `{"intent":"explain","confidence":0.98,"target":"this diagram"}`; Qwen returned valid but oddly spaced JSON with all fields. `json_object` mode also works. `strict` enforcement cannot be confirmed from outside, so always validate client-side.

### Streaming: works, but Gemini does not really stream
`stream:true` + `stream_options:{"include_usage":true}` → `text/event-stream`, `data: {...}` lines, `data: [DONE]`, and a final usage chunk.
- **Gemini:** 5 SSE lines total, first byte 2.26 s of 2.43 s total: the answer arrives in one burst after thinking. Tool call arrives whole.
- **Qwen:** 22 lines for text; first byte 1.4 s of 1.9 s. Tool-call arguments are **fragmented** (`''`, `'{"amount": 5'`, `', "direction": "down'`), so a client must reassemble.
- Streamed responses carry **no cost header** (see §7).

### Image input: works on both
`content:[{"type":"text",…},{"type":"image_url","image_url":{"url":"data:image/png;base64,…"}}]` with a synthetic 800×450 screenshot (Submit button at x40 y200 160×50; Cancel at x600 y360 160×50).
- Gemini: read all 5 text items, boxes **[40,200,160,50]** and **[600,360,160,50]**: exact. 1,100 image tokens, 6.2 s (11.3 s in the spike run, with ~1,400 reasoning tokens).
- Qwen: Submit `[40,198,208,50]` (width wrong by 48 px), Cancel exact. 352 image tokens, 4.1 s.
Gemini's boxes are good enough to anchor overlay annotations; Qwen's need verification against OCR/UIA geometry.

## 4. Speech-to-text results

```
POST /v1/audio/transcriptions   (multipart: model=<id>, file=@speech.wav;type=audio/wav)
→ 200 {"text":"Open Chrome and scroll down the page.","usage":{"type":"duration","seconds":3.255}}
```
- **Accuracy:** both models transcribed the 3.3 s and 9.4 s synthetic utterances exactly (modulo capitalisation of "Downloads"). *Synthetic TTS speech is easier than real microphone speech; a real-voice test is still needed.*
- **Formats:** gateway states `mp3, mp4, mpeg, mpga, m4a, wav, webm` up to **25 MB**. Tested WAV at 8 kHz/16 kHz/48 kHz-stereo, 8-bit and 16-bit: all accepted. Random bytes → 400 "could not be read as audio". 48 kHz stereo was slower (3.3 s vs ~1.8 s), so **record 16 kHz mono**. ogg/flac untested.
- **Silence:** 200 with `"text":""`; the client returns an empty transcript and callers must treat it as "didn't hear anything".
- **Language:** `language=en` accepted. `verbose_json` reports `"language":"auto"` (Nemotron) and `"english"` (Qwen). **Non-English was not tested** (no non-English voice available locally), so the "multilingual" claim is unverified.
- **True live streaming: not available.** `stream=true` is ignored (single JSON body, 3.2 s for a 9.4 s clip); no realtime endpoint. This fits push-to-talk (upload on release) but rules out partial transcripts while speaking.
- Billing is per audio second (≈ $0.0000035/s for both models).

## 5. Text-to-speech results

```
POST /v1/audio/speech  {"model":"x-ai/grok-voice-tts-1.0","input":"Opening Chrome now.","voice":"eve","response_format":"mp3"}
→ 200 audio/mpeg (26,496 bytes), transfer-encoding: chunked, x-oceantoken-cost: 0.000285
```
- **Voice is mandatory** (`'voice' is required … Use one of the voice names documented for this model`) and **cannot be discovered through the API**. Found by testing: Gemini accepted Kore, Puck, Zephyr, Charon, Aoede (rejected "alloy"; accepted "nova", unexplained); Grok accepted eve, ara, rex, sal, leo (rejected "alloy"). Output hashes differ per voice, so the voice is applied. The full voice lists are unknown.
- **Formats:** Gemini TTS supports `wav`, `pcm` only; Grok `mp3`, `pcm` only (others → 400 naming the allowed set). PCM is raw 16-bit LE, 24 kHz mono. **Gemini silently returns WAV when asked for `mp3`** (status 200), so the client trusts the bytes, not the request.
- **No real output streaming** despite chunked encoding: first byte at 5.9 s of 6.4 s (Gemini, 21 s of audio) and 2.3 s of 2.8 s (Grok, 16 s of audio). Synthesis runs ≈ 0.3× real time, so first audio of a long reply waits for the whole reply. **Synthesise sentence by sentence.**
- **Cancellation does not stop the charge.** Aborting the client at 1 s with 0 bytes received was still billed **$0.0124092**, versus $0.01241 for the identical completed request. Cancelling must mean "stop playback and don't issue further requests", not "abort to save money".

## 6. Latency (n=5 each, wall-clock through the gateway)

| Call | Model | min | median | max | cost/call |
|---|---|---|---|---|---|
| Tool call ("Scroll down a bit") | Gemini 3.8 Flash | 2,863 | **4,859** | 8,622 | $0.00120 |
| | Qwen 3.8 Flash | 1,102 | **1,687** | 2,248 | $0.000074 |
| STT 9.4 s clip | Nemotron | 2,489 | **3,152** | 3,366 | $0.000033 |
| | Qwen3-ASR | 1,966 | **2,202** | 2,883 | $0.000033 |
| TTS 19 chars | Gemini TTS | 1,970 | **2,389** | 3,265 | (no cost header) |
| | Grok TTS | 990 | **1,090** | 1,208 | $0.000285 |

A spoken command on the primary stack would be roughly STT 3.2 s + LLM 4.9 s + TTS 2.4 s ≈ **10 s** before action and reply. With the alternates (Qwen3-ASR + Qwen LLM + Grok TTS): ≈ **5 s**. Neither is yet "conversational"; the deterministic fast path for trivial commands (planned in ARCHITECTURE.md) matters for responsiveness.

## 7. Usage and cost

- **Measured total for this whole evaluation: ≈ $0.08** (key spend 1.9290 → 2.0095).
- **Key limits:** `x-oceantoken-key-budget: 10.0` USD. **This is not a "small" limit.** The key already showed **$1.93 of spend before my first call**, which I did not generate. The CTO should confirm the key is the restricted one intended; a $1–2 budget key is advisable.
- Per-call: Gemini chat 5-10× the cost of Qwen; image calls are the expensive ones (Gemini $0.0029-0.0056 each, mostly reasoning tokens).
- **Cost header is missing** on streamed chat and on Gemini TTS (`x-oceantoken-key-spend` is present on Gemini TTS but absent on streams). The client therefore (a) uses the cost header when present, (b) derives cost from the spend delta when only spend is present (labelled `DerivedFromSpendDelta`; unreliable if the key is used concurrently), and (c) attributes spend that appeared between calls but that no response claimed as `UnattributedSpend`, which counts toward the limit.
- A client-side **session spend limit** (default $0.50) blocks requests locally, as a backstop to the server budget (tested).

## 8. What the spike implemented

`src/Ridley.Providers/OceanToken/`: `OceanTokenClient` (key from the DPAPI `ISecretStore`, per-request timeouts, bounded retries with backoff and `Retry-After`, 4xx never retried, user cancellation propagated untouched, typed `ProviderException`), `OceanTokenModelProvider : IModelProvider`, `OceanTokenSpeechToText : ISpeechToTextProvider`, `OceanTokenTextToSpeech` (synthesis only, see §9), `InMemoryUsageTracker`. `spikes/OceanTokenSpike/` is a throwaway live harness (not in `Ridley.sln`). **76 automated tests pass** (33 new), covering retry/limit/timeout/cancel, key and content never in errors or logs, spend guard, tool-argument parsing, formats, empty transcripts.

Live harness results through the real classes: plain chat 3.4 s; tool call 3.1 s returning raw unvalidated args; image 11.3 s (`Submit, Cancel`); STT exact transcript 2.3 s; Grok TTS 0.8 s (MP3 detected); Gemini TTS 2.8 s (WAV detected); missing voice → `NotConfigured` in 3 ms with no network call; wrong key → `Denied`/401 after 124 ms, **not retried**; cancel at 300 ms → `OperationCanceledException` at 308 ms; lowered limit → `Denied` locally in 1 ms.

**Secrets:** the key was stored via `DpapiSecretStore`; the SQLite file contains no plaintext. Logs record only operation, model, status, attempts, latency, token counts and cost; never keys, prompts, transcripts, audio or images (tested). The key file was **moved out of the OneDrive-synced folder** into `C:\dev\Ridley\.env.local` (git-ignored); it sat in a cloud-synced folder before, so **rotating the key is prudent**. A repository scan found the key in no tracked or untracked file.

## 9. Required or recommended interface changes (none applied; all additive, none touch the gate)

1. **`ModelMessage` cannot express a tool round trip.** Needs assistant `ToolCalls` and a tool result with `ToolCallId`. **`ModelToolCall` needs an `Id`** (long, opaque, must be echoed verbatim).
2. **`ModelRequest`** needs `MaxOutputTokens` and an optional `ResponseSchema` (structured output works on both models).
3. **`ModelResponse`** should carry `FinishReason` and token `Usage`.
4. **`ModelDelta` cannot carry streamed tool-call fragments** (Qwen streams them). Add a tool-call delta, or document that streaming is text only.
5. **Split `ITextToSpeechProvider`** into synthesis (`text → audio bytes + format`) and playback (play / stop). A synthesis API cannot "speak" or honour cancel; the spike class deliberately does not implement the current interface. Playback needs an audio library (NAudio, already in the approved list).
6. **Move `IUsageTracker`/usage records to Core** so the shell can show spend, and add a `ProviderException` convention to Core.
7. `ISpeechToTextProvider`/`IMicrophoneCapture`: no change required, but record 16 kHz mono.

## 10. Unsupported / limitations

No live STT streaming · Gemini chat and both TTS models do not stream incrementally · one tool call per response for compound requests · `reasoning_effort` has no visible effect and reasoning tokens are unavoidable · voice lists undiscoverable · Gemini TTS silently ignores the `mp3` request · aborting does not avoid cost · cost header absent on streams and Gemini TTS · no model capability metadata · multilingual STT and non-English TTS untested · real-microphone accuracy untested · only 5 latency samples per call · gateway concurrency and rate limits not probed.

## 11. Observations for the CTO's decision (not decisions)

- **Latency is the main product risk.** Gemini 3.8 Flash's median 4.9 s for a trivial tool call is slow for voice control; Qwen 3.8 Flash is ~3× faster and ~16× cheaper but guessed on an ambiguous request and had weaker bounding boxes. A split (fast model for commands, Gemini for vision/teaching) is worth considering, with ambiguity handled by our own rule.
- Qwen3-ASR was ~30% faster than Nemotron on identical audio at identical price, with no accuracy difference on this sample; the "streaming" benefit of Nemotron is not exposed by the gateway. Confirm with real speech before choosing.
- Grok TTS was ~2× faster than Gemini TTS and reports cost per call.
- The spike confirms D-06 (OpenAI-compatible provider) and D-07 (a hosted STT instead of local Whisper) are viable, without a model download or disk cost.
