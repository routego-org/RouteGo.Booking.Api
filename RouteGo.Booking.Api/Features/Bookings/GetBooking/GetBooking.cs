namespace RouteGo.Booking.Api.Features.Bookings.GetBooking;

/// <summary>The kind of bus a trip runs.</summary>
public enum BusType
{
    Seater,
    Sleeper,
    SemiSleeper,
}

/// <summary>The trip a booking is for, as the booking's trip snapshot describes it.</summary>
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

/// <summary>One seat of the booking with its passenger.</summary>
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

/// <summary>How and when a confirmed booking was cancelled.</summary>
public sealed class BookingCancellation
{
    public required CancellationKind Kind { get; init; }

    /// <summary>The admin's or Fleet's reason; null for a customer cancellation.</summary>
    public required string? Reason { get; init; }

    public required int RefundPercent { get; init; }

    /// <summary>Whole rupees, rounded down.</summary>
    public required long RefundAmount { get; init; }

    public required DateTimeOffset CancelledAt { get; init; }
}

/// <summary>The money coming back to the customer for the booking.</summary>
public sealed class BookingRefund
{
    public required RefundReason Reason { get; init; }

    /// <summary>Whole rupees.</summary>
    public required long Amount { get; init; }

    public required RefundStatus Status { get; init; }

    public required DateTimeOffset RequestedAt { get; init; }

    public required DateTimeOffset? CompletedAt { get; init; }
}

/// <summary>The full picture of one booking.</summary>
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

    /// <summary>The payment page; given only while the booking is paymentPending and the hold has not run out.</summary>
    public required string? CheckoutUrl { get; init; }

    /// <summary>Why the booking failed; given only for a failed booking.</summary>
    public required FailureReason? FailureReason { get; init; }

    /// <summary>Payment's reason for a declined payment.</summary>
    public required string? PaymentFailureDetail { get; init; }

    public required int SeatChangeCount { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset? ConfirmedAt { get; init; }

    /// <summary>Given for a cancelled booking.</summary>
    public required BookingCancellation? Cancellation { get; init; }

    /// <summary>Given whenever the booking has a refund.</summary>
    public required BookingRefund? Refund { get; init; }
}

public enum GetBookingOutcome
{
    Found,
    NotFound,
    Forbidden,
}

/// <summary>How the read ended, with the booking when it was found.</summary>
/// <param name="Outcome">How the read ended.</param>
/// <param name="Booking">The booking when found.</param>
public sealed record GetBookingResult(GetBookingOutcome Outcome, Booking? Booking = null);

[ApiController]
[ApiExplorerSettings(GroupName = "Bookings")]
[Route("bookings")]
public sealed class GetBookingController : ControllerBase
{
    private readonly GetBookingHandler _handler;

    public GetBookingController(IReaderConnectionFactory readerFactory, IClock clock)
    {
        _handler = new GetBookingHandler(readerFactory, clock);
    }

    /// <summary>Get one booking with its status, seats, passengers, money and outcome (the booking's customer, Admin).</summary>
    /// <remarks>
    /// The customer who made the booking and any admin may read it. Another customer is refused, and
    /// an operator is refused by role; an unknown booking is not found for everyone. Reading changes
    /// nothing.
    /// </remarks>
    /// <param name="bookingId">The booking's identifier.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <response code="200">The booking.</response>
    /// <response code="401">No valid access token was presented (UNAUTHORIZED).</response>
    /// <response code="403">The caller is not the booking's customer or an admin (FORBIDDEN).</response>
    /// <response code="404">No booking has this identifier (NOT_FOUND).</response>
    [Authorize(Policy = Policies.CustomerOrAdmin)]
    [HttpGet("{bookingId}")]
    [ProducesResponseType(typeof(Booking), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetBookingAsync(Guid bookingId, CancellationToken cancellationToken)
    {
        Caller caller = CallerReader.Read(User);
        GetBookingResult result = await _handler.HandleAsync(bookingId, caller, cancellationToken);
        switch (result.Outcome)
        {
            case GetBookingOutcome.Found:
                return Ok(result.Booking);
            case GetBookingOutcome.Forbidden:
                return ProblemResult.Forbidden(this, $"Booking {bookingId} is not yours.");
            case GetBookingOutcome.NotFound:
                return ProblemResult.NotFound(this, $"There is no booking {bookingId}.");
            default:
                throw new InvalidOperationException($"Unhandled outcome '{result.Outcome}'.");
        }
    }
}
