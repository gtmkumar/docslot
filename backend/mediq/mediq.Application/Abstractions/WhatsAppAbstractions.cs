namespace mediq.Application.Abstractions;

/// <summary>
/// Verifies the inbound WhatsApp webhook payload signature. Meta signs the EXACT raw request body with the
/// App Secret (HMAC-SHA256) and sends it in <c>X-Hub-Signature-256: sha256=&lt;hex&gt;</c>. The verifier
/// recomputes the HMAC over the raw bytes and compares in constant time; a mismatch means the request did
/// not originate from Meta (reject 401).
/// </summary>
public interface IWhatsAppSignatureVerifier
{
    /// <summary>True iff <paramref name="signatureHeader"/> equals <c>sha256=</c> + HMAC-SHA256(rawBody) keyed by the App Secret.</summary>
    bool Verify(byte[] rawBody, string? signatureHeader);
}

/// <summary>
/// The idempotency gate for inbound messages. WhatsApp may redeliver the same message (at-least-once), so
/// the provider message id is recorded on first sight; a redelivery is detected and skipped — preventing a
/// double-advance of the conversation or a duplicate booking.
/// </summary>
public interface IProcessedMessageStore
{
    /// <summary>
    /// Atomically records the message id (<c>INSERT ... ON CONFLICT DO NOTHING</c>). Returns true when this
    /// is the FIRST time the id is seen (process it); false when it was already processed (skip).
    /// </summary>
    Task<bool> TryMarkProcessedAsync(string whatsappMessageId, DateTime nowUtc, CancellationToken ct);
}

/// <summary>Append-only message journal (<c>docslot.wa_message_log</c>) — both inbound and outbound legs.</summary>
public interface IWaMessageLogWriter
{
    Task LogAsync(WaMessageLogEntry entry, CancellationToken ct);
}

public sealed record WaMessageLogEntry(
    Guid TenantId,
    Guid? PatientId,
    Guid? ConversationId,
    string? WhatsAppMessageId,
    string Direction,        // 'inbound' | 'outbound'
    string MessageType,      // 'text' | 'interactive' | ...
    string? ContentText,     // wrapped as {"text": ...} jsonb by the writer
    string? Status,
    DateTime SentAtUtc);

/// <summary>
/// Stub outbound transport: instead of calling Meta we enqueue the message into
/// <c>docslot.outbox_messages</c> (status 'pending'). A separate dispatcher (out of scope) would drain it.
/// This is the outbox pattern — the inbound handler stays free of any Meta credential.
/// </summary>
public interface IOutboxMessageEnqueuer
{
    Task EnqueueAsync(OutboxMessage message, CancellationToken ct);
}

public sealed record OutboxMessage(
    Guid TenantId,
    Guid? PatientId,
    string MessageIntent,    // 'booking_prompt' | 'booking_confirmation' | ...
    string ToPhone,
    string Text,
    string? CorrelationId,
    DateTime NowUtc,
    IReadOnlyDictionary<string, object?>? Extra = null);

/// <summary>
/// Outbound transport to WhatsApp (the actual send). Implemented by the dev <c>StubWhatsAppSender</c>
/// (logs + returns a synthetic <c>wamid.stub.&lt;guid&gt;</c>, never touches Meta) or the real
/// <c>MetaWhatsAppSender</c> (HTTP POST to graph.facebook.com). Selection is by config: when
/// <c>WhatsApp:AccessToken</c> + <c>WhatsApp:GraphBaseUrl</c> are set, the real sender is wired; else the stub.
/// The drain worker calls this once per claimed outbox row and persists the outcome.
/// </summary>
public interface IWhatsAppSender
{
    /// <summary>Sends one message; returns the provider message id on success or a failure with a reason.</summary>
    Task<WhatsAppSendResult> SendAsync(OutboundMessage message, CancellationToken ct);
}

/// <summary>A single message to deliver, projected from a <c>docslot.outbox_messages</c> row.</summary>
public sealed record OutboundMessage(
    Guid OutboxId,
    Guid TenantId,
    Guid? PatientId,
    string MessageIntent,
    string ToPhone,
    string Text,
    string? CorrelationId,
    int AttemptCount,
    int MaxAttempts);

/// <summary>
/// The outcome of a single send. <see cref="Success"/> ⇒ <see cref="ProviderMessageId"/> is the wamid to
/// persist; otherwise <see cref="Error"/> explains the failure (stored in <c>last_error</c>, drives retry).
/// </summary>
public sealed record WhatsAppSendResult(bool Success, string? ProviderMessageId, string? Error)
{
    public static WhatsAppSendResult Sent(string providerMessageId) => new(true, providerMessageId, null);
    public static WhatsAppSendResult Failed(string error) => new(false, null, error);
}

/// <summary>
/// Drain-side store over <c>docslot.outbox_messages</c>: atomically claim a batch of due 'pending' rows
/// (transition to 'processing' so no other worker/instance double-sends), then mark each terminal outcome
/// ('sent') or schedule a retry / 'abandoned'. The claim uses <c>FOR UPDATE SKIP LOCKED</c> + a status
/// transition so it is safe across scale-out.
/// </summary>
public interface IOutboxDrainStore
{
    /// <summary>
    /// Claims up to <paramref name="batchSize"/> due messages (<c>status='pending' AND (next_retry_at IS NULL
    /// OR next_retry_at &lt;= now())</c>), flipping them to 'processing' in the same statement, and returns them.
    /// </summary>
    Task<IReadOnlyList<OutboundMessage>> ClaimDueAsync(int batchSize, DateTime nowUtc, CancellationToken ct);

    /// <summary>Marks a claimed message delivered: status='sent', sent_at=now, whatsapp_message_id=provider id.</summary>
    Task MarkSentAsync(Guid outboxId, string providerMessageId, DateTime nowUtc, CancellationToken ct);

    /// <summary>
    /// Records a failed send: attempt_count++, last_error set; if the incremented count reaches max_attempts
    /// the row is 'abandoned', otherwise it returns to 'pending' with <paramref name="nextRetryAtUtc"/> set
    /// to the computed exponential backoff.
    /// </summary>
    Task MarkFailedAsync(Guid outboxId, string error, DateTime nextRetryAtUtc, DateTime nowUtc, CancellationToken ct);

    /// <summary>
    /// Requeues rows stranded in 'processing' (the worker died/was cancelled mid-send) older than
    /// <paramref name="olderThan"/> back to 'pending' so the next drain re-attempts them. Backed by the
    /// SECURITY DEFINER fn <c>docslot.requeue_stranded_outbox</c> (the worker has no per-tenant RLS scope).
    /// Returns rows requeued.
    /// </summary>
    Task<int> RequeueStrandedAsync(TimeSpan olderThan, CancellationToken ct);
}

/// <summary>
/// Upsertable conversational-contact profile (<c>docslot.wa_contact_profiles</c>, unique on tenant+phone).
/// Remembers who a number usually books for so a returning patient can be greeted/short-circuited.
/// </summary>
public interface IWaContactProfileRepository
{
    Task<WaContactProfile?> GetAsync(Guid tenantId, string phone, CancellationToken ct);

    /// <summary>Insert-or-update by (tenant, phone). Only the supplied fields are written; nulls leave columns untouched.</summary>
    Task UpsertAsync(WaContactProfileUpsert profile, CancellationToken ct);
}

public sealed record WaContactProfile(
    Guid ProfileId,
    Guid TenantId,
    string Phone,
    string? DisplayName,
    string DefaultBookingFor,
    string? LastRelation,
    Guid? LinkedPatientId,
    string PreferredLanguage);

public sealed record WaContactProfileUpsert(
    Guid TenantId,
    string Phone,
    string? DisplayName,
    string? LastRelation,
    string? PreferredLanguage,
    DateTime NowUtc);

/// <summary>
/// State-machine persistence for an active booking conversation (<c>docslot.conversations</c>). One active
/// row per (tenant, phone); the working selections (relation, chosen department/doctor/slot, and the
/// option lists shown to the user) live in the <c>context</c> jsonb.
/// </summary>
public interface IConversationRepository
{
    /// <summary>The active, non-expired conversation for this number, or null.</summary>
    Task<ConversationState?> GetActiveAsync(Guid tenantId, string phone, CancellationToken ct);

    Task<Guid> CreateAsync(Guid tenantId, string phone, string currentStep, string contextJson, string? detectedLanguage, DateTime nowUtc, CancellationToken ct);

    Task UpdateAsync(Guid conversationId, string currentStep, string contextJson, bool isActive, DateTime nowUtc, CancellationToken ct);
}

public sealed record ConversationState(
    Guid ConversationId,
    Guid TenantId,
    Guid? PatientId,
    string Phone,
    string CurrentStep,
    string ContextJson,
    string? DetectedLanguage,
    bool IsActive);

/// <summary>
/// Read-side menus for the conversational flow: departments, a department's active doctors (with fee), and
/// the earliest available slots for a doctor. All tenant-scoped; none are PHI (clinic catalog + capacity).
/// </summary>
public interface IWhatsAppCatalogReadService
{
    /// <summary>The tenant's customer-facing <c>display_name</c> for greetings/templates (never a hardcoded brand).</summary>
    Task<string?> GetTenantDisplayNameAsync(Guid tenantId, CancellationToken ct);
    Task<IReadOnlyList<WaDepartment>> ListDepartmentsAsync(Guid tenantId, CancellationToken ct);
    Task<IReadOnlyList<WaDoctor>> ListDoctorsAsync(Guid tenantId, Guid departmentId, CancellationToken ct);
    Task<IReadOnlyList<WaSlot>> ListEarliestSlotsAsync(Guid tenantId, Guid doctorId, int take, CancellationToken ct);
}

public sealed record WaDepartment(Guid DepartmentId, string Name);
public sealed record WaDoctor(Guid DoctorId, string FullName, decimal? ConsultationFee);
public sealed record WaSlot(Guid SlotId, DateOnly SlotDate, TimeOnly StartTime);

/// <summary>
/// Request-scoped override for the Idempotency-Key used by the command pipeline when the request did NOT
/// arrive over HTTP with an <c>Idempotency-Key</c> header. The inbound-WhatsApp handler derives a
/// deterministic key (from the conversation + slot) and sets it here BEFORE dispatching the inner
/// <c>CreateBookingCommand</c> (which is <c>IRequireIdempotency</c>), so a retried confirm can't double-book.
/// The HTTP header always takes precedence; this is only the fallback for server-originated dispatches.
/// </summary>
public interface IAmbientIdempotencyKey
{
    string? Key { get; }
    void Set(string key);
}

/// <summary>
/// Request-scoped override that lets an ANONYMOUS request (the WhatsApp webhook) establish the tenant the
/// command pipeline scopes to (RLS <c>app.tenant_id</c> + the tenant passed to booking creation), once it
/// has been resolved server-side from the <c>phone_number_id</c> map. Authenticated requests never set it,
/// so the JWT-only tenant path is unchanged; <see cref="ICurrentUserContext.TenantId"/> falls back to this
/// only when there is no JWT tenant claim.
/// </summary>
public interface ITenantScopeOverride
{
    Guid? TenantId { get; }
    void Set(Guid tenantId);
}

// ============================================================================
// Behalf-booking patient consent (DPDP fake-patient guard)
// ============================================================================

/// <summary>
/// Orchestrates the behalf-booking patient OTP consent flow (DPDP). When a number books FOR SOMEONE ELSE we
/// create the booking 'pending' with <c>patient_consent_status='pending'</c>, generate a one-time code, store
/// only a salted hash, and send the code to the PATIENT's WhatsApp number naming the booker + relation. The
/// patient's reply (matched by phone to a pending code) confirms or declines. Runs inside the inbound
/// command's tenant-scoped UoW, so the OTP row, outbox message, and booking mutation commit together.
/// </summary>
public interface IPatientConsentService
{
    /// <summary>
    /// Generates + persists a consent OTP for a freshly-created behalf booking and enqueues the OTP message
    /// to the patient (any prior pending code for that number is superseded/expired first).
    /// </summary>
    Task SendForBehalfBookingAsync(ConsentSendRequest request, CancellationToken ct);

    /// <summary>
    /// If <paramref name="fromPhone"/> has a pending consent OTP in this tenant, interpret <paramref name="body"/>
    /// as the code (or a decline) and resolve it — confirming the booking's consent, or denying it (which also
    /// cancels the booking and frees its slot), or returning a wrong-code / expired prompt. Returns null when
    /// there is NO pending consent for this number (the caller then runs the normal booking state machine).
    /// </summary>
    Task<ConsentVerifyResult?> TryVerifyReplyAsync(
        Guid tenantId, string fromPhone, string body, string lang, DateTime nowUtc, CancellationToken ct);
}

public sealed record ConsentSendRequest(
    Guid TenantId, Guid BookingId, Guid? PatientId, string PatientPhone, string BookerPhone,
    string Relation, string TenantName, string? BookerName, string? DoctorName, string? SlotLabel,
    string Lang, DateTime NowUtc);

public enum ConsentOutcome { Confirmed, Denied, WrongCode, Expired }

public sealed record ConsentVerifyResult(ConsentOutcome Outcome, string OutboundText, Guid BookingId, Guid? PatientId);

/// <summary>Persistence over <c>docslot.booking_consent_otps</c> (tenant-isolated by RLS).</summary>
public interface IConsentOtpStore
{
    /// <summary>Expire any existing 'pending' code for (tenant, patientPhone) so a new behalf booking supersedes it.</summary>
    Task ExpireExistingPendingAsync(Guid tenantId, string patientPhone, DateTime nowUtc, CancellationToken ct);

    Task CreateAsync(ConsentOtpInsert row, CancellationToken ct);

    /// <summary>The single live (pending, non-expired) consent for this number in this tenant, or null.</summary>
    Task<PendingConsentOtp?> GetPendingByPhoneAsync(Guid tenantId, string patientPhone, DateTime nowUtc, CancellationToken ct);

    Task SetStatusAsync(Guid consentOtpId, string status, DateTime? verifiedAtUtc, CancellationToken ct);

    Task IncrementAttemptsAsync(Guid consentOtpId, CancellationToken ct);

    /// <summary>
    /// Sweeps lapsed pending consent OTPs: marks them 'expired', cancels the awaiting booking, and frees its
    /// slot capacity. Backed by the SECURITY DEFINER fn <c>docslot.expire_stale_consent_otps</c> (cross-tenant;
    /// the worker has no per-tenant RLS scope). Returns the number of slots freed.
    /// </summary>
    Task<int> ExpireStaleAsync(CancellationToken ct);
}

public sealed record ConsentOtpInsert(
    Guid TenantId, Guid BookingId, string PatientPhone, string BookerPhone, string Relation,
    string CodeSalt, string CodeHash, DateTime ExpiresAt, DateTime NowUtc);

public sealed record PendingConsentOtp(
    Guid ConsentOtpId, Guid BookingId, string PatientPhone, string BookerPhone, string Relation,
    string CodeSalt, string CodeHash, short Attempts, short MaxAttempts, DateTime ExpiresAt);

/// <summary>
/// The hidden-Care-Partner conversion sweep (nightly). Recomputes the behalf-booking funnel (distinct patients
/// per booker in 90d + broker linkage) and sends eligible "hidden partner" numbers a bilingual "become a Care
/// Partner" nudge via the outbox — at most one per cooldown (carrot, not spam). Backed by the SECURITY DEFINER
/// fn <c>docslot.run_partner_nudge_sweep</c> (cross-tenant; the worker has no per-tenant RLS scope). Returns
/// the number of nudges sent.
/// </summary>
public interface IPartnerNudgeStore
{
    Task<int> RunSweepAsync(int minDistinctPatients, TimeSpan cooldown, CancellationToken ct);
}
