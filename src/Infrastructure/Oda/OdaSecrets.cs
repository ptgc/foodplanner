namespace Foodplanner.Infrastructure.Oda;

/// <summary>
/// A token or code that must never be printed. <see cref="ToString"/> is redacted, so a log argument, an exception message or a
/// string interpolation cannot leak it by accident. It is a class, not a record, so no generated member prints the value.
/// </summary>
public sealed class OdaSecret(string value)
{
    public string Reveal() => value;

    public override string ToString() => "[redacted]";
}

/// <summary>
/// What the token endpoint returned. Lives only in the memory of the one request that exchanged the code (T-053); never stored,
/// cached, logged or sent to the browser. Refresh is never used.
/// </summary>
public sealed class OdaTokens(OdaSecret access, OdaSecret? refresh)
{
    public OdaSecret Access { get; } = access;

    public OdaSecret? Refresh { get; } = refresh;

    public override string ToString() => "[redacted]";
}
