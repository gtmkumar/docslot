namespace mediq.SharedDataModel.Docslot.Dashboard.Dtos;

/// <summary>
/// Marker for booking/money-mutating requests that MUST be idempotent.
/// <para>
/// Per CLAUDE.md, POSTs that mutate bookings or money carry an <c>Idempotency-Key</c>
/// HTTP header. The key is transport-level (a header), but this contract also surfaces it
/// on the request body so that: (a) the frontend mock seam can mirror it, (b) the eventual
/// command/handler + validation pipeline can assert its presence WITHOUT reaching into
/// HttpContext, and (c) the de-dup store can key on it.
/// </para>
/// <para>
/// Convention: the API's idempotency middleware reads the <c>Idempotency-Key</c> header and
/// binds it into <see cref="IdempotencyKey"/>. A request with this marker and a null/empty
/// key is rejected (422) by the validation pipeline before any handler runs. Replaying the
/// same key returns the original result instead of re-executing the mutation.
/// </para>
/// </summary>
public interface IIdempotentRequest
{
    /// <summary>
    /// Client-supplied idempotency token (mirrors the <c>Idempotency-Key</c> header).
    /// A stable GUID/string the client reuses on retries of the same logical action.
    /// </summary>
    string? IdempotencyKey { get; }
}
