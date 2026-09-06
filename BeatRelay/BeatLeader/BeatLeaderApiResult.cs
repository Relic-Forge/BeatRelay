using System.Net;

namespace BeatRelay.BeatLeader;

public sealed class BeatLeaderApiResult<T>
{
    private BeatLeaderApiResult(bool isSuccess, T? value, HttpStatusCode? statusCode, string? errorMessage)
    {
        IsSuccess = isSuccess;
        Value = value;
        StatusCode = statusCode;
        ErrorMessage = errorMessage;
    }

    public bool IsSuccess { get; }

    public T? Value { get; }

    public HttpStatusCode? StatusCode { get; }

    public string? ErrorMessage { get; }

    public static BeatLeaderApiResult<T> Success(T value, HttpStatusCode statusCode)
        => new(true, value, statusCode, null);

    public static BeatLeaderApiResult<T> Failure(HttpStatusCode? statusCode, string errorMessage)
        => new(false, default, statusCode, errorMessage);
}
