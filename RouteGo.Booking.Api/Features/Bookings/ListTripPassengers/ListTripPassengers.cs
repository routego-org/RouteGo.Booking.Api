namespace RouteGo.Booking.Api.Features.Bookings.ListTripPassengers;

/// <summary>The kind of bus a trip runs.</summary>
public enum BusType
{
    Seater,
    Sleeper,
    SemiSleeper,
}

/// <summary>The trip, as Booking's trip snapshot describes it.</summary>
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

/// <summary>One booked seat and who sits in it. Never the booking contact.</summary>
public sealed class TripPassenger
{
    public required string SeatNo { get; init; }

    public required string SeatClass { get; init; }

    public required Guid BookingId { get; init; }

    public required string PassengerName { get; init; }

    public required int PassengerAge { get; init; }

    public required PassengerGender PassengerGender { get; init; }
}

/// <summary>The passengers of the confirmed bookings on one trip.</summary>
public sealed class TripPassengerList
{
    public required TripSummary Trip { get; init; }

    /// <summary>The number of seats of confirmed bookings on the trip.</summary>
    public required int SeatsBooked { get; init; }

    /// <summary>One entry per seat, ordered by seat label.</summary>
    public required IReadOnlyList<TripPassenger> Passengers { get; init; }
}

public enum ListTripPassengersOutcome
{
    Listed,
    NotFound,
    Forbidden,
}

/// <summary>How the listing ended, with the list when it was listed.</summary>
/// <param name="Outcome">How the listing ended.</param>
/// <param name="Response">The passenger list when listed.</param>
public sealed record ListTripPassengersResult(ListTripPassengersOutcome Outcome, TripPassengerList? Response = null);

[ApiController]
[ApiExplorerSettings(GroupName = "Bookings")]
[Route("trips")]
public sealed class ListTripPassengersController : ControllerBase
{
    private readonly ListTripPassengersHandler _handler;

    public ListTripPassengersController(IReaderConnectionFactory readerFactory)
    {
        _handler = new ListTripPassengersHandler(readerFactory);
    }

    /// <summary>List the passengers of confirmed bookings on one of the operator's own trips (Operator).</summary>
    /// <remarks>
    /// Every seat of every confirmed booking on the trip with its seat class, booking ID and passenger
    /// name, age and gender, ordered by seat label. Never the booking contact, and never a booking that
    /// is not confirmed. The trip must belong to the calling operator.
    /// </remarks>
    /// <param name="tripId">The trip's identifier.</param>
    /// <param name="cancellationToken">Cancels the listing.</param>
    /// <response code="200">The trip's passenger list, possibly empty.</response>
    /// <response code="401">No valid access token was presented (UNAUTHORIZED).</response>
    /// <response code="403">The caller is not this trip's operator (FORBIDDEN).</response>
    /// <response code="404">Booking has no trip snapshot for this identifier (NOT_FOUND).</response>
    [Authorize(Policy = Policies.Operator)]
    [HttpGet("{tripId}/passengers")]
    [ProducesResponseType(typeof(TripPassengerList), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ListTripPassengersAsync(Guid tripId, CancellationToken cancellationToken)
    {
        Caller caller = CallerReader.Read(User);
        ListTripPassengersResult result = await _handler.HandleAsync(tripId, caller, cancellationToken);
        switch (result.Outcome)
        {
            case ListTripPassengersOutcome.Listed:
                return Ok(result.Response);
            case ListTripPassengersOutcome.Forbidden:
                return ProblemResult.Forbidden(this, $"Trip {tripId} is not yours.");
            case ListTripPassengersOutcome.NotFound:
                return ProblemResult.NotFound(this, $"There is no trip {tripId}.");
            default:
                throw new InvalidOperationException($"Unhandled outcome '{result.Outcome}'.");
        }
    }
}
