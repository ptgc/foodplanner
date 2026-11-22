using System.ComponentModel.DataAnnotations;
using Foodplanner.Domain.Support;
using Microsoft.Extensions.Options;

namespace Foodplanner.Api.Support;

/// <summary>
/// Section <c>Support</c> (T-096). <see cref="SystemAdminEmails"/> is the allow-list of system admins: configuration only
/// (user secrets locally, a Key Vault-backed app setting in Azure), at most <see cref="SystemAdminList.MaxEntries"/> entries,
/// read per request through <see cref="IOptionsMonitor{TOptions}"/> so a change takes effect on the next request. The repository
/// never holds an address. Nothing in the app writes it.
/// </summary>
public sealed class SupportOptions
{
    public const string Section = "Support";

    public string[] SystemAdminEmails { get; set; } = [];

    /// <summary>How long a read session lasts. Not extendable: a new session needs a new reason.</summary>
    [Range(1, 240)]
    public int ReadSessionMinutes { get; set; } = 30;

    /// <summary>How long a write session lasts.</summary>
    [Range(1, 240)]
    public int WriteSessionMinutes { get; set; } = 15;
}

/// <summary>More than the allowed number of admins fails startup (and a reload that adds one is refused: nobody is admin until fixed).</summary>
internal sealed class SupportOptionsValidator : IValidateOptions<SupportOptions>
{
    public ValidateOptionsResult Validate(string? name, SupportOptions options) =>
        SystemAdminList.IsTooLong(options.SystemAdminEmails)
            ? ValidateOptionsResult.Fail($"{SupportOptions.Section}:SystemAdminEmails may list at most {SystemAdminList.MaxEntries} addresses.")
            : ValidateOptionsResult.Success;
}
