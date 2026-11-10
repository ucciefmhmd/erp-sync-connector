namespace ErpSync.Application.Common;

public enum ErrorKind
{
    Validation,
    NotFound,
    Conflict,
    Unprocessable,
}

/// <summary>An expected business failure. Unexpected failures stay exceptions.</summary>
public sealed record AppError(string Code, string Message, ErrorKind Kind, IReadOnlyDictionary<string, string[]>? Details = null)
{
    public static AppError Validation(IReadOnlyDictionary<string, string[]> details) =>
        new("validation_failed", "One or more fields are invalid.", ErrorKind.Validation, details);
}

/// <summary>Outcome of an operation that either produces a value or an <see cref="AppError"/>.</summary>
public sealed class Result<T>
{
    internal Result(T? value, AppError? error)
    {
        Value = value;
        Error = error;
    }

    public T? Value { get; }

    public AppError? Error { get; }

    public bool IsSuccess => Error is null;

#pragma warning disable CA2225 // Named alternates exist on the static Result class.
    public static implicit operator Result<T>(T value) => new(value, null);

    public static implicit operator Result<T>(AppError error) => new(default, error);
#pragma warning restore CA2225
}

public static class Result
{
    public static Result<T> Success<T>(T value) => new(value, null);

    public static Result<T> Failure<T>(AppError error) => new(default, error);
}
