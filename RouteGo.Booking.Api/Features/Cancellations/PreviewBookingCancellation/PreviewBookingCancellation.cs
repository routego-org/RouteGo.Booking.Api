namespace RouteGo.Booking.Api.Features.Cancellations.PreviewBookingCancellation;

/// <summary>The refund a customer would get if they cancelled a booking right now.</summary>
public sealed record CancellationPreview
{
    public required Guid BookingId { get; init; }

    /// <summary>The booking's total, whole rupees.</summary>
    public required long TotalAmount { get; init; }

    /// <summary>Always INR.</summary>
    public required string Currency { get; init; }

    /// <summary>The refund share for the time left, 0 to 100.</summary>
    public required int RefundPercent { get; init; }

    /// <summary>The total times the share, rounded down to the whole rupee.</summary>
    public required long RefundAmount { get; init; }

    public required DateTimeOffset DepartureAt { get; init; }

    /// <summary>The moment the share was worked out; cancelling later may give a lower one.</summary>
    public required DateTimeOffset CalculatedAt { get; init; }
}

public enum PreviewBookingCancellationOutcome
{
    Previewed,
    NotFound,
    NotTheCustomersBooking,
    BookingNotConfirmed,
    TripDeparted,
}

/// <summary>How the preview ended.</summary>
public sealed record PreviewBookingCancellationResult
{
    public required PreviewBookingCancellationOutcome Outcome { get; init; }

    /// <summary>The preview, when previewed.</summary>
    public CancellationPreview? Response { get; init; }

    /// <summary>The booking's status, when it is not confirmed.</summary>
    public BookingStatus BookingStatus { get; init; }

    /// <summary>When the trip departed, when it has.</summary>
    public DateTimeOffset DepartedAt { get; init; }
}

[ApiController]
[ApiExplorerSettings(GroupName = "Cancellations")]
[Route("bookings")]
public sealed class PreviewBookingCancellationController : ControllerBase
{
    private readonly PreviewBookingCancellationHandler _handler;

    public PreviewBookingCancellationController(
        IReaderConnectionFactory readerFactory, IBookingSettings settings, IClock clock)
    {
        _handler = new PreviewBookingCancellationHandler(readerFactory, settings, clock);
    }

    /// <summary>Show the refund the customer would get if they cancelled now (the booking's customer).</summary>
    /// <remarks>
    /// The refund share for the time left to departure and the refund amount, rounded down to the whole
    /// rupee. Changes nothing and promises nothing: cancelling works the refund out again. Only for a
    /// confirmed booking whose trip has not departed, and only for the booking's customer.
    /// </remarks>
    /// <param name="bookingId">The booking's identifier.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <response code="200">The refund if cancelled now.</response>
    /// <response code="401">No valid access token was presented (UNAUTHORIZED).</response>
    /// <response code="403">The caller is not the booking's customer (FORBIDDEN).</response>
    /// <response code="404">No booking has this identifier (NOT_FOUND).</response>
    /// <response code="409">The booking is not confirmed (BOOKING_NOT_CONFIRMED) or its trip has departed (TRIP_DEPARTED).</response>
    [Authorize(Policy = Policies.Customer)]
    [HttpGet("{bookingId}/cancellationPreview")]
    [ProducesResponseType(typeof(CancellationPreview), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> PreviewBookingCancellationAsync(Guid bookingId, CancellationToken cancellationToken)
    {
        Caller caller = CallerReader.Read(User);
        PreviewBookingCancellationResult result = await _handler.HandleAsync(bookingId, caller, cancellationToken);
        switch (result.Outcome)
        {
            case PreviewBookingCancellationOutcome.Previewed:
                return Ok(result.Response);
            case PreviewBookingCancellationOutcome.NotFound:
                return ProblemResult.NotFound(this, $"There is no booking {bookingId}.");
            case PreviewBookingCancellationOutcome.NotTheCustomersBooking:
                return ProblemResult.Forbidden(this, "Only the booking's customer can preview its cancellation.");
            case PreviewBookingCancellationOutcome.BookingNotConfirmed:
                return ProblemResult.BookingNotConfirmed(
                    this, $"Booking {bookingId} is not confirmed.", ToWireStatus(result.BookingStatus));
            case PreviewBookingCancellationOutcome.TripDeparted:
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
