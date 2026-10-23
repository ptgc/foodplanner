namespace Foodplanner.Infrastructure.Email;

/// <summary>Sends plain-text email. The production provider is T-017 (choice: Q-007).</summary>
public interface IEmailSender
{
    Task SendAsync(string to, string subject, string textBody, CancellationToken cancellationToken);
}
