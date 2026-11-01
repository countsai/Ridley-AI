namespace Ridley.Core;

/// <summary>Why an operation did not succeed. Used so callers can report honestly instead of guessing.</summary>
public enum ErrorKind { None, Unsupported, Ambiguous, NotFound, Denied, Timeout, Cancelled, Failed, NotConfigured }

public readonly record struct Unit
{
    public static Unit Value => default;
}

/// <summary>Outcome of an operation that can fail in expected ways. Exceptions are reserved for programmer errors.</summary>
public sealed record Result<T>
{
    private Result(bool ok, T? value, string? error, ErrorKind kind)
    {
        Ok = ok;
        Value = value;
        Error = error;
        Kind = kind;
    }

    public bool Ok { get; }
    public T? Value { get; }
    public string? Error { get; }
    public ErrorKind Kind { get; }

    public static Result<T> Success(T value) => new(true, value, null, ErrorKind.None);

    public static Result<T> Fail(ErrorKind kind, string error)
    {
        if (kind == ErrorKind.None)
        {
            throw new ArgumentException("A failure needs an error kind.", nameof(kind));
        }

        return new(false, default, error, kind);
    }
}
