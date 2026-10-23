namespace Foodplanner.Infrastructure.Persistence.Tenancy;

/// <summary>
/// A write would lack or cross a household. This is a programming error, never a user error: it surfaces as
/// 500 (T-013 renders it as ProblemDetails). Thrown before any SQL is sent, so nothing is saved.
/// </summary>
public sealed class TenancyViolationException : Exception
{
    public TenancyViolationException()
    {
    }

    public TenancyViolationException(string message)
        : base(message)
    {
    }

    public TenancyViolationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
