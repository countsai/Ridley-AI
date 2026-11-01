namespace Ridley.Core;

public enum RequestKind { Command, Question, Explain, Teach, Create }
public enum RequestSource { Text, Voice }

public sealed record UserRequest(string Text, RequestSource Source, DateTimeOffset At);
public sealed record ConversationTurn(MessageRole Role, string Text, DateTimeOffset At);
public sealed record ConversationContext(IReadOnlyList<ConversationTurn> Turns, ScreenCapture? LastCapture);

/// <summary>A plan, or a clarifying question when the request is ambiguous.</summary>
public sealed record TaskPlan(IReadOnlyList<PlannedStep> Steps, string? ClarificationQuestion = null);

public enum TaskEventKind { StepStarted, StepSucceeded, StepFailed, StepSkipped, AwaitingConfirmation, Cancelled, Completed }
public sealed record TaskEvent(TaskEventKind Kind, string? StepId, string Message);
public sealed record TaskReport(bool Completed, bool Cancelled, IReadOnlyList<TaskEvent> Events);

/// <summary>What the shell displays for a user turn.</summary>
public sealed record AssistantReply(string Text, bool IsError = false);

public enum AssistantState { Idle, Listening, Processing, Speaking, Error }
