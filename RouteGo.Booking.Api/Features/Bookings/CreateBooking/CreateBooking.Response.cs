namespace RouteGo.Booking.Api.Features.Bookings.CreateBooking;

/// <summary>The kind of bus, as the trip snapshot keeps it.</summary>
public enum BusType
{
    Seater,
    Sleeper,
    SemiSleeper,
}

/// <summary>One seat of the booking with the fare it was sold at and its passenger.</summary>
public sealed class BookingSeat
{
    public required string SeatNo { get; init; }

    public required string SeatClass { get; init; }

    /// <summary>The fare this seat was sold at, whole rupees.</summary>
    public required long Fare { get; init; }

    public required string PassengerName { get; init; }

    public required int PassengerAge { get; init; }

    public required PassengerGender PassengerGender { get; init; }
}

/// <summary>The trip the booking is on, as Booking's trip snapshot describes it.</summary>
public sealed class TripSummary
{
    public required Guid TripId { get; init; }

    public required string SourceCityName { get; init; }

    public required string DestinationCityName { get; init; }

    /// <summary>The service date in IST.</summary>
    public required DateOnly ServiceDate { get; init; }

    public required DateTimeOffset DepartureAt { get; init; }

    public required DateTimeOffset ArrivalAt { get; init; }

    public required string BusName { get; init; }

    public required BusType BusType { get; init; }

    public required string OperatorName { get; init; }
}

/// <summary>How a confirmed booking was cancelled; a booking that was just created never has one.</summary>
public sealed class BookingCancellation
{
    public required CancellationKind Kind { get; init; }

    public required string? Reason { get; init; }

    public required int RefundPercent { get; init; }

    public required long RefundAmount { get; init; }

    public required DateTimeOffset CancelledAt { get; init; }
}

/// <summary>The money coming back for a booking; a booking that was just created never has one.</summary>
public sealed class BookingRefund
{
    public required RefundReason Reason { get; init; }

    public required long Amount { get; init; }

    public required RefundStatus Status { get; init; }

    public required DateTimeOffset RequestedAt { get; init; }

    public required DateTimeOffset? CompletedAt { get; init; }
}

/// <summary>The booking: its status, trip, seats with passengers, money and outcome.</summary>
public sealed class Booking
{
    public required Guid BookingId { get; init; }

    public required BookingStatus Status { get; init; }

    public required TripSummary Trip { get; init; }

    public required string ContactName { get; init; }

    public required string ContactEmail { get; init; }

    public required IReadOnlyList<BookingSeat> Seats { get; init; }

    /// <summary>Whole rupees.</summary>
    public required long TotalAmount { get; init; }

    /// <summary>Always INR.</summary>
    public required string Currency { get; init; }

    /// <summary>When the hold ends unless paid; given only while the booking is unpaid.</summary>
    public required DateTimeOffset? HoldExpiresAt { get; init; }

    /// <summary>The payment page; given only while the booking is paymentPending and before the hold expiry.</summary>
    public required string? CheckoutUrl { get; init; }

    public required FailureReason? FailureReason { get; init; }

    public required string? PaymentFailureDetail { get; init; }

    public required int SeatChangeCount { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset? ConfirmedAt { get; init; }

    public required BookingCancellation? Cancellation { get; init; }

    public required BookingRefund? Refund { get; init; }
}

/// <summary>How creating the booking ended.</summary>
public enum CreateBookingOutcome
{
    Created,
    TripNotFound,
    TripCancelled,
    BookingClosed,
    UnknownSeats,
    PriceChanged,
    UnpaidBookingExists,
    SeatsUnavailable,
    UpstreamUnavailable,
    UpstreamTimeout,
    StateChanged,
}

/// <summary>The outcome of creating a booking with the facts that outcome names.</summary>
public sealed class CreateBookingResult
{
    /// <summary>Seconds the customer is told to wait before trying again after a timeout.</summary>
    public const int RetryAfterSeconds = 5;

    public required CreateBookingOutcome Outcome { get; init; }

    /// <summary>The created booking, when created.</summary>
    public Booking? Response { get; init; }

    /// <summary>The trip's departure, when booking has closed.</summary>
    public DateTimeOffset DepartureAt { get; init; }

    /// <summary>The booking cutoff in force, when booking has closed.</summary>
    public int CutoffMinutes { get; init; }

    /// <summary>The unknown seats or the unavailable seats, for those outcomes.</summary>
    public IReadOnlyList<string> SeatNos { get; init; } = Array.Empty<string>();

    /// <summary>The current total, when the price changed.</summary>
    public long CurrentTotal { get; init; }

    /// <summary>Each seat's current fare, when the price changed.</summary>
    public IReadOnlyList<SeatFareProblemItem> SeatFares { get; init; } = Array.Empty<SeatFareProblemItem>();

    /// <summary>The customer's existing unpaid booking, when there is one.</summary>
    public Guid ExistingBookingId { get; init; }

    /// <summary>When that booking's hold ends, when it has one.</summary>
    public DateTimeOffset? HoldExpiresAt { get; init; }

    /// <summary>Whether trying again may succeed, for an unavailable upstream.</summary>
    public bool IsRetryable { get; init; }

    /// <summary>A refusal that names nothing beyond its outcome.</summary>
    public static CreateBookingResult Refused(CreateBookingOutcome outcome)
    {
        return new CreateBookingResult { Outcome = outcome };
    }
}
