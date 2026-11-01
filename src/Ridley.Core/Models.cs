using System.Text.Json;

namespace Ridley.Core;

// ---- Geometry / windows -------------------------------------------------
public readonly record struct Rect(int X, int Y, int Width, int Height);
public readonly record struct WindowHandle(long Value);
public sealed record WindowInfo(WindowHandle Handle, string Title, string ProcessName, int ProcessId, Rect Bounds);
public sealed record MonitorInfo(int Index, Rect Bounds, double DpiScale, bool IsPrimary);
public sealed record ScreenGeometry(Rect VirtualScreen, IReadOnlyList<MonitorInfo> Monitors);

// ---- Audio ----------------------------------------------------------------
public sealed record AudioClip(byte[] Pcm16, int SampleRate, int Channels)
{
    public override string ToString() => $"AudioClip({Pcm16.Length} bytes, {SampleRate} Hz)"; // never dump samples into logs
}
public sealed record Transcript(string Text, string? Language, double? Confidence);
public sealed record SttOptions(string? Language = null);
public sealed record TtsOptions(string? Voice = null, double Rate = 1.0, double Volume = 1.0);

// ---- Model ----------------------------------------------------------------
[Flags]
public enum ModelCapabilities { None = 0, ToolCalling = 1, Vision = 2, Streaming = 4 }
public enum MessageRole { System, User, Assistant, Tool }
public sealed record ModelMessage(MessageRole Role, string Content);
public sealed record ToolDefinition(string Name, string Description, JsonElement JsonSchema);
public sealed record ModelRequest(
    IReadOnlyList<ModelMessage> Messages,
    IReadOnlyList<ToolDefinition>? Tools = null,
    IReadOnlyList<ScreenCapture>? Images = null);
public sealed record ModelToolCall(string ToolName, JsonElement RawArguments); // RAW: must be validated before use
public sealed record ModelResponse(string? Text, IReadOnlyList<ModelToolCall> ToolCalls);
public sealed record ModelDelta(string? TextChunk);

// ---- Perception -----------------------------------------------------------
public enum CaptureTargetKind { ActiveWindow, Monitor, Region }
public sealed record CaptureTarget(CaptureTargetKind Kind, int MonitorIndex = 0, Rect? Region = null);

/// <summary>Pixels of the user's screen. Held in memory only; never persisted or logged.</summary>
public sealed class ScreenCapture
{
    public ScreenCapture(byte[] pngBytes, Rect screenBounds, DateTimeOffset capturedAt)
    {
        PngBytes = pngBytes;
        ScreenBounds = screenBounds;
        CapturedAt = capturedAt;
    }

    public byte[] PngBytes { get; }
    public Rect ScreenBounds { get; }
    public DateTimeOffset CapturedAt { get; }
    public override string ToString() => $"ScreenCapture({ScreenBounds.Width}x{ScreenBounds.Height}, content redacted)";
}

public sealed record OcrWord(string Text, Rect Bounds);
public sealed record OcrResult(string Text, IReadOnlyList<OcrWord> Words);
public enum UiPattern { Invoke, Toggle, ScrollIntoView, SetValue, Select }
public sealed record UiElementRef(string RuntimeId, WindowHandle Window);
public sealed record UiNode(UiElementRef Ref, string ControlType, string? Name, Rect Bounds, IReadOnlyList<UiNode> Children);
public sealed record UiTree(UiNode Root);
public sealed record UiQuery(int MaxDepth = 6, int MaxNodes = 500);
public sealed record UiSelector(string? Name = null, string? ControlType = null, string? AutomationId = null);

// ---- Teaching overlay -----------------------------------------------------
public enum AnnotationKind { Highlight, Circle, Rectangle, Arrow, Label, NumberedStep }
public sealed record Annotation(string Id, AnnotationKind Kind, Rect Bounds, string? Text = null, Rect? ArrowTo = null);

// ---- Documents ------------------------------------------------------------
public enum DocumentFormat { Markdown, Docx }
public sealed record DocumentSpec(string Title, string Body, DocumentFormat Format, string? SourceDescription = null);
public sealed record CreatedDocument(string Path, long SizeBytes);

// ---- History --------------------------------------------------------------
/// <summary>Action history entry. Deliberately contains no screen content or transcript text.</summary>
public sealed record HistoryEntry(DateTimeOffset At, string ToolName, bool Succeeded, string Summary);
