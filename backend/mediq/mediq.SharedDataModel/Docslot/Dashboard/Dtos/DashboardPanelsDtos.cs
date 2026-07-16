namespace mediq.SharedDataModel.Docslot.Dashboard.Dtos;

/// <summary>
/// One row of the dashboard's "Department load · today" panel: live slot capacity vs booked
/// appointments per department, for today in <b>Asia/Kolkata</b>. Sourced from
/// <c>docslot.time_slots</c> (SUM(current_count) / SUM(max_count)) grouped by
/// <c>docslot.departments</c>; only departments with slots today appear. Tenant-scoped aggregate — no PHI.
/// </summary>
/// <param name="ColorKey">A design-token color key (e.g. "primary", "accent") assigned server-side by
/// display order — NEVER a hex value (REACT_SKILL tokens-only rule).</param>
public sealed record DepartmentLoadDto(
    Guid DepartmentId,
    string Name,
    string ColorKey,
    int Booked,
    int Capacity);

/// <summary>
/// One row of the dashboard's "On the floor now" panel: a doctor with OPD slots today (IST).
/// <paramref name="NextSlot"/> is the doctor's next still-available slot today as an IST instant, or null
/// when they have no free slot left today. <paramref name="SeenToday"/> counts COMPLETED bookings on
/// today's slots. Doctor identity + aggregate counts only — no patient data.
/// </summary>
public sealed record FloorDoctorDto(
    Guid DoctorId,
    string Name,
    string? Specialization,
    string? DepartmentName,
    DateTimeOffset? NextSlot,
    int SeenToday);

/// <summary>
/// The WhatsApp-agent panel read-model. All metrics are tenant-scoped aggregates (no PHI) derived from
/// <c>docslot.wa_message_log</c> (conversation activity, last 24h) and <c>docslot.bookings</c>
/// (today's WhatsApp bookings, IST). Until real conversation-state tracking exists, the percentage
/// metrics are booking-derived PROXIES with these definitions:
/// <list type="bullet">
/// <item><see cref="SelfServedPct"/> — % of greeted patients whose WhatsApp booking reached
/// confirmed/completed (the agent finished the job alone).</item>
/// <item><see cref="HandedPct"/> — % of greeted patients who ALSO have a non-WhatsApp booking today
/// (the desk took over).</item>
/// <item><see cref="DropOffPct"/> — % of greeted patients who neither confirmed via the agent nor were
/// handed to the desk (clamped ≥ 0).</item>
/// </list>
/// </summary>
/// <param name="ActiveConversations">Distinct patients with any <c>wa_message_log</c> activity in the
/// last 24 hours.</param>
/// <param name="Sparkline">24 hourly message-volume buckets (oldest first), normalised 0..1 against the
/// busiest hour; all zeros when there was no traffic.</param>
/// <param name="AvgResponseMins">Average minutes from an inbound message to the next outbound message to
/// the same patient (last 24h), rounded to 1dp; 0 when no pairs exist.</param>
/// <param name="Funnel">Today's booking funnel over WhatsApp bookings (distinct patients, monotonic
/// non-increasing). Keys are the frontend's contract enums: greeted → selectedDept → pickedSlot → confirmed.</param>
public sealed record AgentPanelDto(
    int ActiveConversations,
    IReadOnlyList<decimal> Sparkline,
    decimal AvgResponseMins,
    decimal SelfServedPct,
    decimal HandedPct,
    decimal DropOffPct,
    IReadOnlyList<AgentFunnelStageDto> Funnel);

/// <summary>One funnel stage of the agent panel. <paramref name="Key"/> is the frontend contract key
/// ("greeted" | "selectedDept" | "pickedSlot" | "confirmed"); <paramref name="Pct"/> is against the
/// greeted basis (0..100).</summary>
public sealed record AgentFunnelStageDto(string Key, int Count, decimal Pct);
