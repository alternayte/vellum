namespace Vellum.Kernel.Results;

public abstract record CommandResult
{
    public sealed record Success : CommandResult;
    public sealed record Invalid(IReadOnlyList<ValidationError> Errors) : CommandResult;
    public sealed record Conflict(string Message) : CommandResult;
    public sealed record NotFound(string Message) : CommandResult;

    private CommandResult() { }
}

public abstract record CommandResult<T>
{
    public sealed record Success(T Value) : CommandResult<T>;
    public sealed record Invalid(IReadOnlyList<ValidationError> Errors) : CommandResult<T>;
    public sealed record Conflict(string Message) : CommandResult<T>;
    public sealed record NotFound(string Message) : CommandResult<T>;

    private CommandResult() { }
}

public sealed record ValidationError(string Field, string Message);
