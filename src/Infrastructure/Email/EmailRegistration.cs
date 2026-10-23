using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Foodplanner.Infrastructure.Email;

public sealed class EmailOptions
{
    public const string Section = "Email";
    public const string ProviderSetting = "Email:Provider";
    public const string RecordingProvider = "Recording";

    public string? Provider { get; set; }
}

public static class EmailRegistration
{
    /// <summary>
    /// Registers the email sender chosen by <c>Email:Provider</c>. Only <c>Recording</c> exists until T-017.
    /// Development defaults to it. In any other environment the provider must be set explicitly; the options
    /// validator fails host startup (ValidateOnStart) with a message naming the setting, so a deployed API never
    /// silently drops mail. The setting is read through options, not eagerly, so it sees the final configuration.
    /// </summary>
    public static IServiceCollection AddEmailSender(this IServiceCollection services, bool isDevelopment)
    {
        services.AddOptions<EmailOptions>().BindConfiguration(EmailOptions.Section).ValidateOnStart();
        services.AddSingleton<IValidateOptions<EmailOptions>>(new EmailOptionsValidator(isDevelopment));
        services.AddSingleton<RecordingEmailSender>();
        services.AddSingleton<IEmailSender>(sp => sp.GetRequiredService<RecordingEmailSender>());
        return services;
    }

    private sealed class EmailOptionsValidator(bool isDevelopment) : IValidateOptions<EmailOptions>
    {
        public ValidateOptionsResult Validate(string? name, EmailOptions options)
        {
            if (string.IsNullOrWhiteSpace(options.Provider))
            {
                return isDevelopment
                    ? ValidateOptionsResult.Success
                    : ValidateOptionsResult.Fail(
                        $"{EmailOptions.ProviderSetting} is not set. Set {EmailOptions.ProviderSetting}={EmailOptions.RecordingProvider} "
                        + "(env var Email__Provider) until the production provider (T-017) exists.");
            }

            return string.Equals(options.Provider, EmailOptions.RecordingProvider, StringComparison.OrdinalIgnoreCase)
                ? ValidateOptionsResult.Success
                : ValidateOptionsResult.Fail(
                    $"{EmailOptions.ProviderSetting} has unsupported value '{options.Provider}'. Supported: {EmailOptions.RecordingProvider}.");
        }
    }
}
