namespace RouteGo.Booking.Api.Features.Cancellations.CancelBooking;

/// <summary>How a booking ended: who cancelled it, the refund and where the refund stands.</summary>
public sealed record Cancellation
{
    public required Guid BookingId { get; init; }

    /// <summary>Always customerCancelled for this operation.</summary>
    public required CancellationKind Kind { get; init; }

    /// <summary>Null for a customer cancellation.</summary>
    public required string? Reason { get; init; }

    public required int RefundPercent { get; init; }

    /// <summary>Whole rupees, rounded down; 0 when no refund is due.</summary>
    public required long RefundAmount { get; init; }

    /// <summary>Always INR.</summary>
    public required string Currency { get; init; }

    public required DateTimeOffset CancelledAt { get; init; }

    /// <summary>The refund's status; null when the refund amount is 0.</summary>
    public required RefundStatus? RefundStatus { get; init; }
}

public enum CancelBookingOutcome
{
    Cancelled,
    AlreadyCancelled,
    NotFound,
    NotTheCustomersBooking,
    BookingNotConfirmed,
    TripDeparted,
}

/// <summary>How the cancellation ended.</summary>
public sealed record CancelBookingResult
{
    public required CancelBookingOutcome Outcome { get; init; }

    /// <summary>The cancellation, when the booking is cancelled now or was before.</summary>
    public Cancellation? Response { get; init; }

    /// <summary>The booking's status, when it is not confirmed.</summary>
    public BookingStatus BookingStatus { get; init; }

    /// <summary>When the trip departed, when it has.</summary>
    public DateTimeOffset DepartedAt { get; init; }
}

/// <summary>Which booking to cancel, for whom, and under which correlation ID its events are recorded.</summary>
/// <param name="BookingId">The booking.</param>
/// <param name="Caller">The calling customer; only their own booking can be cancelled.</param>
/// <param name="CorrelationId">The request's correlation ID, copied into the events.</param>
public sealed record CancelBookingTarget(Guid BookingId, Caller Caller, string CorrelationId);

[ApiController]
[ApiExplorerSettings(GroupName = "Cancellations")]
[Route("bookings")]
public sealed class CancelBookingController : ControllerBase
{
    private readonly CancelBookingHandler _handler;

    public CancelBookingController(
        IWriterConnectionFactory writerFactory, IBookingSettings settings, IClock clock, ILogger<CancelBookingHandler> logger)
    {
        _handler = new CancelBookingHandler(writerFactory, settings, clock, logger);
    }

    /// <summary>Cancel a confirmed booking with the refund its tier gives (the booking's customer).</summary>
    /// <remarks>
    /// Cancels the whole booking before departure: the refund share for the time left now, the amount
    /// rounded down; the seats are freed and the refund is asked of Payment when it is more than 0.
    /// Cancelling a booking its customer already cancelled returns that cancellation unchanged.
    /// </remarks>
    /// <param name="bookingId">The booking's identifier.</param>
    /// <param name="cancellationToken">Cancels the request (not the booking).</param>
    /// <response code="200">The booking is cancelled; the cancellation is returned.</response>
    /// <response code="401">No valid access token was presented (UNAUTHORIZED).</response>
    /// <response code="403">The caller is not the booking's customer (FORBIDDEN).</response>
    /// <response code="404">No booking has this identifier (NOT_FOUND).</response>
    /// <response code="409">The booking is not confirmed (BOOKING_NOT_CONFIRMED) or its trip has departed (TRIP_DEPARTED).</response>
    [Authorize(Policy = Policies.Customer)]
    [HttpPost("{bookingId}/cancel")]
    [ProducesResponseType(typeof(Cancellation), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CancelBookingAsync(Guid bookingId, CancellationToken cancellationToken)
    {
        Caller caller = CallerReader.Read(User);
        string correlationId = CorrelationIdMiddleware.GetCorrelationId(HttpContext);
        var target = new CancelBookingTarget(bookingId, caller, correlationId);
        CancelBookingResult result = await _handler.HandleAsync(target, cancellationToken);
        switch (result.Outcome)
        {
            case CancelBookingOutcome.Cancelled:
            case CancelBookingOutcome.AlreadyCancelled:
                return Ok(result.Response);
            case CancelBookingOutcome.NotFound:
                return ProblemResult.NotFound(this, $"There is no booking {bookingId}.");
            case CancelBookingOutcome.NotTheCustomersBooking:
                return ProblemResult.Forbidden(this, "Only the booking's customer can cancel it this way.");
            case CancelBookingOutcome.BookingNotConfirmed:
                return ProblemResult.BookingNotConfirmed(
                    this, $"Booking {bookingId} is not confirmed.", ToWireStatus(result.BookingStatus));
            case CancelBookingOutcome.TripDeparted:
                return ProblemResult.TripDeparted(this, $"The trip of booking {bookingId} has already departed.", result.DepartedAt);
            default:
                throw new InvalidOperationException($"Unhandled outcome '{result.Outcome}'.");
        }
    }

    // The wire spells a status in camelCase, as the contract's examples do.
    private static string ToWireStatus(BookingStatus status)
    {
        return JsonNamingPolicy.CamelCase.ConvertName(status.ToString());
    }
}
