using mediq.Application.Abstractions;
using mediq.Application.Cqrs;
using mediq.Application.Features.Docslot.Bookings;
using Microsoft.Extensions.Logging;

namespace mediq.Application.Features.Docslot.WhatsApp;

/// <summary>
/// The inbound booking state machine. Runs INSIDE the command pipeline's tenant-scoped UoW transaction
/// (tenant resolved at the edge → <see cref="ITenantScopeOverride"/> → RLS <c>app.tenant_id</c>), so every
/// wa_* / conversation write is tenant-scoped. Booking creation is delegated to the audited
/// <see cref="CreateBookingCommand"/> via the inner dispatcher (reusing holds, OPD token, events, audit).
/// </summary>
public sealed class ProcessInboundWhatsAppMessageCommandHandler(
    IProcessedMessageStore processed,
    IWaMessageLogWriter messageLog,
    IWaContactProfileRepository profiles,
    IConversationRepository conversations,
    IWhatsAppCatalogReadService catalog,
    IOutboxMessageEnqueuer outbox,
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

        // 3) Log the inbound leg.
        var conversation = await conversations.GetActiveAsync(tenantId, phone, ct);
        await messageLog.LogAsync(new WaMessageLogEntry(
            tenantId, profile?.LinkedPatientId, conversation?.ConversationId, command.WhatsAppMessageId,
            "inbound", command.MessageType, reply, Status: "received", now), ct);

        // 4) Advance the state machine → produce the next outbound text (and, on confirm, a booking).
        var outcome = await AdvanceAsync(tenantId, phone, profile, conversation, reply, command.SenderDisplayName, now, ct);

        // 5) Enqueue the outbound prompt (stubbed send via outbox) + log the outbound leg.
        if (outcome.OutboundText is not null)
        {
            await outbox.EnqueueAsync(new OutboxMessage(
                tenantId, profile?.LinkedPatientId, outcome.Intent, phone, outcome.OutboundText,
                ctx.CorrelationId, now), ct);

            await messageLog.LogAsync(new WaMessageLogEntry(
                tenantId, profile?.LinkedPatientId, outcome.ConversationId, WhatsAppMessageId: null,
                "outbound", "text", outcome.OutboundText, Status: "queued", now), ct);
        }

        return new ProcessInboundResult(
            Skipped: false, NewStep: outcome.NewStep, OutboundText: outcome.OutboundText,
            BookingId: outcome.BookingId, BookingNumber: outcome.BookingNumber);
    }

    // ---- state machine -------------------------------------------------------------------------------

    private async Task<Outcome> AdvanceAsync(
        Guid tenantId, string phone, WaContactProfile? profile, ConversationState? conversation,
        string reply, string? senderName, DateTime now, CancellationToken ct)
    {
        // No active conversation → greet and start.
        if (conversation is null)
            return await StartNewAsync(tenantId, phone, senderName, now, ct);

        var context = ConversationContext.FromJson(conversation.ContextJson);

        return conversation.CurrentStep switch
        {
            ConversationSteps.WhoFor => await HandleWhoForAsync(conversation, context, reply, now, ct),
            ConversationSteps.ChooseRelation => await HandleRelationAsync(tenantId, conversation, context, reply, now, ct),
            ConversationSteps.ChooseDepartment => await HandleDepartmentAsync(tenantId, conversation, context, reply, now, ct),
            ConversationSteps.ChooseDoctor => await HandleDoctorAsync(tenantId, conversation, context, reply, now, ct),
            ConversationSteps.ChooseSlot => await HandleSlotAsync(conversation, context, reply, now, ct),
            ConversationSteps.Confirm => await HandleConfirmAsync(tenantId, phone, profile, conversation, context, reply, now, ct),
            // 'done' or unknown → start a fresh conversation.
            _ => await StartNewAsync(tenantId, phone, senderName, now, ct),
        };
    }

    private async Task<Outcome> StartNewAsync(Guid tenantId, string phone, string? senderName, DateTime now, CancellationToken ct)
    {
        var context = new ConversationContext { DisplayName = senderName };
        var conversationId = await conversations.CreateAsync(
            tenantId, phone, ConversationSteps.WhoFor, context.ToJson(), detectedLanguage: "en", now, ct);
        return Outcome.Prompt(conversationId, ConversationSteps.WhoFor, WhatsAppTemplates.Greeting(), "booking_prompt");
    }

    private async Task<Outcome> HandleWhoForAsync(
        ConversationState conv, ConversationContext context, string reply, DateTime now, CancellationToken ct)
    {
        switch (reply)
        {
            case "1":   // myself
                return await TransitionToDepartmentsAsync(conv, context with { Relation = "self" }, now, ct);
            case "2":   // someone else → ask the relation
                await conversations.UpdateAsync(conv.ConversationId, ConversationSteps.ChooseRelation, context.ToJson(), isActive: true, now, ct);
                return Outcome.Prompt(conv.ConversationId, ConversationSteps.ChooseRelation, WhatsAppTemplates.AskRelation(), "booking_prompt");
            default:
                return Outcome.Reprompt(conv.ConversationId, ConversationSteps.WhoFor, WhatsAppTemplates.DidntUnderstand());
        }
    }

    private async Task<Outcome> HandleRelationAsync(
        Guid tenantId, ConversationState conv, ConversationContext context, string reply, DateTime now, CancellationToken ct)
    {
        var relation = reply switch { "1" => "family", "2" => "friend", "3" => "care_partner", _ => null };
        if (relation is null)
            return Outcome.Reprompt(conv.ConversationId, ConversationSteps.ChooseRelation, WhatsAppTemplates.DidntUnderstand());

        return await TransitionToDepartmentsAsync(conv, context with { Relation = relation }, now, ct);
    }

    private async Task<Outcome> TransitionToDepartmentsAsync(
        ConversationState conv, ConversationContext context, DateTime now, CancellationToken ct)
    {
        var departments = await catalog.ListDepartmentsAsync(conv.TenantId, ct);
        if (departments.Count == 0)
        {
            await conversations.UpdateAsync(conv.ConversationId, ConversationSteps.Done, context.ToJson(), isActive: false, now, ct);
            return new Outcome(conv.ConversationId, ConversationSteps.Done, WhatsAppTemplates.NothingAvailable("departments"), "booking_prompt", null, null);
        }

        var options = departments.Take(MaxOptions).ToList();
        var next = context with
        {
            DepartmentOptions = options.Select((d, i) => new OptionEntry(i + 1, d.DepartmentId, d.Name)).ToList()
        };
        await conversations.UpdateAsync(conv.ConversationId, ConversationSteps.ChooseDepartment, next.ToJson(), isActive: true, now, ct);
        return Outcome.Prompt(conv.ConversationId, ConversationSteps.ChooseDepartment, WhatsAppTemplates.ChooseDepartment(options), "booking_prompt");
    }

    private async Task<Outcome> HandleDepartmentAsync(
        Guid tenantId, ConversationState conv, ConversationContext context, string reply, DateTime now, CancellationToken ct)
    {
        if (!TryResolveOption(context.DepartmentOptions, reply, out var chosen))
            return Outcome.Reprompt(conv.ConversationId, ConversationSteps.ChooseDepartment, WhatsAppTemplates.DidntUnderstand());

        var doctors = await catalog.ListDoctorsAsync(tenantId, chosen.Id, ct);
        if (doctors.Count == 0)
            return Outcome.Reprompt(conv.ConversationId, ConversationSteps.ChooseDepartment, WhatsAppTemplates.NothingAvailable("doctors") + "\nPlease pick another department.");

        var options = doctors.Take(MaxOptions).ToList();
        var next = context with
        {
            DepartmentId = chosen.Id,
            DepartmentName = chosen.Label,
            DoctorOptions = options.Select((d, i) => new OptionEntry(i + 1, d.DoctorId, d.FullName)).ToList()
        };
        await conversations.UpdateAsync(conv.ConversationId, ConversationSteps.ChooseDoctor, next.ToJson(), isActive: true, now, ct);
        return Outcome.Prompt(conv.ConversationId, ConversationSteps.ChooseDoctor, WhatsAppTemplates.ChooseDoctor(chosen.Label, options), "booking_prompt");
    }

    private async Task<Outcome> HandleDoctorAsync(
        Guid tenantId, ConversationState conv, ConversationContext context, string reply, DateTime now, CancellationToken ct)
    {
        if (!TryResolveOption(context.DoctorOptions, reply, out var chosen))
            return Outcome.Reprompt(conv.ConversationId, ConversationSteps.ChooseDoctor, WhatsAppTemplates.DidntUnderstand());

        var slots = await catalog.ListEarliestSlotsAsync(tenantId, chosen.Id, MaxOptions, ct);
        if (slots.Count == 0)
            return Outcome.Reprompt(conv.ConversationId, ConversationSteps.ChooseDoctor, WhatsAppTemplates.NothingAvailable("slots") + "\nPlease pick another doctor.");

        var next = context with
        {
            DoctorId = chosen.Id,
            DoctorName = chosen.Label,
            SlotOptions = slots.Select((s, i) => new OptionEntry(i + 1, s.SlotId, WhatsAppTemplates.SlotLabel(s))).ToList()
        };
        await conversations.UpdateAsync(conv.ConversationId, ConversationSteps.ChooseSlot, next.ToJson(), isActive: true, now, ct);
        return Outcome.Prompt(conv.ConversationId, ConversationSteps.ChooseSlot, WhatsAppTemplates.ChooseSlot(chosen.Label, slots), "booking_prompt");
    }

    private async Task<Outcome> HandleSlotAsync(
        ConversationState conv, ConversationContext context, string reply, DateTime now, CancellationToken ct)
    {
        if (!TryResolveOption(context.SlotOptions, reply, out var chosen))
            return Outcome.Reprompt(conv.ConversationId, ConversationSteps.ChooseSlot, WhatsAppTemplates.DidntUnderstand());

        var next = context with { SlotId = chosen.Id, SlotLabel = chosen.Label };
        await conversations.UpdateAsync(conv.ConversationId, ConversationSteps.Confirm, next.ToJson(), isActive: true, now, ct);
        var summary = WhatsAppTemplates.ConfirmSummary(next.DepartmentName ?? "—", next.DoctorName ?? "—", chosen.Label);
        return Outcome.Prompt(conv.ConversationId, ConversationSteps.Confirm, summary, "booking_prompt");
    }

    private async Task<Outcome> HandleConfirmAsync(
        Guid tenantId, string phone, WaContactProfile? profile, ConversationState conv,
        ConversationContext context, string reply, DateTime now, CancellationToken ct)
    {
        if (!IsAffirmative(reply))
            return Outcome.Reprompt(conv.ConversationId, ConversationSteps.Confirm, WhatsAppTemplates.ConfirmHint());

        if (context.SlotId is not { } slotId || context.DoctorId is not { } doctorId)
            return Outcome.Reprompt(conv.ConversationId, ConversationSteps.Confirm, WhatsAppTemplates.DidntUnderstand());

        // Deterministic Idempotency-Key derived from the conversation + slot, so a retried confirm can't
        // double-book (CreateBookingCommand requires a key and is durably idempotent). The webhook has no
        // HTTP Idempotency-Key header, so we publish the key to the ambient holder the pipeline reads.
        var idempotencyKey = $"wa-{conv.ConversationId:N}-{slotId:N}";
        ambientIdempotencyKey.Set(idempotencyKey);

        var request = new CreateBookingRequest(
            SlotId: slotId,
            DoctorId: doctorId,
            DepartmentId: context.DepartmentId,
            PatientPhone: phone,
            PatientName: context.DisplayName ?? profile?.DisplayName,
            PatientAge: null,
            PatientGender: null,
            BookingType: "consultation",
            BookedVia: "whatsapp",
            ChiefComplaint: null,
            IssueOpdToken: true,
            IdempotencyKey: idempotencyKey);

        CreateBookingResult booking;
        try
        {
            booking = await commands.Send(new CreateBookingCommand(tenantId, request), ct);
        }
        catch (Exception ex)
        {
            // Slot may have been taken between selection and confirm. Keep the conversation open and ask to retry.
            logger.LogWarning(ex, "WhatsApp booking creation failed for conversation {ConversationId}.", conv.ConversationId);
            return Outcome.Reprompt(conv.ConversationId, ConversationSteps.Confirm,
                "That slot is no longer available. Send any message to start over and pick another time.");
        }

        // Remember the relation/name for next time. last_relation only carries a someone-else relation
        // (the DB check constraint excludes 'self'); a self booking leaves it untouched.
        var rememberedRelation = context.Relation is "self" ? null : context.Relation;
        await profiles.UpsertAsync(new WaContactProfileUpsert(
            tenantId, phone, context.DisplayName ?? profile?.DisplayName, rememberedRelation, PreferredLanguage: null, now), ct);

        await conversations.UpdateAsync(conv.ConversationId, ConversationSteps.Done, context.ToJson(), isActive: false, now, ct);

        var confirmation = WhatsAppTemplates.BookingConfirmation(
            booking.BookingNumber, booking.TokenNumber, context.DoctorName ?? "—", context.SlotLabel ?? "—");

        return new Outcome(conv.ConversationId, ConversationSteps.Done, confirmation, "booking_confirmation",
            booking.BookingId, booking.BookingNumber);
    }

    // ---- helpers -------------------------------------------------------------------------------------

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
