namespace VPass.PrintService.Api;

/// <summary>The server rejected the device token (revoked / deleted / re-paired / expired). Stop and wait for re-pairing.</summary>
public sealed class RevokedException : Exception
{
    public RevokedException(string message) : base(message) { }
}

/// <summary>Non-success response from the API that is not a revocation (5xx, 4xx with a message).</summary>
public sealed class ApiException : Exception
{
    public int HttpStatus { get; }
    public ApiException(int httpStatus, string message) : base(message) { HttpStatus = httpStatus; }
}
