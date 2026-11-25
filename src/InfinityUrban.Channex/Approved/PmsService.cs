using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;

namespace Stayline;

public record CreatePropertyInput(string Name, string Kind, string Currency, string TimeZoneId);
public record CreateRoomInput(string Name, int Quantity, int Occupancy);
public record CreateRateInput(string Name, decimal BasePrice);
public record CalendarInput(List<CalendarChange> Changes, bool Reapply = false);
public record CalendarChange(Guid? RoomTypeId, Guid? RatePlanId, DateOnly DateFrom, DateOnly DateTo,
    int? Capacity = null, decimal? Rate = null, int? MinStayArrival = null, int? MinStayThrough = null,
    int? MaxStay = null, bool? StopSell = null, bool? ClosedToArrival = null, bool? ClosedToDeparture = null);
public record ReservationInput(string GuestName, Guid RoomTypeId, Guid RatePlanId, int Quantity, DateOnly Arrival, DateOnly Departure);
public record MappingItem(Guid Id, string? ChannexId);
public record MappingInput(string? ChannexId, List<MappingItem> Rooms, List<MappingItem> Rates);
public class AppError(string message, int status = 400) : Exception(message) { public int Status { get; } = status; }

public class PmsService(IDbContextFactory<AppDb> factory, MutationLock mutationLock)
{
    public static readonly string[] Currencies = ["USD", "BRL", "EUR", "GBP", "CAD", "AUD", "NZD", "MXN", "CHF"];
    public static DateOnly Today(Property p) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(p.TimeZoneId)).DateTime);

    private async Task<T> WriteAsync<T>(Func<AppDb, Task<T>> action, CancellationToken ct)
    {
        await mutationLock.Gate.WaitAsync(ct);
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var result = await action(db);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return result;
        }
        finally { mutationLock.Gate.Release(); }
    }

    public Task<Property> CreatePropertyAsync(CreatePropertyInput input, CancellationToken ct = default) => WriteAsync(async db =>
    {
        var name = Name(input.Name);
        Require(input.Kind is "hotel" or "rental", "Choose hotel or rental.");
        var currency = (input.Currency ?? "").Trim().ToUpperInvariant();
        Require(Currencies.Contains(currency), "Choose a supported two-decimal currency: " + string.Join(", ", Currencies));
        try { TimeZoneInfo.FindSystemTimeZoneById(input.TimeZoneId ?? ""); }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        { throw new AppError("Enter a valid time zone, such as America/Sao_Paulo or UTC."); }
        var property = new Property { Name = name, Kind = input.Kind, Currency = currency, TimeZoneId = input.TimeZoneId! };
        db.Properties.Add(property);
        await Task.CompletedTask;
        return property;
    }, ct);

    public Task<RoomType> CreateRoomAsync(Guid propertyId, CreateRoomInput input, CancellationToken ct = default) => WriteAsync(async db =>
    {
        var property = await PropertyAsync(db, propertyId, ct);
        Require(input.Quantity is >= 1 and <= 1000, "Inventory must be between 1 and 1000.");
        Require(property.Kind != "rental" || input.Quantity == 1, "Each rental unit must have inventory of one. Add another unit for another rental.");
        Require(input.Occupancy is >= 1 and <= 30, "Occupancy must be between 1 and 30.");
        var room = new RoomType { PropertyId = propertyId, Name = Name(input.Name), Quantity = input.Quantity, Occupancy = input.Occupancy };
        db.RoomTypes.Add(room);
        Enqueue(db, propertyId, "availability", room.Id, Today(property), Today(property).AddDays(500));
        return room;
    }, ct);

    public Task<RatePlan> CreateRateAsync(Guid roomId, CreateRateInput input, CancellationToken ct = default) => WriteAsync(async db =>
    {
        var room = await db.RoomTypes.SingleOrDefaultAsync(x => x.Id == roomId, ct) ?? throw new AppError("Room type not found.", 404);
        var property = await PropertyAsync(db, room.PropertyId, ct);
        Price(input.BasePrice);
        var rate = new RatePlan { RoomTypeId = roomId, Name = Name(input.Name), BasePrice = input.BasePrice };
        db.RatePlans.Add(rate);
        Enqueue(db, property.Id, "restrictions", rate.Id, Today(property), Today(property).AddDays(500));
        return rate;
    }, ct);

    public Task<int> SaveCalendarAsync(Guid propertyId, CalendarInput input, CancellationToken ct = default) => WriteAsync(async db =>
    {
        var property = await PropertyAsync(db, propertyId, ct);
        Require(input.Changes is { Count: > 0 and <= 100 }, "Add between 1 and 100 calendar changes.");
        var rooms = await db.RoomTypes.Where(x => x.PropertyId == propertyId).ToListAsync(ct);
        var ids = rooms.Select(x => x.Id).ToList();
        var rates = await db.RatePlans.Where(x => ids.Contains(x.RoomTypeId)).ToListAsync(ct);
        var count = 0;
        foreach (var change in input.Changes!)
        {
            ValidateDates(property, change.DateFrom, change.DateTo);
            Require(change.RoomTypeId.HasValue ^ change.RatePlanId.HasValue, "Choose either a room inventory change or a rate plan change.");
            if (change.RoomTypeId is Guid roomId)
            {
                var room = rooms.SingleOrDefault(x => x.Id == roomId) ?? throw new AppError("Room type does not belong to this property.");
                Require(change.Capacity.HasValue && change.Capacity >= 0 && change.Capacity <= room.Quantity, "Open inventory must be between zero and the room type's total inventory.");
                Require(change.Rate == null && change.MinStayArrival == null && change.MinStayThrough == null && change.MaxStay == null && change.StopSell == null && change.ClosedToArrival == null && change.ClosedToDeparture == null, "Use a rate plan to change prices or restrictions.");
                var existing = await db.DailyInventories.Where(x => x.RoomTypeId == roomId && x.Date >= change.DateFrom && x.Date <= change.DateTo).ToListAsync(ct);
                var bookings = await ActiveReservationsAsync(db, propertyId, ct);
                for (var date = change.DateFrom; date <= change.DateTo; date = date.AddDays(1))
                {
                    Require(change.Capacity >= Booked(bookings, roomId, date), "Open inventory cannot be below existing reservations. Use a stop-sell restriction to close sales.");
                    var row = db.DailyInventories.Local.FirstOrDefault(x => x.RoomTypeId == roomId && x.Date == date) ?? existing.SingleOrDefault(x => x.Date == date);
                    if (row == null) { row = new DailyInventory { RoomTypeId = roomId, Date = date, Capacity = room.Quantity }; db.DailyInventories.Add(row); }
                    if (row.Capacity == change.Capacity && !input.Reapply) continue;
                    row.Capacity = change.Capacity!.Value;
                    Enqueue(db, propertyId, "availability", roomId, date, date.AddDays(1)); count++;
                }
            }
            else
            {
                var rate = rates.SingleOrDefault(x => x.Id == change.RatePlanId) ?? throw new AppError("Rate plan does not belong to this property.");
                Require(change.Capacity == null, "Use room inventory to change capacity.");
                Require(change.Rate.HasValue || change.MinStayArrival.HasValue || change.MinStayThrough.HasValue || change.MaxStay.HasValue || change.StopSell.HasValue || change.ClosedToArrival.HasValue || change.ClosedToDeparture.HasValue, "Choose at least one price or restriction to change.");
                if (change.Rate.HasValue) Price(change.Rate.Value);
                var existing = await db.DailyRates.Where(x => x.RatePlanId == rate.Id && x.Date >= change.DateFrom && x.Date <= change.DateTo).ToListAsync(ct);
                for (var date = change.DateFrom; date <= change.DateTo; date = date.AddDays(1))
                {
                    var row = db.DailyRates.Local.FirstOrDefault(x => x.RatePlanId == rate.Id && x.Date == date) ?? existing.SingleOrDefault(x => x.Date == date);
                    if (row == null) { row = new DailyRate { RatePlanId = rate.Id, Date = date, Rate = rate.BasePrice }; db.DailyRates.Add(row); }
                    var before = (row.Rate, row.MinStayArrival, row.MinStayThrough, row.MaxStay, row.StopSell, row.ClosedToArrival, row.ClosedToDeparture);
                    if (change.Rate.HasValue) row.Rate = change.Rate.Value;
                    if (change.MinStayArrival.HasValue) row.MinStayArrival = change.MinStayArrival.Value;
                    if (change.MinStayThrough.HasValue) row.MinStayThrough = change.MinStayThrough.Value;
                    if (change.MaxStay.HasValue) row.MaxStay = change.MaxStay.Value;
                    if (change.StopSell.HasValue) row.StopSell = change.StopSell.Value;
                    if (change.ClosedToArrival.HasValue) row.ClosedToArrival = change.ClosedToArrival.Value;
                    if (change.ClosedToDeparture.HasValue) row.ClosedToDeparture = change.ClosedToDeparture.Value;
                    Require(row.MinStayArrival is >= 1 and <= 365 && row.MinStayThrough is >= 1 and <= 365, "Minimum stays must be between 1 and 365 nights.");
                    Require(row.MaxStay is >= 0 and <= 365 && (row.MaxStay == 0 || row.MaxStay >= Math.Max(row.MinStayArrival, row.MinStayThrough)), "Maximum stay must be zero (unlimited) or at least the minimum stay, up to 365 nights.");
                    if (before == (row.Rate, row.MinStayArrival, row.MinStayThrough, row.MaxStay, row.StopSell, row.ClosedToArrival, row.ClosedToDeparture) && !input.Reapply) continue;
                    // Keep the user's selected patch fields rather than marking the whole daily snapshot dirty.
                    // Both minimum-stay fields remain in a requested pair even if only one needed correction.
                    Enqueue(db, propertyId, "restrictions", rate.Id, date, date.AddDays(1), AriFields.SelectedBy(change)); count++;
                }
            }
        }
        return count;
    }, ct);

    public Task<Reservation> SaveReservationAsync(Guid propertyId, Guid? reservationId, ReservationInput input, CancellationToken ct = default) => WriteAsync(async db =>
    {
        var property = await PropertyAsync(db, propertyId, ct);
        var guest = Name(input.GuestName);
        Require(input.Quantity is >= 1 and <= 1000, "Enter a valid number of rooms or units.");
        Require(input.Departure > input.Arrival && input.Departure.DayNumber - input.Arrival.DayNumber <= 500, "Departure must follow arrival; stays can be up to 500 nights.");
        ValidateDates(property, input.Arrival, input.Departure.AddDays(-1));
        var room = await db.RoomTypes.SingleOrDefaultAsync(x => x.Id == input.RoomTypeId && x.PropertyId == propertyId, ct) ?? throw new AppError("Choose a room from this property.");
        Require(await db.RatePlans.AnyAsync(x => x.Id == input.RatePlanId && x.RoomTypeId == room.Id, ct), "Choose a rate plan for this room type.");
        Reservation? reservation = null;
        if (reservationId.HasValue)
        {
            reservation = await db.Reservations.Include(x => x.Stays).SingleOrDefaultAsync(x => x.Id == reservationId && x.PropertyId == propertyId, ct) ?? throw new AppError("Reservation not found.", 404);
            Require(reservation.Source == "direct", "Change or cancel an OTA booking through its original booking channel.");
            Require(reservation.Status != "cancelled", "A cancelled reservation cannot be modified.");
        }
        var others = (await ActiveReservationsAsync(db, propertyId, ct)).Where(x => x.Id != reservationId).ToList();
        var inventory = await db.DailyInventories.Where(x => x.RoomTypeId == room.Id && x.Date >= input.Arrival && x.Date < input.Departure).ToListAsync(ct);
        for (var date = input.Arrival; date < input.Departure; date = date.AddDays(1))
        {
            var capacity = inventory.SingleOrDefault(x => x.Date == date)?.Capacity ?? room.Quantity;
            Require(capacity - Booked(others, room.Id, date) >= input.Quantity, $"Insufficient availability on {date:yyyy-MM-dd}.");
        }
        if (reservation == null) { reservation = new Reservation { PropertyId = propertyId }; db.Reservations.Add(reservation); }
        else { EnqueueReservation(db, reservation); db.ReservationStays.RemoveRange(reservation.Stays); reservation.Stays.Clear(); }
        reservation.GuestName = guest; reservation.Arrival = input.Arrival; reservation.Departure = input.Departure; reservation.UpdatedAt = DateTimeOffset.UtcNow;
        var stay = new ReservationStay { ReservationId = reservation.Id, RoomTypeId = room.Id, RatePlanId = input.RatePlanId, Quantity = input.Quantity, Arrival = input.Arrival, Departure = input.Departure };
        reservation.Stays.Add(stay);
        db.ReservationStays.Add(stay);
        EnqueueReservation(db, reservation);
        return reservation;
    }, ct);

    public Task<bool> CancelReservationAsync(Guid id, CancellationToken ct = default) => WriteAsync(async db =>
    {
        var reservation = await db.Reservations.Include(x => x.Stays).SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new AppError("Reservation not found.", 404);
        Require(reservation.Source == "direct", "Cancel an OTA booking through its original booking channel.");
        if (reservation.Status == "cancelled") return false;
        reservation.Status = "cancelled"; reservation.UpdatedAt = DateTimeOffset.UtcNow;
        EnqueueReservation(db, reservation);
        return true;
    }, ct);

    public Task<int> QueueFullSyncAsync(Guid propertyId, CancellationToken ct = default) => WriteAsync(async db =>
    {
        var property = await PropertyAsync(db, propertyId, ct);
        return await FullSyncAsync(db, property, ct);
    }, ct);

    public static async Task<int> FullSyncAsync(AppDb db, Property property, CancellationToken ct)
    {
        var rooms = await db.RoomTypes.Where(x => x.PropertyId == property.Id).ToListAsync(ct);
        var ids = rooms.Select(x => x.Id).ToList();
        var rates = await db.RatePlans.Where(x => ids.Contains(x.RoomTypeId)).ToListAsync(ct);
        Require(rooms.Count > 0 && rates.Count > 0, "Add room inventory and rate plans before a full sync.");
        var readiness = await SyncReadiness.CheckAsync(db, property, configured: true, ct);
        if (!readiness.Ready) throw new AppError("Full sync is not ready. " + string.Join(" ", readiness.Issues), 409);
        var today = Today(property);
        foreach (var room in rooms) Enqueue(db, property.Id, "availability", room.Id, today, today.AddDays(500));
        foreach (var rate in rates) Enqueue(db, property.Id, "restrictions", rate.Id, today, today.AddDays(500));
        return (rooms.Count + rates.Count) * 500;
    }

    public static void EnqueueReservation(AppDb db, Reservation reservation)
    {
        foreach (var stay in reservation.Stays)
            Enqueue(db, reservation.PropertyId, "availability", stay.RoomTypeId, stay.Arrival ?? reservation.Arrival, stay.Departure ?? reservation.Departure);
    }
    public static void Enqueue(AppDb db, Guid propertyId, string kind, Guid entityId, DateOnly from, DateOnly toExclusive,
        int changedFields = 0)
    {
        for (var date = from; date < toExclusive; date = date.AddDays(1))
            db.Outbox.Add(new OutboxChange { PropertyId = propertyId, Kind = kind, EntityId = entityId, Date = date, ChangedFields = changedFields });
    }
    public static int Booked(IEnumerable<Reservation> reservations, Guid roomId, DateOnly date) => reservations
        .Where(r => r.Status != "cancelled")
        .Sum(r => r.Stays.Where(s => s.RoomTypeId == roomId && (s.Arrival ?? r.Arrival) <= date && (s.Departure ?? r.Departure) > date).Sum(s => s.Quantity));
    public static Task<List<Reservation>> ActiveReservationsAsync(AppDb db, Guid propertyId, CancellationToken ct) => db.Reservations.Include(x => x.Stays).Where(x => x.PropertyId == propertyId && x.Status != "cancelled").ToListAsync(ct);
    public static async Task<Property> PropertyAsync(AppDb db, Guid id, CancellationToken ct) => await db.Properties.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new AppError("Property not found.", 404);
    public static void Require([DoesNotReturnIf(false)] bool condition, string error) { if (!condition) throw new AppError(error); }
    private static string Name(string? name) { Require(!string.IsNullOrWhiteSpace(name) && name.Trim().Length <= 120, "Enter a name of 1–120 characters."); return name!.Trim(); }
    private static void Price(decimal rate) => Require(rate is > 0 and <= 1000000 && decimal.Round(rate, 2) == rate, "Prices must be positive, up to 1,000,000, with at most two decimal places.");
    private static void ValidateDates(Property property, DateOnly from, DateOnly to)
    {
        var today = Today(property);
        Require(from >= today && to >= from && to.DayNumber - from.DayNumber < 500 && to <= today.AddDays(730), "Choose future dates, up to 500 days at a time, within the next two years.");
    }
}
