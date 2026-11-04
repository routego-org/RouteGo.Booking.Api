namespace RouteGo.Booking.Api.Features.Bookings.CreateBooking;

/// <summary>One seat the customer books and the passenger who will sit in it.</summary>
public sealed class BookingSeatRequest
{
    /// <summary>The seat label, as the trip's seat map spells it.</summary>
    public string? SeatNo { get; init; }

    /// <summary>The passenger's name, 2 to 60 characters.</summary>
    public string? PassengerName { get; init; }

    /// <summary>Whole years, 0 to 120. Read as a number with decimals so that 4.5 is refused by name instead of failing the whole body.</summary>
    public decimal? PassengerAge { get; init; }

    /// <summary>male, female or other, in any letter case.</summary>
    public string? PassengerGender { get; init; }
}

/// <summary>What the customer wants to book: the trip, the seats with their passengers, the contact and the total they saw.</summary>
public sealed class BookingRequest
{
    /// <summary>The trip, as Fleet identifies it.</summary>
    public Guid? TripId { get; init; }

    /// <summary>The booking contact's name, 2 to 60 characters.</summary>
    public string? ContactName { get; init; }

    /// <summary>1 to the seat limit different seats, one passenger each.</summary>
    public List<BookingSeatRequest>? Seats { get; init; }

    /// <summary>The total in whole rupees the customer saw; only compared with the current total, never charged.</summary>
    public long? ExpectedTotal { get; init; }
}

/// <summary>Everything the handler needs to know about the caller and the request, with the correlation ID its events carry.</summary>
/// <param name="Caller">The signed-in customer; the booking belongs to this account and uses its email.</param>
/// <param name="Request">The validated request.</param>
/// <param name="CorrelationId">The request's correlation ID, copied into the events and calls the request causes.</param>
public sealed record BookingOrder(Caller Caller, BookingRequest Request, string CorrelationId);

/// <summary>Checks the shape of a booking request; every business limit comes from the domain rules or the settings.</summary>
public sealed class CreateBookingValidator : AbstractValidator<BookingRequest>
{
    private readonly int _maxSeats;

    public CreateBookingValidator(int maxSeats)
    {
        _maxSeats = maxSeats;
        RuleFor(x => x.TripId).NotNull().NotEqual(Guid.Empty).WithMessage("Name the trip.");
        RuleFor(x => x.ContactName).Must(BookingRules.IsValidName)
            .WithMessage("The contact name must be 2 to 60 characters.");
        RuleFor(x => x.ExpectedTotal).NotNull().GreaterThanOrEqualTo(0L)
            .WithMessage("Give the total you saw, in whole rupees.");
        RuleFor(x => x.Seats).Must(HasAcceptableCount).WithMessage($"Book 1 to {maxSeats} seats.");
        RuleFor(x => x.Seats).Must(HasDistinctSeats).WithMessage("Each seat may be named only once.");
        RuleForEach(x => x.Seats).NotNull().SetValidator(new BookingSeatValidator());
    }

    private bool HasAcceptableCount(List<BookingSeatRequest>? seats)
    {
        if (seats is null)
        {
            return false;
        }

        return seats.Count >= 1 && seats.Count <= _maxSeats;
    }

    private static bool HasDistinctSeats(List<BookingSeatRequest>? seats)
    {
        if (seats is null)
        {
            return true;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (BookingSeatRequest? seat in seats)
        {
            if (seat is null)
            {
                continue;
            }

            string label = (seat.SeatNo ?? string.Empty).Trim();
            if (!seen.Add(label))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>Checks one seat of a booking request and its passenger.</summary>
public sealed class BookingSeatValidator : AbstractValidator<BookingSeatRequest>
{
    public BookingSeatValidator()
    {
        RuleFor(x => x.SeatNo).Must(seatNo => !string.IsNullOrWhiteSpace(seatNo)).WithMessage("Name the seat.");
        RuleFor(x => x.PassengerName).Must(BookingRules.IsValidName)
            .WithMessage("The passenger name must be 2 to 60 characters.");
        RuleFor(x => x.PassengerAge).Must(IsAcceptableAge).WithMessage("The passenger age must be a whole number of years, 0 to 120.");
        RuleFor(x => x.PassengerGender).Must(BookingRules.IsValidGender).WithMessage("The passenger gender must be male, female or other.");
    }

    private static bool IsAcceptableAge(decimal? age)
    {
        if (age is null)
        {
            return false;
        }

        bool isWhole = decimal.Truncate(age.Value) == age.Value;
        bool isInIntRange = age.Value >= int.MinValue && age.Value <= int.MaxValue;
        if (!isWhole || !isInIntRange)
        {
            return false;
        }

        return BookingRules.IsValidPassengerAge((int)age.Value);
    }
}

[ApiController]
[ApiExplorerSettings(GroupName = "Bookings")]
[Route("bookings")]
public sealed class CreateBookingController : ControllerBase
{
    private readonly CreateBookingHandler _handler;
    private readonly IBookingSettings _settings;

    public CreateBookingController(
        IWriterConnectionFactory writerFactory,
        ISeatInventoryClient seatInventory,
        IPaymentClient payment,
        IBookingSettings settings,
        IClock clock)
    {
        _handler = new CreateBookingHandler(writerFactory, seatInventory, payment, clock);
        _settings = settings;
    }

    /// <summary>Create a booking: check the trip and price, hold the seats, start the payment (Customer only).</summary>
    /// <remarks>
    /// Checks the trip and the seats against Booking's trip snapshot, compares the total of the seats'
    /// current fares with the expected total, refuses while the customer has another unpaid booking,
    /// holds every seat through Seat Inventory (all or nothing) and starts the payment. The answer
    /// carries the hold expiry and the payment page address. If the hold or the payment start fails,
    /// nothing is left held and the booking is recorded as failed.
    /// </remarks>
    /// <param name="request">The trip, the seats with their passengers, the contact and the expected total.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <response code="201">The booking is created, its seats held and its payment started (status paymentPending).</response>
    /// <response code="400">The request is not valid (VALIDATION_FAILED).</response>
    /// <response code="401">No valid access token was presented (UNAUTHORIZED).</response>
    /// <response code="403">The caller is not a customer (FORBIDDEN).</response>
    /// <response code="404">Booking has no trip snapshot for this trip (NOT_FOUND).</response>
    /// <response code="409">The trip is cancelled or closed, the price changed, an unpaid booking exists, or seats are taken.</response>
    /// <response code="422">The trip does not have some of the seats (UNKNOWN_SEATS).</response>
    /// <response code="502">Seat Inventory or Payment is unavailable (UPSTREAM_UNAVAILABLE).</response>
    /// <response code="504">Seat Inventory or Payment did not answer in time (UPSTREAM_TIMEOUT).</response>
    [Authorize(Policy = Policies.Customer)]
    [HttpPost]
    [ProducesResponseType(typeof(Booking), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status504GatewayTimeout)]
    public async Task<IActionResult> CreateBookingAsync(BookingRequest request, CancellationToken cancellationToken)
    {
        BookingSettingsValues settings = _settings.Current;
        var validator = new CreateBookingValidator(settings.MaxSeats);
        ValidationResult validation = await validator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return ProblemResult.ValidationFailed(this, ToErrors(validation));
        }

        Caller caller = CallerReader.Read(User);
        string correlationId = CorrelationIdMiddleware.GetCorrelationId(HttpContext);
        var order = new BookingOrder(caller, request, correlationId);
        CreateBookingResult result = await _handler.HandleAsync(order, settings, cancellationToken);
        if (result.Outcome == CreateBookingOutcome.Created)
        {
            Booking booking = result.Response!;
            return Created($"/bookings/{booking.BookingId}", booking);
        }

        return MapRefusal(result);
    }

    private static Dictionary<string, string[]> ToErrors(ValidationResult validation)
    {
        var errors = new Dictionary<string, string[]>();
        foreach (IGrouping<string, ValidationFailure> field in validation.Errors.GroupBy(failure => failure.PropertyName))
        {
            errors[field.Key] = field.Select(failure => failure.ErrorMessage).ToArray();
        }

        return errors;
    }

    private ProblemResult MapRefusal(CreateBookingResult result)
    {
        switch (result.Outcome)
        {
            case CreateBookingOutcome.TripNotFound:
                return ProblemResult.NotFound(this, "Booking does not know this trip.");
            case CreateBookingOutcome.TripCancelled:
                return ProblemResult.TripCancelled(this, "This trip was cancelled.");
            case CreateBookingOutcome.BookingClosed:
                return ProblemResult.BookingClosed(this, "Booking for this trip has closed.", result.DepartureAt, result.CutoffMinutes);
            case CreateBookingOutcome.UnknownSeats:
                return ProblemResult.UnknownSeats(this, "The trip does not have these seats.", result.SeatNos);
            case CreateBookingOutcome.PriceChanged:
                return ProblemResult.PriceChanged(this, "The fares changed since you saw them.", result.CurrentTotal, result.SeatFares);
            case CreateBookingOutcome.UnpaidBookingExists:
                return ProblemResult.UnpaidBookingExists(
                    this, "You already have a booking waiting for payment.", result.ExistingBookingId, result.HoldExpiresAt);
            case CreateBookingOutcome.SeatsUnavailable:
                return ProblemResult.SeatsUnavailable(this, "Some seats are not available.", result.SeatNos);
            default:
                return MapFailure(result);
        }
    }

    private ProblemResult MapFailure(CreateBookingResult result)
    {
        switch (result.Outcome)
        {
            case CreateBookingOutcome.UpstreamUnavailable:
                return ProblemResult.UpstreamUnavailable(this, "We could not complete your booking. Please try again.", result.IsRetryable);
            case CreateBookingOutcome.UpstreamTimeout:
                return ProblemResult.UpstreamTimeout(
                    this, "We could not complete your booking in time. Please try again.", true, CreateBookingResult.RetryAfterSeconds);
            case CreateBookingOutcome.StateChanged:
                return ProblemResult.Conflict(this, "The booking changed while it was being created. Please check your bookings.");
            default:
                throw new InvalidOperationException($"Unhandled outcome '{result.Outcome}'.");
        }
    }
}
