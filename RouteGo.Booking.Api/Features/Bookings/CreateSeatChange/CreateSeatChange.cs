namespace RouteGo.Booking.Api.Features.Bookings.CreateSeatChange;

/// <summary>One passenger's move from one seat to another.</summary>
public sealed class SeatMoveRequest
{
    /// <summary>One of the booking's seats.</summary>
    public string? FromSeatNo { get; init; }

    /// <summary>A free seat of the same class on the same trip.</summary>
    public string? ToSeatNo { get; init; }
}

/// <summary>The moves a customer asks for.</summary>
public sealed class CreateSeatChangeRequest
{
    /// <summary>1 to 6 moves; no seat named twice; no move from a seat to itself.</summary>
    public List<SeatMoveRequest?>? Changes { get; init; }
}

/// <summary>One seat of the booking with its passenger.</summary>
public sealed record BookingSeat
{
    public required string SeatNo { get; init; }

    public required string SeatClass { get; init; }

    /// <summary>The fare the seat was sold at, whole rupees.</summary>
    public required long Fare { get; init; }

    public required string PassengerName { get; init; }

    public required int PassengerAge { get; init; }

    public required PassengerGender PassengerGender { get; init; }
}

/// <summary>The booking's seats after a seat change.</summary>
public sealed record SeatChange
{
    public required Guid BookingId { get; init; }

    /// <summary>The moves this request made; empty when every move was already done.</summary>
    public required IReadOnlyList<SeatMove> Changes { get; init; }

    /// <summary>How many seat changes the booking has had.</summary>
    public required int SeatChangeCount { get; init; }

    /// <summary>The booking's seats after the change, by seat label.</summary>
    public required IReadOnlyList<BookingSeat> Seats { get; init; }
}

public enum CreateSeatChangeOutcome
{
    Changed,
    AlreadyDone,
    NotFound,
    NotTheCustomersBooking,
    BookingNotConfirmed,
    TripCancelled,
    BookingClosed,
    SeatChangeLimitReached,
    SeatsNotInBooking,
    UnknownSeats,
    SeatClassMismatch,
    SeatsUnavailable,
    UpstreamUnavailable,
    UpstreamTimeout,
    BookingChangedMeanwhile,
}

/// <summary>How the seat change ended.</summary>
public sealed record CreateSeatChangeResult
{
    public required CreateSeatChangeOutcome Outcome { get; init; }

    /// <summary>The booking's seats, when the change was made or was already done.</summary>
    public SeatChange? Response { get; init; }

    /// <summary>The booking's status, when it is not confirmed.</summary>
    public BookingStatus BookingStatus { get; init; }

    /// <summary>The departure, when changes have closed.</summary>
    public DateTimeOffset DepartureAt { get; init; }

    /// <summary>The cutoff in minutes, when changes have closed.</summary>
    public int CutoffMinutes { get; init; }

    /// <summary>The most seat changes a booking may have, when the limit is reached.</summary>
    public int MaxSeatChanges { get; init; }

    /// <summary>The seats a refusal names: not the booking's, unknown or not free.</summary>
    public IReadOnlyList<string> SeatNos { get; init; } = Array.Empty<string>();

    /// <summary>The moves a class mismatch names.</summary>
    public IReadOnlyList<SeatMove> Moves { get; init; } = Array.Empty<SeatMove>();

    /// <summary>Whether trying again later may succeed, for an unusable Seat Inventory.</summary>
    public bool IsRetryable { get; init; }
}

/// <summary>Which booking's seats to change, for whom, and under which correlation ID the change is recorded.</summary>
/// <param name="BookingId">The booking.</param>
/// <param name="Caller">The calling customer; only their own booking can change.</param>
/// <param name="CorrelationId">The request's correlation ID, copied into the event and the Seat Inventory call.</param>
public sealed record SeatChangeTarget(Guid BookingId, Caller Caller, string CorrelationId);

public sealed class CreateSeatChangeValidator : AbstractValidator<CreateSeatChangeRequest>
{
    public CreateSeatChangeValidator()
    {
        RuleFor(x => x.Changes).NotNull().WithMessage("Give the seats to move.");
        RuleFor(x => x.Changes).Must(HasAcceptableCount)
            .When(x => x.Changes != null)
            .WithMessage($"Give 1 to {BookingRules.MaxSeatsCeiling} moves.");
        RuleFor(x => x.Changes).Must(NamesNoSeatTwice)
            .When(x => x.Changes != null)
            .WithMessage("A seat may be named only once as a seat to leave and once as a seat to move to.");
        RuleForEach(x => x.Changes).NotNull().WithMessage("A move needs a seat to leave and a seat to move to.");
        RuleForEach(x => x.Changes).ChildRules(move =>
        {
            move.RuleFor(m => m!.FromSeatNo).NotEmpty().WithMessage("Name the seat to leave.");
            move.RuleFor(m => m!.ToSeatNo).NotEmpty().WithMessage("Name the seat to move to.");
            move.RuleFor(m => m!.ToSeatNo).Must((m, toSeatNo) => MovesToAnotherSeat(m, toSeatNo)).WithMessage("A move must go to a different seat.");
        }).When(x => x.Changes != null);
    }

    private static bool HasAcceptableCount(List<SeatMoveRequest?>? changes)
    {
        return changes!.Count >= 1 && changes.Count <= BookingRules.MaxSeatsCeiling;
    }

    private static bool NamesNoSeatTwice(List<SeatMoveRequest?>? changes)
    {
        var moves = changes!.Where(move => move != null).ToList();
        int distinctFrom = moves.Select(move => move!.FromSeatNo).Distinct().Count();
        int distinctTo = moves.Select(move => move!.ToSeatNo).Distinct().Count();
        return distinctFrom == moves.Count && distinctTo == moves.Count;
    }

    private static bool MovesToAnotherSeat(SeatMoveRequest? move, string? toSeatNo)
    {
        return move!.FromSeatNo != toSeatNo;
    }
}

[ApiController]
[ApiExplorerSettings(GroupName = "Bookings")]
[Route("bookings")]
public sealed class CreateSeatChangeController : ControllerBase
{
    // Seat Inventory is usually back within a few seconds; the contract's example says 5.
    private const int UpstreamRetryAfterSeconds = 5;

    private readonly CreateSeatChangeHandler _handler;
    private readonly CreateSeatChangeValidator _validator = new CreateSeatChangeValidator();

    public CreateSeatChangeController(
        IWriterConnectionFactory writerFactory, ISeatInventoryClient seatInventory, IBookingSettings settings, IClock clock)
    {
        _handler = new CreateSeatChangeHandler(writerFactory, seatInventory, settings, clock);
    }

    /// <summary>Move a confirmed booking to other free seats of the same class (the booking's customer).</summary>
    /// <remarks>
    /// Moves 1 to 6 of the booking's seats to free seats of the same seat class, all together or none,
    /// through Seat Inventory. Each passenger and fare moves with their seat; no money moves. Allowed
    /// for a confirmed booking until the booking cutoff before departure, a limited number of times per
    /// booking. A request whose every move is already done returns 200 with no change.
    /// </remarks>
    /// <param name="bookingId">The booking's identifier.</param>
    /// <param name="request">The moves.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <response code="201">The seats are moved; the booking's seats after the change are returned.</response>
    /// <response code="200">Every move was already done; nothing changed.</response>
    /// <response code="400">The moves are not acceptable (VALIDATION_FAILED).</response>
    /// <response code="401">No valid access token was presented (UNAUTHORIZED).</response>
    /// <response code="403">The caller is not the booking's customer (FORBIDDEN).</response>
    /// <response code="404">No booking has this identifier (NOT_FOUND).</response>
    /// <response code="409">Not confirmed, trip cancelled, changes closed or a new seat is taken (BOOKING_NOT_CONFIRMED, TRIP_CANCELLED, BOOKING_CLOSED, SEATS_UNAVAILABLE).</response>
    /// <response code="422">Limit reached, or a seat is not the booking's, not on the trip or of another class (SEAT_CHANGE_LIMIT_REACHED, SEATS_NOT_IN_BOOKING, UNKNOWN_SEATS, SEAT_CLASS_MISMATCH).</response>
    /// <response code="502">Seat Inventory is down (UPSTREAM_UNAVAILABLE).</response>
    /// <response code="504">Seat Inventory did not answer in time (UPSTREAM_TIMEOUT).</response>
    [Authorize(Policy = Policies.Customer)]
    [HttpPost("{bookingId}/seatChanges")]
    [ProducesResponseType(typeof(SeatChange), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(SeatChange), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status504GatewayTimeout)]
    public async Task<IActionResult> CreateSeatChangeAsync(
        Guid bookingId, CreateSeatChangeRequest request, CancellationToken cancellationToken)
    {
        ValidationResult validation = await _validator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return ProblemResult.ValidationFailed(this, validation.ToDictionary());
        }

        Caller caller = CallerReader.Read(User);
        string correlationId = CorrelationIdMiddleware.GetCorrelationId(HttpContext);
        var target = new SeatChangeTarget(bookingId, caller, correlationId);
        CreateSeatChangeResult result = await _handler.HandleAsync(target, ToMoves(request), cancellationToken);
        if (result.Outcome == CreateSeatChangeOutcome.Changed)
        {
            return Created($"/bookings/{bookingId}", result.Response);
        }

        if (result.Outcome == CreateSeatChangeOutcome.AlreadyDone)
        {
            return Ok(result.Response);
        }

        return Refuse(bookingId, result);
    }

    private IActionResult Refuse(Guid bookingId, CreateSeatChangeResult result)
    {
        switch (result.Outcome)
        {
            case CreateSeatChangeOutcome.NotFound:
                return ProblemResult.NotFound(this, $"There is no booking {bookingId}.");
            case CreateSeatChangeOutcome.NotTheCustomersBooking:
                return ProblemResult.Forbidden(this, "Only the booking's customer can change its seats.");
            case CreateSeatChangeOutcome.BookingNotConfirmed:
                string status = JsonNamingPolicy.CamelCase.ConvertName(result.BookingStatus.ToString());
                return ProblemResult.BookingNotConfirmed(this, $"Booking {bookingId} is not confirmed.", status);
            case CreateSeatChangeOutcome.TripCancelled:
                return ProblemResult.TripCancelled(this, $"The trip of booking {bookingId} was cancelled.");
            case CreateSeatChangeOutcome.BookingClosed:
                return ProblemResult.BookingClosed(
                    this, "Seat changes have closed for this trip.", result.DepartureAt, result.CutoffMinutes);
            case CreateSeatChangeOutcome.BookingChangedMeanwhile:
                return ProblemResult.Conflict(this, $"Booking {bookingId} changed while its seats were being moved; try again.");
            default:
                return RefuseSeats(bookingId, result);
        }
    }

    private IActionResult RefuseSeats(Guid bookingId, CreateSeatChangeResult result)
    {
        switch (result.Outcome)
        {
            case CreateSeatChangeOutcome.SeatChangeLimitReached:
                return ProblemResult.SeatChangeLimitReached(
                    this, $"Booking {bookingId} has had the most seat changes allowed.", result.MaxSeatChanges);
            case CreateSeatChangeOutcome.SeatsNotInBooking:
                return ProblemResult.SeatsNotInBooking(this, "Some seats to leave are not part of this booking.", result.SeatNos);
            case CreateSeatChangeOutcome.UnknownSeats:
                return ProblemResult.UnknownSeats(this, "Some seats to move to do not exist on this trip.", result.SeatNos);
            case CreateSeatChangeOutcome.SeatClassMismatch:
                return ProblemResult.SeatClassMismatch(this, "A seat can only be changed for one of the same class.", ToItems(result.Moves));
            case CreateSeatChangeOutcome.SeatsUnavailable:
                return ProblemResult.SeatsUnavailable(this, "Some seats to move to are not free.", result.SeatNos);
            case CreateSeatChangeOutcome.UpstreamUnavailable:
                return ProblemResult.UpstreamUnavailable(
                    this, "Seat Inventory is not available; your seats are unchanged.", result.IsRetryable);
            case CreateSeatChangeOutcome.UpstreamTimeout:
                return ProblemResult.UpstreamTimeout(
                    this, "Seat Inventory did not answer in time; your seats are shown as last recorded.", true, UpstreamRetryAfterSeconds);
            default:
                throw new InvalidOperationException($"Unhandled outcome '{result.Outcome}'.");
        }
    }

    private static List<SeatMove> ToMoves(CreateSeatChangeRequest request)
    {
        var moves = new List<SeatMove>();
        foreach (SeatMoveRequest? move in request.Changes!)
        {
            moves.Add(new SeatMove(move!.FromSeatNo!, move.ToSeatNo!));
        }

        return moves;
    }

    private static List<SeatMoveProblemItem> ToItems(IReadOnlyList<SeatMove> moves)
    {
        var items = new List<SeatMoveProblemItem>();
        foreach (SeatMove move in moves)
        {
            items.Add(new SeatMoveProblemItem(move.FromSeatNo, move.ToSeatNo));
        }

        return items;
    }
}
