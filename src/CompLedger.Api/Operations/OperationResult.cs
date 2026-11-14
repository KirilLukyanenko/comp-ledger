namespace CompLedger.Api.Operations;

/// <summary>
/// What an operation answers, whichever front door asked: a status and a body, or a status and the detail of its
/// ProblemDetails. The REST endpoint turns it into an HTTP response, the MCP tool into a tool result (PRP-005 6.4).
/// </summary>
public sealed record OperationResult
{
    private OperationResult(int status, object? body, string? detail, string? location)
    {
        Status = status;
        Body = body;
        Detail = detail;
        Location = location;
    }

    public int Status { get; }

    /// <summary>The response body of a success; null for an error.</summary>
    public object? Body { get; }

    /// <summary>The ProblemDetails detail of an error.</summary>
    public string? Detail { get; }

    /// <summary>The path of the created resource, for a <c>201</c>.</summary>
    public string? Location { get; }

    public bool IsError => Status >= StatusCodes.Status400BadRequest;

    public static OperationResult Ok(object body) => new(StatusCodes.Status200OK, body, null, null);

    public static OperationResult Created(object body, string location) => new(StatusCodes.Status201Created, body, null, location);

    public static OperationResult Problem(int status, string? detail) => new(status, null, detail, null);

    public IResult ToHttpResult() => Status switch
    {
        >= StatusCodes.Status400BadRequest => Results.Problem(statusCode: Status, detail: Detail),
        StatusCodes.Status201Created => Results.Created(Location, Body),
        _ => Results.Json(Body),
    };
}
