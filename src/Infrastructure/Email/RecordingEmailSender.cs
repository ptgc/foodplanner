using System.Collections.Concurrent;

namespace Foodplanner.Infrastructure.Email;

public sealed record EmailMessage(string To, string Subject, string TextBody);

/// <summary>
/// Keeps the most recent messages in memory for tests and Development; the oldest are dropped past
/// <see cref="Capacity"/> so a long-running container cannot grow without bound. It never logs a body, because bodies
/// contain confirmation links.
/// </summary>
public sealed class RecordingEmailSender : IEmailSender
{
    public const int Capacity = 500;

    private readonly ConcurrentQueue<EmailMessage> _messages = new();

    public IReadOnlyList<EmailMessage> Messages => [.. _messages];

    public Task SendAsync(string to, string subject, string textBody, CancellationToken cancellationToken)
    {
        _messages.Enqueue(new EmailMessage(to, subject, textBody));
        while (_messages.Count > Capacity)
        {
            _messages.TryDequeue(out _);
        }

        return Task.CompletedTask;
    }
}
