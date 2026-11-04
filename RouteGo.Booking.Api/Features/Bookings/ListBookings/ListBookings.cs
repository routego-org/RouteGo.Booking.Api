namespace RouteGo.Booking.Api.Features.Bookings.ListBookings;

/// <summary>Which page of the customer's bookings to list.</summary>
public sealed class ListBookingsRequest
{
    /// <summary>The page shown when none is asked for.</summary>
    public const int DefaultPage = 1;

    /// <summary>The page size used when none is asked for.</summary>
    public const int DefaultPageSize = 20;

    /// <summary>The most bookings one page may hold.</summary>
    public const int MaxPageSize = 200;

    /// <summary>1-based page number; 1 when absent.</summary>
    public int? Page { get; init; }

    /// <summary>Items per page, 1 to 200; 20 when absent.</summary>
    public int? PageSize { get; init; }
}

/// <summary>The kind of bus a trip runs.</summary>
public enum BusType
{
    Seater,
    Sleeper,
    SemiSleeper,
}

/// <summary>The trip a booking is for.</summary>
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

/// <summary>One booking as the customer's list shows it.</summary>
public sealed class BookingSummary
{
    public required Guid BookingId { get; init; }

    public required BookingStatus Status { get; init; }

    public required TripSummary Trip { get; init; }

    public required IReadOnlyList<string> SeatNos { get; init; }

    /// <summary>Whole rupees.</summary>
    public required long TotalAmount { get; init; }

    /// <summary>Always INR.</summary>
    public required string Currency { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Given whenever the booking has a refund.</summary>
    public required BookingRefund? Refund { get; init; }
}

/// <summary>One page of the customer's bookings, newest first.</summary>
public sealed class BookingPage
{
    public required IReadOnlyList<BookingSummary> Items { get; init; }

    public required int Page { get; init; }

    public required int PageSize { get; init; }

    /// <summary>How many bookings are listed across all pages.</summary>
    public required int TotalItems { get; init; }
}

public sealed class ListBookingsValidator : AbstractValidator<ListBookingsRequest>
{
    public ListBookingsValidator()
    {
        RuleFor(x => x.Page).GreaterThanOrEqualTo(ListBookingsRequest.DefaultPage)
            .WithMessage("The page must be 1 or more.");
        RuleFor(x => x.PageSize).InclusiveBetween(1, ListBookingsRequest.MaxPageSize)
            .WithMessage("The page size must be 1 to 200.");
    }
}

[ApiController]
[ApiExplorerSettings(GroupName = "Bookings")]
[Route("bookings")]
public sealed class ListBookingsController : ControllerBase
{
    private readonly ListBookingsHandler _handler;
    private readonly ListBookingsValidator _validator = new ListBookingsValidator();

    public ListBookingsController(IReaderConnectionFactory readerFactory)
    {
        _handler = new ListBookingsHandler(readerFactory);
    }

    /// <summary>List the signed-in customer's bookings, newest first (Customer).</summary>
    /// <remarks>
    /// The customer's own bookings in status confirmed, cancelled, failed or expired, newest first by
    /// creation time, each with its refund if any. Bookings still unpaid are not listed, and neither
    /// are bookings that failed while being created. No bookings is an empty page, not an error.
    /// </remarks>
    /// <param name="request">The page wanted.</param>
    /// <param name="cancellationToken">Cancels the listing.</param>
    /// <response code="200">The requested page.</response>
    /// <response code="400">The page or page size is out of range (VALIDATION_FAILED).</response>
    /// <response code="401">No valid access token was presented (UNAUTHORIZED).</response>
    /// <response code="403">The caller is not a customer (FORBIDDEN).</response>
    [Authorize(Policy = Policies.Customer)]
    [HttpGet]
    [ProducesResponseType(typeof(BookingPage), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ListBookingsAsync([FromQuery] ListBookingsRequest request, CancellationToken cancellationToken)
    {
        ValidationResult validation = await _validator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return ProblemResult.ValidationFailed(this, validation.ToDictionary());
        }

        Caller caller = CallerReader.Read(User);
        BookingPage page = await _handler.HandleAsync(request, caller, cancellationToken);
        return Ok(page);
    }
}
