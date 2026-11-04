namespace RouteGo.Booking.Api.Tests.Support;

/// <summary>A booking to put in the database before a test, on a trip seeded with <see cref="TripSeed"/>.</summary>
public sealed class BookingSeedSpec
{
    /// <summary>The booking; a new one by default.</summary>
    public Guid BookingId { get; init; } = Guid.NewGuid();

    /// <summary>The customer's account identifier (the token's subject).</summary>
    public string CustomerId { get; init; } = "C-1";

    /// <summary>The booking contact's name.</summary>
    public string ContactName { get; init; } = "Priya Shah";

    /// <summary>The booking contact's email.</summary>
    public string ContactEmail { get; init; } = "priya@example.com";

    /// <summary>The trip, which must already be seeded.</summary>
    public required Guid TripId { get; init; }

    /// <summary>The booking's status; PaymentPending by default.</summary>
    public BookingStatus Status { get; init; } = BookingStatus.PaymentPending;

    /// <summary>The seats with their passengers; 3A and 3B at 800 by default.</summary>
    public IReadOnlyList<SeedPassenger> Passengers { get; init; } = new List<SeedPassenger>
    {
        new SeedPassenger("3A", "Priya Shah", 800),
        new SeedPassenger("3B", "Ravi Shah", 800),
    };

    /// <summary>When the hold ends; set for an unpaid booking.</summary>
    public DateTimeOffset? HoldExpiresAt { get; init; }

    /// <summary>Payment's identifier for the booking's payment.</summary>
    public Guid? PaymentId { get; init; }

    /// <summary>The payment page address.</summary>
    public string? CheckoutUrl { get; init; }

    /// <summary>Why the booking failed; required exactly when the status is Failed.</summary>
    public FailureReason? FailureReason { get; init; }

    /// <summary>When the booking was confirmed; required for a Confirmed or Cancelled booking.</summary>
    public DateTimeOffset? ConfirmedAt { get; init; }

    /// <summary>How many seat changes the booking has had.</summary>
    public int SeatChangeCount { get; init; }
}
