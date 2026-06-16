using System.Globalization;
using System.Text;
using mediq.Application.Abstractions;

namespace mediq.Application.Features.Docslot.WhatsApp;

/// <summary>
/// Centralized outbound message text for the booking flow. English copy (a Hindi variant is provided for the
/// greeting as a bonus). Kept here so the handler stays focused on state transitions, not string building.
/// </summary>
public static class WhatsAppTemplates
{
    public static string Greeting() =>
        "Namaste! Welcome to Apollo Care. I can help you book a doctor's appointment.\n\n" +
        "Who is this appointment for?\n1) Myself\n2) Someone else";

    public static string GreetingHi() =>
        "नमस्ते! अपोलो केयर में आपका स्वागत है। मैं आपको डॉक्टर की अपॉइंटमेंट बुक करने में मदद कर सकता/सकती हूँ।\n\n" +
        "यह अपॉइंटमेंट किसके लिए है?\n1) मेरे लिए\n2) किसी और के लिए";

    public static string AskRelation() =>
        "Who are you booking for?\n1) Family\n2) Friend\n3) Care Partner";

    public static string ChooseDepartment(IReadOnlyList<WaDepartment> departments)
    {
        var sb = new StringBuilder("Great. Which department do you need?\n");
        for (var i = 0; i < departments.Count; i++)
            sb.Append(CultureInfo.InvariantCulture, $"{i + 1}) {departments[i].Name}\n");
        sb.Append("\nReply with the number.");
        return sb.ToString();
    }

    public static string ChooseDoctor(string departmentName, IReadOnlyList<WaDoctor> doctors)
    {
        var sb = new StringBuilder($"{departmentName} doctors available:\n");
        for (var i = 0; i < doctors.Count; i++)
        {
            var fee = doctors[i].ConsultationFee is { } f
                ? $" — ₹{f.ToString("0", CultureInfo.InvariantCulture)} consultation"
                : "";
            sb.Append(CultureInfo.InvariantCulture, $"{i + 1}) {doctors[i].FullName}{fee}\n");
        }
        sb.Append("\nReply with the number.");
        return sb.ToString();
    }

    public static string ChooseSlot(string doctorName, IReadOnlyList<WaSlot> slots)
    {
        var sb = new StringBuilder($"Available slots for {doctorName}:\n");
        for (var i = 0; i < slots.Count; i++)
            sb.Append(CultureInfo.InvariantCulture, $"{i + 1}) {SlotLabel(slots[i])}\n");
        sb.Append("\nReply with the number.");
        return sb.ToString();
    }

    public static string SlotLabel(WaSlot slot) =>
        $"{slot.SlotDate.ToString("ddd dd MMM", CultureInfo.InvariantCulture)} at {slot.StartTime.ToString("hh:mm tt", CultureInfo.InvariantCulture)}";

    public static string ConfirmSummary(string departmentName, string doctorName, string slotLabel) =>
        "Please confirm your appointment:\n\n" +
        $"Department: {departmentName}\n" +
        $"Doctor: {doctorName}\n" +
        $"When: {slotLabel}\n\n" +
        "Reply YES to confirm.";

    public static string BookingConfirmation(string? bookingNumber, int? tokenNumber, string doctorName, string slotLabel)
    {
        var sb = new StringBuilder("Your appointment is confirmed! ✅\n\n");
        if (bookingNumber is not null) sb.Append(CultureInfo.InvariantCulture, $"Booking: {bookingNumber}\n");
        if (tokenNumber is not null) sb.Append(CultureInfo.InvariantCulture, $"OPD Token: {tokenNumber}\n");
        sb.Append(CultureInfo.InvariantCulture, $"Doctor: {doctorName}\n");
        sb.Append(CultureInfo.InvariantCulture, $"When: {slotLabel}\n\n");
        sb.Append("Please arrive 10 minutes early. Reply to this chat anytime to rebook.");
        return sb.ToString();
    }

    public static string DidntUnderstand() =>
        "Sorry, I didn't catch that. Please reply with one of the numbers shown above.";

    public static string NothingAvailable(string what) =>
        $"Sorry, no {what} are available right now. Please try again later.";

    public static string ConfirmHint() => "Please reply YES to confirm, or send a new message to start over.";
}
