using System.Collections.Concurrent;
using mediq.Application.Abstractions;

namespace mediq.IntegrationTests.TestDoubles;

/// <summary>
/// Test double for the password-reset notifier seam. RECORDS every send so a test can (a) assert the (offline)
/// dispatch was invoked and (b) recover the ONE-TIME plaintext token — the self-service flow never returns it
/// in the response, so the recorded notification is how a test drives the subsequent reset-password call. Can
/// be armed to THROW so a test can prove the dispatch is advisory (a notifier failure must not fail the
/// request). Registered as a singleton in the fixture and reset per test.
/// </summary>
public sealed class RecordingPasswordResetNotifier : IPasswordResetNotifier
{
    private readonly ConcurrentQueue<PasswordResetNotification> _sends = new();

    /// <summary>When true, the NEXT (and every) NotifyAsync throws — simulating a broken transport.</summary>
    public bool ThrowOnSend { get; set; }

    public IReadOnlyCollection<PasswordResetNotification> Sends => _sends.ToArray();

    public int CountFor(string email) => _sends.Count(n =>
        string.Equals(n.Email, email, StringComparison.OrdinalIgnoreCase));

    /// <summary>The most recent recorded token for an email (the one-time reset credential).</summary>
    public string? LatestTokenFor(string email) => _sends
        .Where(n => string.Equals(n.Email, email, StringComparison.OrdinalIgnoreCase))
        .Select(n => n.Token)
        .LastOrDefault();

    public void Reset()
    {
        _sends.Clear();
        ThrowOnSend = false;
    }

    public Task NotifyAsync(PasswordResetNotification notification, CancellationToken ct)
    {
        if (ThrowOnSend)
            throw new InvalidOperationException("simulated password-reset notifier failure");
        _sends.Enqueue(notification);
        return Task.CompletedTask;
    }
}
