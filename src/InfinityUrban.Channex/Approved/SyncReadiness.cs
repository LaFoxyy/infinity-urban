using Microsoft.EntityFrameworkCore;

namespace Stayline;

public record SyncReadiness(bool Ready, bool Configured, bool PropertyMapped, int RoomCount,
    int RateCount, int UnmappedRoomCount, int UnmappedRateCount, string[] Issues)
{
    public static async Task<SyncReadiness> CheckAsync(AppDb db, Property property, bool configured, CancellationToken ct = default)
    {
        var rooms = await db.RoomTypes.Where(x => x.PropertyId == property.Id).ToListAsync(ct);
        var ids = rooms.Select(x => x.Id).ToList();
        var rates = await db.RatePlans.Where(x => ids.Contains(x.RoomTypeId)).ToListAsync(ct);
        var propertyMapped = ValidId(property.ChannexId);
        var unmappedRooms = rooms.Count(x => !ValidId(x.ChannexId));
        var unmappedRates = rates.Count(x => !ValidId(x.ChannexId));
        var issues = new List<string>();
        if (!configured) issues.Add("Configure and enable Channex staging on the server.");
        if (rooms.Count == 0) issues.Add("Add room types or import a property from Channex staging.");
        if (rates.Count == 0) issues.Add("Add at least one rate plan before syncing.");
        var noRates = rooms.Where(room => !rates.Any(rate => rate.RoomTypeId == room.Id)).Select(x => x.Name).ToArray();
        if (noRates.Length > 0) issues.Add("Add a rate plan for: " + string.Join(", ", noRates) + ".");
        if (!propertyMapped) issues.Add("Save a matching-currency Channex property mapping after reviewing your local calendar.");
        if (unmappedRooms > 0) issues.Add($"Map all room types ({unmappedRooms} remaining).");
        if (unmappedRates > 0) issues.Add($"Map all rate plans ({unmappedRates} remaining).");
        return new(issues.Count == 0, configured, propertyMapped, rooms.Count, rates.Count, unmappedRooms, unmappedRates, issues.ToArray());
    }
    private static bool ValidId(string? id) => Guid.TryParse(id, out var value) && value != Guid.Empty;
}
