using Microsoft.AspNetCore.Mvc;

namespace Foodplanner.Api.Features.Auth;

/// <summary>
/// The shape of <c>403 pin-incorrect</c> (T-026): RFC 9457 ProblemDetails plus the extension member
/// <c>attemptsRemaining</c> (tries left before the lock). Only used to describe the response in OpenAPI.
/// </summary>
public sealed class PinIncorrectProblem : ProblemDetails
{
    [System.Text.Json.Serialization.JsonNumberHandling(System.Text.Json.Serialization.JsonNumberHandling.Strict)]
    public int AttemptsRemaining { get; set; }
}
