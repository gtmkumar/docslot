using System.Globalization;
using System.Text;
using mediq.Application.Abstractions;
using mediq.Domain.Docslot;

namespace mediq.Application.Features.Docslot.WhatsApp;

/// <summary>
/// Centralized outbound message text for the booking + consent flow. Every template is BILINGUAL: it takes a
/// <c>lang</c> ("en" | "hi", from the contact's <c>preferred_language</c>) and renders the matching copy.
/// The clinic name shown to the patient is the tenant's <c>display_name</c> (passed in) — never a hardcoded
/// brand. Kept here so the handler stays focused on state transitions, not string building.
/// </summary>
public static class WhatsAppTemplates
{
    public const string En = "en";
    public const string Hi = "hi";

    private static bool IsHi(string? lang) => string.Equals(lang, Hi, StringComparison.OrdinalIgnoreCase);

    public static string Greeting(string lang, string tenantName) => IsHi(lang)
        ? $"नमस्ते! {tenantName} में आपका स्वागत है। मैं आपको डॉक्टर की अपॉइंटमेंट बुक करने में मदद कर सकता/सकती हूँ।\n\n" +
          "यह अपॉइंटमेंट किसके लिए है?\n1) मेरे लिए\n2) किसी और के लिए"
        : $"Namaste! Welcome to {tenantName}. I can help you book a doctor's appointment.\n\n" +
          "Who is this appointment for?\n1) Myself\n2) Someone else";

    public static string AskRelation(string lang) => IsHi(lang)
        ? "आप किसके लिए बुक कर रहे हैं?\n1) परिवार\n2) मित्र\n3) पड़ोसी\n4) केयर पार्टनर\n5) अन्य"
        : "Who are you booking for?\n1) Family\n2) Friend\n3) Neighbour\n4) Care Partner\n5) Other";

    public static string AskPatientPhone(string lang) => IsHi(lang)
        ? "मरीज़ का WhatsApp नंबर क्या है? (देश कोड सहित, जैसे +9198…)\n\n" +
          "हम उन्हें एक पुष्टि कोड भेजेंगे — अपॉइंटमेंट उनकी सहमति के बाद ही तय होगी।"
        : "What is the patient's WhatsApp number? (with country code, e.g. +9198…)\n\n" +
          "We'll send them a confirmation code — the appointment is only set once they approve.";

    public static string ChooseDepartment(string lang, IReadOnlyList<WaDepartment> departments)
    {
        var sb = new StringBuilder(IsHi(lang) ? "ठीक है। आपको कौन-सा विभाग चाहिए?\n" : "Great. Which department do you need?\n");
        for (var i = 0; i < departments.Count; i++)
            sb.Append(CultureInfo.InvariantCulture, $"{i + 1}) {departments[i].Name}\n");
        sb.Append(IsHi(lang) ? "\nनंबर के साथ उत्तर दें।" : "\nReply with the number.");
        return sb.ToString();
    }

    public static string ChooseDoctor(string lang, string departmentName, IReadOnlyList<WaDoctor> doctors)
    {
        var sb = new StringBuilder(IsHi(lang)
            ? $"{departmentName} के उपलब्ध डॉक्टर:\n"
            : $"{departmentName} doctors available:\n");
        for (var i = 0; i < doctors.Count; i++)
        {
            var fee = doctors[i].ConsultationFee is { } f
                ? (IsHi(lang)
                    ? $" — ₹{f.ToString("0", CultureInfo.InvariantCulture)} परामर्श"
                    : $" — ₹{f.ToString("0", CultureInfo.InvariantCulture)} consultation")
                : "";
            sb.Append(CultureInfo.InvariantCulture, $"{i + 1}) {doctors[i].FullName}{fee}\n");
        }
        sb.Append(IsHi(lang) ? "\nनंबर के साथ उत्तर दें।" : "\nReply with the number.");
        return sb.ToString();
    }

    public static string ChooseSlot(string lang, string doctorName, IReadOnlyList<WaSlot> slots)
    {
        var sb = new StringBuilder(IsHi(lang)
            ? $"{doctorName} के लिए उपलब्ध समय:\n"
            : $"Available slots for {doctorName}:\n");
        for (var i = 0; i < slots.Count; i++)
            sb.Append(CultureInfo.InvariantCulture, $"{i + 1}) {SlotLabel(slots[i])}\n");
        sb.Append(IsHi(lang) ? "\nनंबर के साथ उत्तर दें।" : "\nReply with the number.");
        return sb.ToString();
    }

    public static string SlotLabel(WaSlot slot) =>
        $"{slot.SlotDate.ToString("ddd dd MMM", CultureInfo.InvariantCulture)} at {slot.StartTime.ToString("hh:mm tt", CultureInfo.InvariantCulture)}";

    public static string ConfirmSummary(
        string lang, string departmentName, string doctorName, string slotLabel,
        bool isBehalf = false, string? patientLabel = null)
    {
        var hi = IsHi(lang);
        var sb = new StringBuilder(hi ? "कृपया अपनी अपॉइंटमेंट की पुष्टि करें:\n\n" : "Please confirm your appointment:\n\n");
        if (isBehalf && patientLabel is not null)
            sb.Append(hi ? $"मरीज़: {patientLabel}\n" : $"Patient: {patientLabel}\n");
        sb.Append(hi ? $"विभाग: {departmentName}\n" : $"Department: {departmentName}\n");
        sb.Append(hi ? $"डॉक्टर: {doctorName}\n" : $"Doctor: {doctorName}\n");
        sb.Append(hi ? $"समय: {slotLabel}\n\n" : $"When: {slotLabel}\n\n");
        sb.Append(hi ? "पुष्टि के लिए YES लिखें।" : "Reply YES to confirm.");
        return sb.ToString();
    }

    public static string BookingConfirmation(string lang, string? bookingNumber, int? tokenNumber, string doctorName, string slotLabel)
    {
        var hi = IsHi(lang);
        var token = tokenNumber?.ToString(CultureInfo.InvariantCulture);
        var sb = new StringBuilder(hi ? "आपकी अपॉइंटमेंट तय हो गई है! ✅\n\n" : "Your appointment is confirmed! ✅\n\n");
        if (bookingNumber is not null) sb.Append(hi ? $"बुकिंग: {bookingNumber}\n" : $"Booking: {bookingNumber}\n");
        if (token is not null) sb.Append(hi ? $"OPD टोकन: {token}\n" : $"OPD Token: {token}\n");
        sb.Append(hi ? $"डॉक्टर: {doctorName}\n" : $"Doctor: {doctorName}\n");
        sb.Append(hi ? $"समय: {slotLabel}\n\n" : $"When: {slotLabel}\n\n");
        sb.Append(hi
            ? "कृपया 10 मिनट पहले पहुँचें। दोबारा बुक करने के लिए कभी भी संदेश भेजें।"
            : "Please arrive 10 minutes early. Reply to this chat anytime to rebook.");
        return sb.ToString();
    }

    // ---- behalf consent -----------------------------------------------------------------------------

    /// <summary>Message to the BOOKER once the patient OTP has been dispatched.</summary>
    public static string BehalfAwaitingConsent(string lang, string patientPhone) => IsHi(lang)
        ? $"धन्यवाद! हमने {patientPhone} पर एक पुष्टि अनुरोध भेजा है। जैसे ही वे स्वीकृति देंगे, अपॉइंटमेंट तय हो जाएगी।"
        : $"Thanks! We've sent a confirmation request to {patientPhone}. The appointment is set as soon as they approve.";

    /// <summary>OTP message to the PATIENT, naming the booker + claimed relation (DPDP transparency).</summary>
    public static string ConsentRequest(
        string lang, string tenantName, string bookerLabel, string relation,
        string doctorName, string slotLabel, string code)
    {
        var rel = RelationLabel(lang, relation);
        return IsHi(lang)
            ? $"नमस्ते! {tenantName} में {bookerLabel} ({rel}) ने आपके लिए एक अपॉइंटमेंट बुक की है:\n\n" +
              $"डॉक्टर: {doctorName}\nसमय: {slotLabel}\n\n" +
              $"पुष्टि के लिए यह कोड भेजें: *{code}*\n" +
              "यदि आपने यह अनुरोध नहीं किया, तो NO लिखें।"
            : $"Namaste! At {tenantName}, {bookerLabel} ({rel}) booked an appointment for you:\n\n" +
              $"Doctor: {doctorName}\nWhen: {slotLabel}\n\n" +
              $"Reply with this code to approve: *{code}*\n" +
              "If you did not request this, reply NO.";
    }

    public static string ConsentConfirmed(string lang) => IsHi(lang)
        ? "धन्यवाद! आपकी अपॉइंटमेंट की पुष्टि हो गई है। ✅"
        : "Thank you! Your appointment is confirmed. ✅";

    public static string ConsentDenied(string lang) => IsHi(lang)
        ? "ठीक है, हमने वह अनुरोध रद्द कर दिया है। आपकी सहमति के बिना कोई अपॉइंटमेंट नहीं बनाई गई।"
        : "Okay, we've cancelled that request. No appointment was made without your consent.";

    public static string ConsentExpired(string lang) => IsHi(lang)
        ? "यह पुष्टि अनुरोध समाप्त हो गया है। नई अपॉइंटमेंट के लिए कृपया दोबारा संदेश भेजें।"
        : "That confirmation request has expired. Please message us again to book afresh.";

    public static string ConsentWrongCode(string lang, int attemptsRemaining) => IsHi(lang)
        ? $"वह कोड मेल नहीं खाया। कृपया दोबारा प्रयास करें ({attemptsRemaining} प्रयास शेष)। रद्द करने के लिए NO लिखें।"
        : $"That code didn't match. Please try again ({attemptsRemaining} attempts left). Reply NO to cancel.";

    // ---- generic --------------------------------------------------------------------------------------

    public static string DidntUnderstand(string lang) => IsHi(lang)
        ? "क्षमा करें, समझ नहीं आया। कृपया ऊपर दिए गए नंबरों में से किसी एक के साथ उत्तर दें।"
        : "Sorry, I didn't catch that. Please reply with one of the numbers shown above.";

    public static string NothingAvailable(string lang, string what) => IsHi(lang)
        ? $"क्षमा करें, अभी कोई {what} उपलब्ध नहीं है। कृपया बाद में पुनः प्रयास करें।"
        : $"Sorry, no {what} are available right now. Please try again later.";

    public static string ConfirmHint(string lang) => IsHi(lang)
        ? "पुष्टि के लिए YES लिखें, या नई बुकिंग शुरू करने के लिए कोई संदेश भेजें।"
        : "Please reply YES to confirm, or send a new message to start over.";

    public static string RelationLabel(string lang, string relation) => relation switch
    {
        mediq.Domain.Docslot.BehalfRelation.Family => IsHi(lang) ? "परिवार" : "Family",
        mediq.Domain.Docslot.BehalfRelation.Friend => IsHi(lang) ? "मित्र" : "Friend",
        mediq.Domain.Docslot.BehalfRelation.Neighbour => IsHi(lang) ? "पड़ोसी" : "Neighbour",
        mediq.Domain.Docslot.BehalfRelation.CarePartner => IsHi(lang) ? "केयर पार्टनर" : "Care Partner",
        _ => IsHi(lang) ? "अन्य" : "Other",
    };
}
