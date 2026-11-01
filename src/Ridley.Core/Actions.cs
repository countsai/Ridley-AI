using System.Text.Json;

namespace Ridley.Core;

public enum RiskLevel { Safe, Confirm, Blocked }
public enum AuthorisedBy { Policy, UserConfirmation }

/// <summary>Static description of an action: its name, parameter schema and baseline risk.</summary>
public sealed record ToolDescriptor(string Name, string Description, JsonElement JsonSchema, RiskLevel BaselineRisk);

/// <summary>
/// Tool arguments that have passed schema validation. The constructor is internal on purpose: raw model output
/// (<see cref="ModelToolCall.RawArguments"/>) cannot become executable arguments without going through the validator.
/// </summary>
public sealed class ValidatedArgs
{
    internal ValidatedArgs(string toolName, JsonElement arguments)
    {
        ToolName = toolName;
        Arguments = arguments.Clone();
    }

    public string ToolName { get; }
    public JsonElement Arguments { get; }
}

public sealed record ExpectedOutcome(string Description, TimeSpan Timeout);

/// <summary>One step of a plan. Only holds validated arguments, so an unvalidated step cannot be constructed from model output.</summary>
public sealed record PlannedStep(string StepId, ToolDescriptor Tool, ValidatedArgs Args, ExpectedOutcome Expected, string UserFacingSummary);

/// <summary>
/// Proof that the deterministic permission gate approved a step. The only way to obtain one is through
/// <see cref="IPermissionGate"/>; <see cref="IActionExecutor"/> accepts nothing else, so the gate cannot be bypassed.
/// </summary>
public sealed class AuthorisedStep
{
    internal AuthorisedStep(PlannedStep step, AuthorisedBy by)
    {
        Step = step;
        By = by;
        AuthorisedAt = DateTimeOffset.UtcNow;
    }

    public PlannedStep Step { get; }
    public AuthorisedBy By { get; }
    public DateTimeOffset AuthorisedAt { get; }
}

public sealed record ConfirmationRequest(string Title, string Detail, RiskLevel Risk);

/// <summary>Asks the human. The model can never answer this on the user's behalf.</summary>
public interface IConfirmationPrompt
{
    Task<bool> ConfirmAsync(ConfirmationRequest request, CancellationToken ct);
}

public sealed record ActionContext(WindowInfo? TargetWindow);
public sealed record ActionOutcome(bool Succeeded, ErrorKind Kind, string Message)
{
    public static ActionOutcome Success(string message) => new(true, ErrorKind.None, message);
    public static ActionOutcome Failure(ErrorKind kind, string message) => new(false, kind, message);
}
public sealed record Verification(bool Verified, string Evidence);
