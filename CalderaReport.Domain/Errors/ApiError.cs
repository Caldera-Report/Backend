namespace CalderaReport.Domain.Errors;

public record ApiError(string Message, int StatusCode);
