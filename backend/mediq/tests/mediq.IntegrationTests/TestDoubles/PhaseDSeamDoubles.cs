using System.Collections.Concurrent;
using mediq.Application.Abstractions;

namespace mediq.IntegrationTests.TestDoubles;

/// <summary>
/// Test double for the #93 invitation notifier seam. RECORDS every send so a test can assert the (offline)
/// dispatch was invoked, and can be armed to THROW so a test can prove the dispatch is advisory (a notifier
/// failure must not fail the invite). Registered as a singleton in the invitation fixture and reset per test.
/// </summary>
public sealed class RecordingInvitationNotifier : IInvitationNotifier
{
    private readonly ConcurrentQueue<InvitationNotification> _sends = new();

    /// <summary>When true, the NEXT (and every) NotifyAsync throws — simulating a broken email/WhatsApp transport.</summary>
    public bool ThrowOnSend { get; set; }

    public IReadOnlyCollection<InvitationNotification> Sends => _sends.ToArray();

    public int CountFor(string email) => _sends.Count(n =>
        string.Equals(n.InvitedEmail, email, StringComparison.OrdinalIgnoreCase));

    public void Reset()
    {
        _sends.Clear();
        ThrowOnSend = false;
    }

    public Task NotifyAsync(InvitationNotification notification, CancellationToken ct)
    {
        if (ThrowOnSend)
            throw new InvalidOperationException("simulated invitation notifier failure");
        _sends.Enqueue(notification);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Test double for the #94 geo-IP seam. Returns a fixed <see cref="City"/> (default null = the offline
/// NullGeoIpResolver behaviour) so a test can flip it to a known city and assert it surfaces on audit rows +
/// active sessions, or leave it null to assert the unknown/offline case. Singleton in the security fixture;
/// xUnit runs a class's tests sequentially, so per-test mutation is race-free.
/// </summary>
public sealed class ConfigurableGeoIpResolver : IGeoIpResolver
{
    public string? City { get; set; }

    public Task<string?> ResolveCityAsync(string? ipAddress, CancellationToken ct) =>
        Task.FromResult(string.IsNullOrEmpty(ipAddress) ? null : City);
}
