namespace RouteGo.Booking.Api.Features.Bookings.ReleaseBooking;

[ApiController]
[ApiExplorerSettings(GroupName = "Bookings")]
[Route("bookings")]
public sealed class ReleaseBookingController : ControllerBase
{
    private readonly ReleaseBookingHandler _handler;

    public ReleaseBookingController(IWriterConnectionFactory writerFactory, ISeatInventoryClient seatInventory, IClock clock)
    {
        _handler = new ReleaseBookingHandler(writerFactory, seatInventory, clock);
    }

    /// <summary>Abandon an unpaid booking and free its seats at once (the booking's customer only).</summary>
    /// <remarks>
    /// Ends a seatsHeld or paymentPending booking as failed with failure reason abandoned and releases
    /// its hold through Seat Inventory at once. If Seat Inventory cannot be reached the booking still
    /// ends and its seats follow from the BookingFailed event. Repeating it on a booking already
    /// abandoned returns it unchanged. A payment that still succeeds is refunded in full.
    /// </remarks>
    /// <param name="bookingId">The booking's identifier.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <response code="200">The booking is failed with failure reason abandoned.</response>
    /// <response code="401">No valid access token was presented (UNAUTHORIZED).</response>
    /// <response code="403">Not the booking's customer (FORBIDDEN).</response>
    /// <response code="404">No booking with this identifier exists (NOT_FOUND).</response>
    /// <response code="409">The booking is no longer waiting for payment (BOOKING_NOT_UNPAID).</response>
    [Authorize(Policy = Policies.Customer)]
    [HttpPost("{bookingId}/release")]
    [ProducesResponseType(typeof(Booking), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ReleaseBookingAsync(Guid bookingId, CancellationToken cancellationToken)
    {
        Caller caller = CallerReader.Read(User);
        string correlationId = CorrelationIdMiddleware.GetCorrelationId(HttpContext);
        var target = new ReleaseTarget(bookingId, caller, correlationId);
        ReleaseBookingResult result = await _handler.HandleAsync(target, cancellationToken);
        switch (result.Outcome)
        {
            case ReleaseBookingOutcome.Released:
                return Ok(result.Response);
            case ReleaseBookingOutcome.NotFound:
                return ProblemResult.NotFound(this, $"There is no booking {bookingId}.");
            case ReleaseBookingOutcome.Forbidden:
                return ProblemResult.Forbidden(this, "This booking belongs to another customer.");
            case ReleaseBookingOutcome.NotUnpaid:
                string status = JsonNamingPolicy.CamelCase.ConvertName(result.Status.ToString());
                return ProblemResult.BookingNotUnpaid(this, $"Booking {bookingId} is no longer waiting for payment.", status);
            default:
                throw new InvalidOperationException($"Unhandled outcome '{result.Outcome}'.");
        }
    }
}
