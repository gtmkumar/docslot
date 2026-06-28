using mediq.Application.Abstractions;
using mediq.Application.Cqrs;
using mediq.Application.Features.Docslot.Bookings;
using mediq.Domain.Docslot;
using Microsoft.Extensions.Logging;

namespace mediq.Application.Features.Docslot.WhatsApp;

/// <summary>
/// The inbound booking state machine. Runs INSIDE the command pipeline's tenant-scoped UoW transaction
/// (tenant resolved at the edge → <see cref="ITenantScopeOverride"/> → RLS <c>app.tenant_id</c>), so every
/// wa_* / conversation / consent write is tenant-scoped. Booking creation is delegated to the audited
/// <see cref="CreateBookingCommand"/> via the inner dispatcher (reusing holds, OPD token, events, audit).
/// <para>
/// Behalf bookings (someone booking FOR ANOTHER number) take the DPDP path: the booking is created
/// 'pending' with <c>patient_consent_status='pending'</c> and a one-time code is sent to the PATIENT, whose
/// reply (intercepted up-front by <see cref="IPatientConsentService"/>) confirms or declines. Every template
/// renders in the contact's <c>preferred_language</c> and greets with the tenant's <c>display_name</c>.
/// </para>
/// </summary>
public sealed class ProcessInboundWhatsAppMessageCommandHandler(
    IProcessedMessageStore processed,
    IWaMessageLogWriter messageLog,
    IWaContactProfileRepository profiles,
    IConversationRepository conversations,
    IWhatsAppCatalogReadService catalog,
    IOutboxMessageEnqueuer outbox,
    IPatientConsentService consent,
    ICommandDispatcher commands,
    IAmbientIdempotencyKey ambientIdempotencyKey,
    ICurrentUserContext ctx,
    IClock clock,
    ILogger<ProcessInboundWhatsAppMessageCommandHandler> logger)
    : ICommandHandler<ProcessInboundWhatsAppMessageCommand, ProcessInboundResult>
{
    private const int MaxOptions = 9;   // keep numbered lists short/tappable

    public async Task<ProcessInboundResult> Handle(ProcessInboundWhatsAppMessageCommand command, CancellationToken ct)
    {
        var now = clock.UtcNow;

        // 1) Idempotency — record the provider message id on first sight; skip a redelivery.
        if (!await processed.TryMarkProcessedAsync(command.WhatsAppMessageId, now, ct))
        {
            logger.LogInformation("WhatsApp message {MessageId} already processed; skipping.", command.WhatsAppMessageId);
            return new ProcessInboundResult(Skipped: true, NewStep: string.Empty, OutboundText: null, BookingId: null, BookingNumber: null);
        }

        var tenantId = command.TenantId;
        var phone = command.FromPhone;
        var reply = (command.Body ?? string.Empty).Trim();

        // 2) Remember the contact (display name helps the booking + future greetings).
        var profile = await profiles.GetAsync(tenantId, phone, ct);
        await profiles.UpsertAsync(new WaContactProfileUpsert(
            tenantId, phone, command.SenderDisplayName, LastRelation: null, PreferredLanguage: null, now), ct);

        var lang = ResolveLang(profile);
        var tenantName = await catalog.GetTenantDisplayNameAsync(tenantId, ct) ?? (lang == WhatsAppTemplates.Hi ? "हमारा क्लिनिक" : "our clinic");

        // 3) Log the inbound leg.
        var conversation = await conversations.GetActiveAsync(tenantId, phone, ct);
        await messageLog.LogAsync(new WaMessageLogEntry(
            tenantId, profile?.LinkedPatientId, conversation?.ConversationId, command.WhatsAppMessageId,
            "inbound", command.MessageType, reply, Status: "received", now), ct);

        // 4) Consent-reply interception: if THIS number has a pending behalf-consent OTP, the message is the
        // patient's approval/decline — resolve it and short-circuit the normal booking state machine.
        var consentResult = await consent.TryVerifyReplyAsync(tenantId, phone, reply, lang, now, ct);
        if (consentResult is not null)
        {
            await EnqueueAndLogAsync(tenantId, consentResult.PatientId, "consent_reply", phone, consentResult.OutboundText, now, ct);
            return new ProcessInboundResult(
                Skipped: false, NewStep: ConversationSteps.Done, OutboundText: consentResult.OutboundText,
                BookingId: consentResult.BookingId, BookingNumber: null);
        }

        // 5) Advance the booking state machine → produce the next outbound text (and, on confirm, a booking).
        var flow = new Flow(tenantId, phone, lang, tenantName, profile);
        var outcome = await AdvanceAsync(flow, conversation, reply, command.SenderDisplayName, now, ct);

        // 6) Enqueue the outbound prompt (stubbed send via outbox) + log the outbound leg.
        if (outcome.OutboundText is not null)
            await EnqueueAndLogAsync(tenantId, profile?.LinkedPatientId, outcome.Intent, phone, outcome.OutboundText, now, ct, outcome.ConversationId);

        return new ProcessInboundResult(
            Skipped: false, NewStep: outcome.NewStep, OutboundText: outcome.OutboundText,
            BookingId: outcome.BookingId, BookingNumber: outcome.BookingNumber);
    }

    private async Task EnqueueAndLogAsync(
        Guid tenantId, Guid? patientId, string intent, string toPhone, string text, DateTime now,
        CancellationToken ct, Guid? conversationId = null)
    {
        await outbox.EnqueueAsync(new OutboxMessage(tenantId, patientId, intent, toPhone, text, ctx.CorrelationId, now), ct);
        await messageLog.LogAsync(new WaMessageLogEntry(
            tenantId, patientId, conversationId, WhatsAppMessageId: null, "outbound", "text", text, Status: "queued", now), ct);
    }

    private static string ResolveLang(WaContactProfile? profile) =>
        string.IsNullOrWhiteSpace(profile?.PreferredLanguage) ? WhatsAppTemplates.En : profile!.PreferredLanguage;

    // ---- state machine -------------------------------------------------------------------------------

    private async Task<Outcome> AdvanceAsync(
        Flow flow, ConversationState? conversation, string reply, string? senderName, DateTime now, CancellationToken ct)
    {
        // No active conversation → greet and start.
        if (conversation is null)
            return await StartNewAsync(flow, senderName, now, ct);

        var context = ConversationContext.FromJson(conversation.ContextJson);

        return conversation.CurrentStep switch
        {
            ConversationSteps.WhoFor => await HandleWhoForAsync(flow, conversation, context, reply, now, ct),
            ConversationSteps.ChooseRelation => await HandleRelationAsync(flow, conversation, context, reply, now, ct),
            ConversationSteps.AskPatientPhone => await HandlePatientPhoneAsync(flow, conversation, context, reply, now, ct),
            ConversationSteps.ChooseDepartment => await HandleDepartmentAsync(flow, conversation, context, reply, now, ct),
            ConversationSteps.ChooseDoctor => await HandleDoctorAsync(flow, conversation, context, reply, now, ct),
            ConversationSteps.ChooseSlot => await HandleSlotAsync(flow, conversation, context, reply, now, ct),
            ConversationSteps.Confirm => await HandleConfirmAsync(flow, conversation, context, reply, now, ct),
            // 'done' or unknown → start a fresh conversation.
            _ => await StartNewAsync(flow, senderName, now, ct),
        };
    }

    private async Task<Outcome> StartNewAsync(Flow flow, string? senderName, DateTime now, CancellationToken ct)
    {
        var context = new ConversationContext { DisplayName = senderName };
        var conversationId = await conversations.CreateAsync(
            flow.TenantId, flow.Phone, ConversationSteps.WhoFor, context.ToJson(), detectedLanguage: flow.Lang, now, ct);
        return Outcome.Prompt(conversationId, ConversationSteps.WhoFor, WhatsAppTemplates.Greeting(flow.Lang, flow.TenantName), "booking_prompt");
    }

    private async Task<Outcome> HandleWhoForAsync(
        Flow flow, ConversationState conv, ConversationContext context, string reply, DateTime now, CancellationToken ct)
    {
        switch (reply)
        {
            case "1":   // myself
                return await TransitionToDepartmentsAsync(flow, conv, context with { Relation = "self" }, now, ct);
            case "2":   // someone else → ask the relation
                await conversations.UpdateAsync(conv.ConversationId, ConversationSteps.ChooseRelation, context.ToJson(), isActive: true, now, ct);
                return Outcome.Prompt(conv.ConversationId, ConversationSteps.ChooseRelation, WhatsAppTemplates.AskRelation(flow.Lang), "booking_prompt");
            default:
                return Outcome.Reprompt(conv.ConversationId, ConversationSteps.WhoFor, WhatsAppTemplates.DidntUnderstand(flow.Lang));
        }
    }

    private async Task<Outcome> HandleRelationAsync(
        Flow flow, ConversationState conv, ConversationContext context, string reply, DateTime now, CancellationToken ct)
    {
        var relation = MapRelation(reply);
        if (relation is null)
            return Outcome.Reprompt(conv.ConversationId, ConversationSteps.ChooseRelation, WhatsAppTemplates.DidntUnderstand(flow.Lang));

        // Behalf path: we need the PATIENT's number before booking (DPDP consent goes to them, not the booker).
        var next = context with { Relation = relation };
        await conversations.UpdateAsync(conv.ConversationId, ConversationSteps.AskPatientPhone, next.ToJson(), isActive: true, now, ct);
        return Outcome.Prompt(conv.ConversationId, ConversationSteps.AskPatientPhone, WhatsAppTemplates.AskPatientPhone(flow.Lang), "booking_prompt");
    }

    private async Task<Outcome> HandlePatientPhoneAsync(
        Flow flow, ConversationState conv, ConversationContext context, string reply, DateTime now, CancellationToken ct)
    {
        var patientPhone = NormalizePhone(reply);
        if (patientPhone is null)
            return Outcome.Reprompt(conv.ConversationId, ConversationSteps.AskPatientPhone, WhatsAppTemplates.AskPatientPhone(flow.Lang));

        return await TransitionToDepartmentsAsync(flow, conv, context with { PatientPhone = patientPhone }, now, ct);
    }

    private async Task<Outcome> TransitionToDepartmentsAsync(
        Flow flow, ConversationState conv, ConversationContext context, DateTime now, CancellationToken ct)
    {
        var departments = await catalog.ListDepartmentsAsync(flow.TenantId, ct);
        if (departments.Count == 0)
        {
            await conversations.UpdateAsync(conv.ConversationId, ConversationSteps.Done, context.ToJson(), isActive: false, now, ct);
            return new Outcome(conv.ConversationId, ConversationSteps.Done, WhatsAppTemplates.NothingAvailable(flow.Lang, "departments"), "booking_prompt", null, null);
        }

        var options = departments.Take(MaxOptions).ToList();
        var next = context with
        {
            DepartmentOptions = options.Select((d, i) => new OptionEntry(i + 1, d.DepartmentId, d.Name)).ToList()
        };
        await conversations.UpdateAsync(conv.ConversationId, ConversationSteps.ChooseDepartment, next.ToJson(), isActive: true, now, ct);
        return Outcome.Prompt(conv.ConversationId, ConversationSteps.ChooseDepartment, WhatsAppTemplates.ChooseDepartment(flow.Lang, options), "booking_prompt");
    }

    private async Task<Outcome> HandleDepartmentAsync(
        Flow flow, ConversationState conv, ConversationContext context, string reply, DateTime now, CancellationToken ct)
    {
        if (!TryResolveOption(context.DepartmentOptions, reply, out var chosen))
            return Outcome.Reprompt(conv.ConversationId, ConversationSteps.ChooseDepartment, WhatsAppTemplates.DidntUnderstand(flow.Lang));

        var doctors = await catalog.ListDoctorsAsync(flow.TenantId, chosen.Id, ct);
        if (doctors.Count == 0)
            return Outcome.Reprompt(conv.ConversationId, ConversationSteps.ChooseDepartment, WhatsAppTemplates.NothingAvailable(flow.Lang, "doctors"));

        var options = doctors.Take(MaxOptions).ToList();
        var next = context with
        {
            DepartmentId = chosen.Id,
            DepartmentName = chosen.Label,
            DoctorOptions = options.Select((d, i) => new OptionEntry(i + 1, d.DoctorId, d.FullName)).ToList()
        };
        await conversations.UpdateAsync(conv.ConversationId, ConversationSteps.ChooseDoctor, next.ToJson(), isActive: true, now, ct);
        return Outcome.Prompt(conv.ConversationId, ConversationSteps.ChooseDoctor, WhatsAppTemplates.ChooseDoctor(flow.Lang, chosen.Label, options), "booking_prompt");
    }

    private async Task<Outcome> HandleDoctorAsync(
        Flow flow, ConversationState conv, ConversationContext context, string reply, DateTime now, CancellationToken ct)
    {
        if (!TryResolveOption(context.DoctorOptions, reply, out var chosen))
            return Outcome.Reprompt(conv.ConversationId, ConversationSteps.ChooseDoctor, WhatsAppTemplates.DidntUnderstand(flow.Lang));

        var slots = await catalog.ListEarliestSlotsAsync(flow.TenantId, chosen.Id, MaxOptions, ct);
        if (slots.Count == 0)
            return Outcome.Reprompt(conv.ConversationId, ConversationSteps.ChooseDoctor, WhatsAppTemplates.NothingAvailable(flow.Lang, "slots"));

        var next = context with
        {
            DoctorId = chosen.Id,
            DoctorName = chosen.Label,
            SlotOptions = slots.Select((s, i) => new OptionEntry(i + 1, s.SlotId, WhatsAppTemplates.SlotLabel(s))).ToList()
        };
        await conversations.UpdateAsync(conv.ConversationId, ConversationSteps.ChooseSlot, next.ToJson(), isActive: true, now, ct);
        return Outcome.Prompt(conv.ConversationId, ConversationSteps.ChooseSlot, WhatsAppTemplates.ChooseSlot(flow.Lang, chosen.Label, slots), "booking_prompt");
    }

    private async Task<Outcome> HandleSlotAsync(
        Flow flow, ConversationState conv, ConversationContext context, string reply, DateTime now, CancellationToken ct)
    {
        if (!TryResolveOption(context.SlotOptions, reply, out var chosen))
            return Outcome.Reprompt(conv.ConversationId, ConversationSteps.ChooseSlot, WhatsAppTemplates.DidntUnderstand(flow.Lang));

        var next = context with { SlotId = chosen.Id, SlotLabel = chosen.Label };
        await conversations.UpdateAsync(conv.ConversationId, ConversationSteps.Confirm, next.ToJson(), isActive: true, now, ct);
        var isBehalf = IsBehalf(next);
        var summary = WhatsAppTemplates.ConfirmSummary(
            flow.Lang, next.DepartmentName ?? "—", next.DoctorName ?? "—", chosen.Label,
            isBehalf, isBehalf ? next.PatientPhone : null);
        return Outcome.Prompt(conv.ConversationId, ConversationSteps.Confirm, summary, "booking_prompt");
    }

    private async Task<Outcome> HandleConfirmAsync(
        Flow flow, ConversationState conv, ConversationContext context, string reply, DateTime now, CancellationToken ct)
    {
        if (!IsAffirmative(reply))
            return Outcome.Reprompt(conv.ConversationId, ConversationSteps.Confirm, WhatsAppTemplates.ConfirmHint(flow.Lang));

        if (context.SlotId is not { } slotId || context.DoctorId is not { } doctorId)
            return Outcome.Reprompt(conv.ConversationId, ConversationSteps.Confirm, WhatsAppTemplates.DidntUnderstand(flow.Lang));

        var isBehalf = IsBehalf(context);
        if (isBehalf && string.IsNullOrWhiteSpace(context.PatientPhone))
            return Outcome.Reprompt(conv.ConversationId, ConversationSteps.Confirm, WhatsAppTemplates.DidntUnderstand(flow.Lang));

        // Deterministic Idempotency-Key derived from the conversation + slot, so a retried confirm can't
        // double-book (CreateBookingCommand requires a key and is durably idempotent). The webhook has no
        // HTTP Idempotency-Key header, so we publish the key to the ambient holder the pipeline reads.
        var idempotencyKey = $"wa-{conv.ConversationId:N}-{slotId:N}";
        ambientIdempotencyKey.Set(idempotencyKey);

        // For a behalf booking the PATIENT is the booking identity; the booker is the conversation number.
        var patientPhone = isBehalf ? context.PatientPhone! : flow.Phone;

        var request = new CreateBookingRequest(
            SlotId: slotId,
            DoctorId: doctorId,
            DepartmentId: context.DepartmentId,
            PatientPhone: patientPhone,
            PatientName: isBehalf ? null : (context.DisplayName ?? flow.Profile?.DisplayName),
            PatientAge: null,
            PatientGender: null,
            BookingType: "consultation",
            BookedVia: "whatsapp",
            ChiefComplaint: null,
            IssueOpdToken: !isBehalf,   // behalf: issue the OPD token only after consent is confirmed (kept simple — staff approve)
            IdempotencyKey: idempotencyKey,
            BookedByType: isBehalf ? BookedByType.Behalf : BookedByType.Self,
            BehalfRelation: isBehalf ? context.Relation : null,
            BehalfBookerPhone: isBehalf ? flow.Phone : null);

        CreateBookingResult booking;
        try
        {
            booking = await commands.Send(new CreateBookingCommand(flow.TenantId, request), ct);
        }
        catch (Exception ex)
        {
            // Slot may have been taken between selection and confirm. Keep the conversation open and ask to retry.
            logger.LogWarning(ex, "WhatsApp booking creation failed for conversation {ConversationId}.", conv.ConversationId);
            return Outcome.Reprompt(conv.ConversationId, ConversationSteps.Confirm, WhatsAppTemplates.NothingAvailable(flow.Lang, "slots"));
        }

        // Remember the relation for next time. last_relation only carries a someone-else relation (the DB
        // check constraint excludes 'self'); a self booking leaves it untouched.
        var rememberedRelation = isBehalf ? context.Relation : null;
        await profiles.UpsertAsync(new WaContactProfileUpsert(
            flow.TenantId, flow.Phone, context.DisplayName ?? flow.Profile?.DisplayName, rememberedRelation, PreferredLanguage: null, now), ct);

        await conversations.UpdateAsync(conv.ConversationId, ConversationSteps.Done, context.ToJson(), isActive: false, now, ct);

        if (isBehalf)
        {
            // DPDP: do NOT confirm yet. Send the patient an OTP; the booking stays pending until they approve.
            await consent.SendForBehalfBookingAsync(new ConsentSendRequest(
                flow.TenantId, booking.BookingId, PatientId: null, patientPhone, BookerPhone: flow.Phone,
                Relation: context.Relation!, TenantName: flow.TenantName,
                BookerName: context.DisplayName ?? flow.Profile?.DisplayName,
                DoctorName: context.DoctorName, SlotLabel: context.SlotLabel, Lang: flow.Lang, NowUtc: now), ct);

            return new Outcome(conv.ConversationId, ConversationSteps.Done,
                WhatsAppTemplates.BehalfAwaitingConsent(flow.Lang, patientPhone), "consent_request",
                booking.BookingId, booking.BookingNumber);
        }

        var confirmation = WhatsAppTemplates.BookingConfirmation(
            flow.Lang, booking.BookingNumber, booking.TokenNumber, context.DoctorName ?? "—", context.SlotLabel ?? "—");
        return new Outcome(conv.ConversationId, ConversationSteps.Done, confirmation, "booking_confirmation",
            booking.BookingId, booking.BookingNumber);
    }

    // ---- helpers -------------------------------------------------------------------------------------

    private static bool IsBehalf(ConversationContext context) =>
        context.Relation is not null && context.Relation != "self";

    private static string? MapRelation(string reply) => reply.Trim() switch
    {
        "1" => BehalfRelation.Family,
        "2" => BehalfRelation.Friend,
        "3" => BehalfRelation.Neighbour,
        "4" => BehalfRelation.CarePartner,
        "5" => BehalfRelation.Other,
        _ => null,
    };

    /// <summary>Digits-only normalization (Meta delivers numbers without '+'); 10–15 digits or reject.</summary>
    private static string? NormalizePhone(string raw)
    {
        var digits = new string(raw.Where(char.IsDigit).ToArray());
        return digits.Length is >= 10 and <= 15 ? digits : null;
    }

    private static bool TryResolveOption(List<OptionEntry> options, string reply, out OptionEntry chosen)
    {
        chosen = default!;
        if (!int.TryParse(reply.Trim(), out var n)) return false;
        var match = options.FirstOrDefault(o => o.Number == n);
        if (match is null) return false;
        chosen = match;
        return true;
    }

    private static bool IsAffirmative(string reply)
    {
        var r = reply.Trim().ToLowerInvariant();
        return r is "yes" or "y" or "1" or "confirm" or "ok" or "haan" or "हाँ";
    }

    /// <summary>Per-turn flow context (tenant, contact number, language, tenant display name, contact profile).</summary>
    private sealed record Flow(Guid TenantId, string Phone, string Lang, string TenantName, WaContactProfile? Profile);

    /// <summary>Internal carrier for the result of one state transition.</summary>
    private sealed record Outcome(
        Guid? ConversationId, string NewStep, string? OutboundText, string Intent,
        Guid? BookingId, string? BookingNumber)
    {
        public static Outcome Prompt(Guid conversationId, string step, string text, string intent) =>
            new(conversationId, step, text, intent, null, null);

        public static Outcome Reprompt(Guid conversationId, string step, string text) =>
            new(conversationId, step, text, "booking_prompt", null, null);
    }
}
