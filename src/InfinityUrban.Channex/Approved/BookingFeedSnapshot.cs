using System.Text.Json;

namespace Stayline;

/// <summary>
/// Durable operational subset of an authoritative feed revision. Never retain the raw booking
/// response: guarantees, cards, payment details, contact details and unrelated metadata are excluded.
/// </summary>
public sealed record BookingFeedSnapshot(string RevisionId, string? BookingId, string? Status, string PayloadJson)
{
    public static BookingFeedSnapshot FromFeedItem(JsonElement item)
    {
        if (!Guid.TryParse(JsonFields.Required(item, "id"), out var id) || id == Guid.Empty)
            throw new InvalidOperationException("The booking feed contains an invalid revision UUID.");
        if (JsonFields.Optional(item, "type") is { } type && type != "booking_revision")
            throw new InvalidOperationException("The booking feed contains an unexpected resource type.");
        if (!item.TryGetProperty("attributes", out var attributes) || attributes.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("The booking feed revision has no complete attributes. It was not acknowledged.");
        var payloadId = JsonFields.Optional(attributes, "id");
        if (payloadId is not null && (!Guid.TryParse(payloadId, out var attributeId) || attributeId != id))
            throw new InvalidOperationException("The booking feed revision has a mismatched identifier.");

        var result = new Dictionary<string, object?> { ["id"] = id.ToString("D") };
        foreach (var field in new[] { "property_id", "booking_id", "status", "inserted_at", "arrival_date", "departure_date" })
            CopyString(attributes, result, field);
        if (attributes.TryGetProperty("customer", out var customer) && customer.ValueKind == JsonValueKind.Object)
        {
            var guest = new Dictionary<string, object?>();
            foreach (var field in new[] { "name", "surname" }) CopyString(customer, guest, field);
            result["customer"] = guest;
        }
        if (attributes.TryGetProperty("rooms", out var rooms) && rooms.ValueKind == JsonValueKind.Array)
        {
            var stays = new List<Dictionary<string, object?>>();
            foreach (var room in rooms.EnumerateArray())
            {
                var stay = new Dictionary<string, object?>();
                foreach (var field in new[] { "room_type_id", "rate_plan_id", "checkin_date", "checkout_date" })
                    CopyString(room, stay, field);
                stays.Add(stay);
            }
            result["rooms"] = stays;
        }
        return new(id.ToString("D"), JsonFields.Optional(attributes, "booking_id"),
            JsonFields.Optional(attributes, "status"), JsonSerializer.Serialize(result));
    }

    private static void CopyString(JsonElement source, Dictionary<string, object?> target, string field)
    {
        if (JsonFields.Optional(source, field) is { } value) target[field] = value;
    }
}
