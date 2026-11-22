using Foodplanner.Domain.Support;
using Microsoft.Extensions.Options;

namespace Foodplanner.Api.Support;

public static class SupportSetup
{
    /// <summary>Registers system admin support (T-096): the options, the per-request context, the audit writer and the session service.</summary>
    public static IServiceCollection AddSupport(this IServiceCollection services)
    {
        services.AddOptions<SupportOptions>().BindConfiguration(SupportOptions.Section).ValidateDataAnnotations().ValidateOnStart();
        services.AddSingleton<IValidateOptions<SupportOptions>, SupportOptionsValidator>();
        services.AddScoped<RequestSupportContext>();
        services.AddScoped<ISupportContext>(sp => sp.GetRequiredService<RequestSupportContext>());
        services.AddScoped<ISystemAdminDirectory, ConfigSystemAdminDirectory>();
        services.AddScoped<ISystemAdminStatus, SystemAdminStatus>();
        services.AddScoped<SupportAuditScope>();
        services.AddScoped<SupportAuditWriter>();
        services.AddScoped<SupportOwnerNotifier>();
        services.AddScoped<SupportSessionService>();
        services.AddScoped<SupportAccessLog>();
        return services;
    }

    /// <summary>Logs how many admins are configured (never the addresses).</summary>
    public static void LogConfiguredAdmins(this WebApplication app)
    {
        var count = SystemAdminList.Normalize(app.Services.GetRequiredService<IOptions<SupportOptions>>().Value.SystemAdminEmails).Length;
        app.Logger.LogInformation("System admin allow-list: {Count} configured.", count);
    }
}
