using mediq.Application.Abstractions;

namespace mediq.Infrastructure.Commission;

/// <summary>
/// Dev/dry-run payout rail. Performs NO real money movement — it returns a clearly-labelled <c>DRYRUN-…</c>
/// reference and the gateway name <c>stub_dryrun</c>, so the execute handler records an honest, distinguishable
/// "paid (dry run)" rather than a fabricated UTR that looks like a real bank transfer. A real adapter
/// (RazorpayX/Cashfree) implements <see cref="IPayoutGateway"/> and is selected by config when credentials are
/// present (same pattern as the WhatsApp sender). This is the "payout dry-run" of PRD gate 2.
/// </summary>
public sealed class StubPayoutGateway : IPayoutGateway
{
    public string Name => "stub_dryrun";

    public Task<PayoutGatewayResult> SendAsync(PayoutInstruction instruction, CancellationToken ct) =>
        // Reference is DERIVED from the idempotency key (payout id), not random, so a crash-resume re-call returns
        // the SAME DRYRUN-… reference — modelling a real gateway's idempotent dedupe (and proving the resume path
        // never invents a second "transfer"). No real money moves.
        Task.FromResult(PayoutGatewayResult.Ok($"DRYRUN-{instruction.IdempotencyKey.Replace("-", "")}"[..18], Name, isDryRun: true));
}
