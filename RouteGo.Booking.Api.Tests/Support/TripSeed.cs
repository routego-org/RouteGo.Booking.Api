namespace RouteGo.Booking.Api.Tests.Support;

/// <summary>A trip snapshot to put in the database before a test: operator, route, times and seats.</summary>
public sealed class TripSeed
{
    /// <summary>The trip; a new one by default.</summary>
    public Guid TripId { get; init; } = Guid.NewGuid();

    /// <summary>The operator, as the operatorId claim of the operator's token spells it.</summary>
    public string OperatorId { get; init; } = "OP-1";

    /// <summary>The operator's company name.</summary>
    public string OperatorName { get; init; } = "Sharma Travels";

    /// <summary>The operator's email.</summary>
    public string OperatorEmail { get; init; } = "ops@sharma.example";

    /// <summary>The source city name.</summary>
    public string Source { get; init; } = "Mumbai";

    /// <summary>The destination city name.</summary>
    public string Destination { get; init; } = "Pune";

    /// <summary>When the trip departs; 2099 by default, so it is never departed or closed.</summary>
    public DateTimeOffset DepartureAt { get; init; } = new DateTimeOffset(2099, 6, 1, 15, 30, 0, TimeSpan.Zero);

    /// <summary>Whether Fleet cancelled the trip.</summary>
    public bool IsCancelled { get; init; }

    /// <summary>The seats; 3A and 3B Sleeper at 800 and 4C Seater at 600 by default.</summary>
    public IReadOnlyList<SeedSeat> Seats { get; init; } = new List<SeedSeat>
    {
        new SeedSeat("3A", "Sleeper", 800),
        new SeedSeat("3B", "Sleeper", 800),
        new SeedSeat("4C", "Seater", 600),
        new SeedSeat("5C", "Seater", 600),
    };
}
