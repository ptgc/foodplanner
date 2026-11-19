namespace Foodplanner.Domain.Users;

/// <summary>Why a PIN was refused (T-026).</summary>
public enum PinProblem
{
    None = 0,
    NotFourDigits,
    TooWeak,
}

/// <summary>
/// Format rules for the 4-digit PIN that returns a device from kid mode to the adult session (T-026, Q-020): exactly four
/// ASCII digits; all-equal digits ("0000") and straight ascending or descending runs ("1234", "9876") are refused.
/// </summary>
public static class PinRules
{
    public const int Length = 4;

    public static PinProblem Check(string? pin)
    {
        if (pin is null || pin.Length != Length || !pin.All(c => c is >= '0' and <= '9'))
        {
            return PinProblem.NotFourDigits;
        }

        var steps = Enumerable.Range(1, Length - 1).Select(i => pin[i] - pin[i - 1]).Distinct().ToList();
        return steps.Count == 1 && steps[0] is 0 or 1 or -1 ? PinProblem.TooWeak : PinProblem.None;
    }
}
