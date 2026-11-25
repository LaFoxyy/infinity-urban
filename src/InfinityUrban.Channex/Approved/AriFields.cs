namespace Stayline;

/// <summary>The restriction fields selected by an operational change. Zero in old outbox rows means a full snapshot.</summary>
public static class AriFields
{
    public const int Rate = 1;
    public const int MinStayArrival = 2;
    public const int MinStayThrough = 4;
    public const int MaxStay = 8;
    public const int StopSell = 16;
    public const int ClosedToArrival = 32;
    public const int ClosedToDeparture = 64;
    public const int AllRestrictions = Rate | MinStayArrival | MinStayThrough | MaxStay | StopSell | ClosedToArrival | ClosedToDeparture;

    public static int SelectedBy(CalendarChange change) =>
        (change.Rate.HasValue ? Rate : 0) |
        (change.MinStayArrival.HasValue ? MinStayArrival : 0) |
        (change.MinStayThrough.HasValue ? MinStayThrough : 0) |
        (change.MaxStay.HasValue ? MaxStay : 0) |
        (change.StopSell.HasValue ? StopSell : 0) |
        (change.ClosedToArrival.HasValue ? ClosedToArrival : 0) |
        (change.ClosedToDeparture.HasValue ? ClosedToDeparture : 0);
}
