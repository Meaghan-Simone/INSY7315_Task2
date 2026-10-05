namespace UncoveringGreatnessCRM.Services;

public enum ResultStatus { Ok, NotFound, Forbidden, Invalid, Conflict }

public class Result
{
    public ResultStatus Status { get; init; } = ResultStatus.Ok;
    public string? Error { get; init; }
    public Dictionary<string, string[]>? Errors { get; init; }
    public bool Succeeded => Status == ResultStatus.Ok;

    public static Result Success() => new();
    public static Result Fail(ResultStatus status, string message, string? field = null) => new()
    {
        Status = status,
        Error = message,
        Errors = field is null ? null : new Dictionary<string, string[]> { [field] = new[] { message } }
    };
    public static Result NotFound(string what = "Record") => Fail(ResultStatus.NotFound, $"{what} not found.");
    public static Result Forbidden(string message = "You do not have permission to do that.") => Fail(ResultStatus.Forbidden, message);
    public static Result Invalid(string field, string message) => Fail(ResultStatus.Invalid, message, field);
    public static Result Conflict(string message, string? field = null) => Fail(ResultStatus.Conflict, message, field);
}

public class Result<T> : Result
{
    public T? Value { get; init; }
    public static Result<T> Success(T value) => new() { Value = value };
    public static Result<T> From(Result r) => new() { Status = r.Status, Error = r.Error, Errors = r.Errors };
}

public record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount)
{
    public int TotalPages => PageSize <= 0 ? 1 : Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize));
    public bool HasPrevious => Page > 1;
    public bool HasNext => Page < TotalPages;
}
