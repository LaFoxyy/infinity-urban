using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Stayline;

namespace Stayline.Checks;

internal static class DomainChecks
{
    public static async Task CalendarWritesSavedStateAndOutboxTogether()
    {
        await using var factory = new TestDb();
        var seed = await SeededProperty.CreateAsync(factory);
        var service = new PmsService(factory, new MutationLock());
        var changed = await service.SaveCalendarAsync(seed.PropertyId, new([
            new(null, seed.RatePlanId, seed.Start, seed.Start.AddDays(2), Rate: 157.25m, MinStayArrival: 3, MaxStay: 7),
            new(seed.RoomTypeId, null, seed.Start, seed.Start.AddDays(1), Capacity: 3)
        ]));
        Check.Equal(5, changed, "Calendar must count all changed date/entity pairs");
        await using var db = factory.CreateDbContext();
        var rates = await db.DailyRates.OrderBy(x => x.Date).ToListAsync();
        Check.Equal(3, rates.Count, "Calendar ranges include both selected endpoints");
        Check.That(rates.All(x => x.Rate == 157.25m && x.MinStayArrival == 3 && x.MaxStay == 7),
            "Saved prices and restrictions must reflect user input");
        Check.Equal(2, await db.DailyInventories.CountAsync(x => x.Capacity == 3), "Inventory capacity changes must be persisted");
        Check.Equal(5, await db.Outbox.CountAsync(x => x.ProcessedAt == null), "Every changed date must have durable pending work");
    }

    public static async Task InvalidSecondChangeRollsBackWholeCalendarSave()
    {
        await using var factory = new TestDb();
        var seed = await SeededProperty.CreateAsync(factory);
        var service = new PmsService(factory, new MutationLock());
        await Check.RejectsAsync(() => service.SaveCalendarAsync(seed.PropertyId, new([
            new(null, seed.RatePlanId, seed.Start, seed.Start, Rate: 184.75m),
            new(seed.RoomTypeId, null, seed.Start, seed.Start, Capacity: 6)
        ])), "Inventory above room capacity must be rejected");
        await using var db = factory.CreateDbContext();
        Check.Equal(0, await db.DailyRates.CountAsync(), "Earlier calendar edits must roll back when a later edit is invalid");
        Check.Equal(0, await db.DailyInventories.CountAsync(), "Invalid capacity must not be saved");
        Check.Equal(0, await db.Outbox.CountAsync(), "A rolled-back save must not leave phantom work in the outbox");
    }

    public static async Task OverlappingCalendarEditsKeepOneCurrentRow()
    {
        await using var factory = new TestDb();
        var seed = await SeededProperty.CreateAsync(factory);
        var service = new PmsService(factory, new MutationLock());
        await service.SaveCalendarAsync(seed.PropertyId, new([
            new(null, seed.RatePlanId, seed.Start, seed.Start.AddDays(1), Rate: 145.15m),
            new(null, seed.RatePlanId, seed.Start.AddDays(1), seed.Start.AddDays(2), Rate: 165.25m)
        ]));
        await using var db = factory.CreateDbContext();
        var rows = await db.DailyRates.OrderBy(x => x.Date).ToListAsync();
        Check.Equal(3, rows.Count, "Overlapping edits must not produce duplicate daily rate records");
        Check.Equal(145.15m, rows[0].Rate, "The nonoverlapping first date must keep its price");
        Check.Equal(165.25m, rows[1].Rate, "The last edit must win on the overlapping date");
        Check.Equal(165.25m, rows[2].Rate, "The last selected date must be updated");
    }

    public static async Task ReservationLifecycleUsesCheckoutExclusiveDates()
    {
        await using var factory = new TestDb();
        var seed = await SeededProperty.CreateAsync(factory);
        var service = new PmsService(factory, new MutationLock());
        var reservation = await service.SaveReservationAsync(seed.PropertyId, null,
            new("Alex Rivera", seed.RoomTypeId, seed.RatePlanId, 2, seed.Start, seed.Start.AddDays(3)));
        await using (var db = factory.CreateDbContext())
        {
            var bookings = await db.Reservations.Include(x => x.Stays).ToListAsync();
            Check.Equal(2, PmsService.Booked(bookings, seed.RoomTypeId, seed.Start), "Arrival night must consume inventory");
            Check.Equal(2, PmsService.Booked(bookings, seed.RoomTypeId, seed.Start.AddDays(2)), "Last occupied night must consume inventory");
            Check.Equal(0, PmsService.Booked(bookings, seed.RoomTypeId, seed.Start.AddDays(3)), "Checkout date must remain available");
            Check.Equal(3, await db.Outbox.CountAsync(), "A three-night reservation must queue three inventory dates");
            Check.That(!await db.Outbox.AnyAsync(x => x.Date == seed.Start.AddDays(3)), "The checkout date must not be queued as an occupied date");
        }
        await service.SaveReservationAsync(seed.PropertyId, reservation.Id,
            new("Alex Rivera", seed.RoomTypeId, seed.RatePlanId, 1, seed.Start.AddDays(1), seed.Start.AddDays(4)));
        await using (var db = factory.CreateDbContext())
        {
            var bookings = await db.Reservations.Include(x => x.Stays).ToListAsync();
            Check.Equal(0, PmsService.Booked(bookings, seed.RoomTypeId, seed.Start), "Moving arrival must release the old arrival night");
            Check.Equal(1, PmsService.Booked(bookings, seed.RoomTypeId, seed.Start.AddDays(3)), "Extending the stay must consume its new night");
            Check.Equal(9, await db.Outbox.CountAsync(), "Modification must dirty both the old and new date ranges");
            Check.Equal(1, await db.ReservationStays.CountAsync(), "Modification must replace the old room allocation");
        }
        Check.That(await service.CancelReservationAsync(reservation.Id), "First cancellation must change the reservation");
        Check.That(!await service.CancelReservationAsync(reservation.Id), "Repeated cancellation must be idempotent");
        await using var final = factory.CreateDbContext();
        var cancelled = await final.Reservations.Include(x => x.Stays).ToListAsync();
        Check.Equal(0, PmsService.Booked(cancelled, seed.RoomTypeId, seed.Start.AddDays(2)), "Cancellation must release inventory");
        Check.Equal(12, await final.Outbox.CountAsync(), "Only the first cancellation must queue the occupied dates");
    }

    public static async Task OverbookingAndCapacityReductionAreRejectedAtomically()
    {
        await using var factory = new TestDb();
        var seed = await SeededProperty.CreateAsync(factory);
        var service = new PmsService(factory, new MutationLock());
        await service.SaveReservationAsync(seed.PropertyId, null,
            new("Guest One", seed.RoomTypeId, seed.RatePlanId, 4, seed.Start, seed.Start.AddDays(2)));
        await Check.RejectsAsync(() => service.SaveReservationAsync(seed.PropertyId, null,
            new("Guest Two", seed.RoomTypeId, seed.RatePlanId, 2, seed.Start, seed.Start.AddDays(2))), "Overbooking must be rejected");
        await Check.RejectsAsync(() => service.SaveCalendarAsync(seed.PropertyId, new([
            new(seed.RoomTypeId, null, seed.Start, seed.Start.AddDays(1), Capacity: 3)
        ])), "Capacity cannot be reduced below existing reservations");
        await using var db = factory.CreateDbContext();
        Check.Equal(1, await db.Reservations.CountAsync(), "Rejected overbooking must not create a booking");
        Check.Equal(0, await db.DailyInventories.CountAsync(), "Rejected reduction must not change capacity");
        Check.Equal(2, await db.Outbox.CountAsync(), "Rejected operations must not append synchronization work");
    }

    public static async Task RentalUnitsHaveSingleInventory()
    {
        await using var factory = new TestDb();
        var service = new PmsService(factory, new MutationLock());
        var rental = await service.CreatePropertyAsync(new("Ocean Apartment", "rental", "BRL", "America/Sao_Paulo"));
        await Check.RejectsAsync(() => service.CreateRoomAsync(rental.Id, new("Apartment A", 2, 4)),
            "A rental unit must have inventory one");
        var room = await service.CreateRoomAsync(rental.Id, new("Apartment A", 1, 4));
        Check.Equal(1, room.Quantity, "A rental must create a sellable single unit");
        await using var db = factory.CreateDbContext();
        Check.Equal(1, await db.RoomTypes.CountAsync(), "Rejected creation must not leave an extra room record");
        Check.Equal(500, await db.Outbox.CountAsync(), "New room creation must queue its initial 500-day inventory");
    }
}
