namespace DocBridge.Shared.Errors;

/// <summary>Maps to the system-wide error shape: { "error": { "code", "message" } }.</summary>
public sealed class ApiException(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;

    public static ApiException BadRequest(string message, string code = "VALIDATION_ERROR") => new(400, code, message);
    public static ApiException Unauthorized(string message = "Authentication required") => new(401, "UNAUTHORIZED", message);
    public static ApiException Forbidden(string message = "Not allowed") => new(403, "FORBIDDEN", message);
    public static ApiException NotFound(string message = "Not found") => new(404, "NOT_FOUND", message);
    public static ApiException Conflict(string message, string code = "CONFLICT") => new(409, code, message);
    public static ApiException BadGateway(string message = "Upstream service unavailable") => new(502, "UPSTREAM_UNAVAILABLE", message);
}

public sealed record ErrorBody(string Code, string Message);

public sealed record ErrorEnvelope(ErrorBody Error)
{
    public static ErrorEnvelope Of(string code, string message) => new(new ErrorBody(code, message));
}
