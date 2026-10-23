using System.Collections.Concurrent;

namespace Foodplanner.Infrastructure.Email;

public sealed record EmailMessage(string To, string Subject, string TextBody);

/// <summary>
/// Keeps messages in memory for tests and Development. It never logs a body, because bodies contain
/// confirmation links.
/// </summary>
public sealed class RecordingEmailSender : IEmailSender
{
    private readonly ConcurrentQueue<EmailMessage> _messages = new();

    public IReadOnlyList<EmailMessage> Messages => [.. _messages];

    public Task SendAsync(string to, string subject, string textBody, CancellationToken cancellationToken)
    {
        _messages.Enqueue(new EmailMessage(to, subject, textBody));
        return Task.CompletedTask;
    }
}
