namespace RouteGo.Booking.Api.Features.Bookings.SearchBookings;

/// <summary>What to look for; a booking must match every criterion given, and at least one is required.</summary>
public sealed class BookingSearch
{
    public Guid? BookingId { get; init; }

    /// <summary>Matched whole, ignoring letter case.</summary>
    public string? ContactEmail { get; init; }

    public Guid? TripId { get; init; }
}

/// <summary>The search and the page wanted.</summary>
public sealed class SearchBookingsRequest
{
    /// <summary>The page shown when none is asked for.</summary>
    public const int DefaultPage = 1;

    /// <summary>The page size used when none is asked for.</summary>
    public const int DefaultPageSize = 20;

    /// <summary>The most bookings one page may hold.</summary>
    public const int MaxPageSize = 200;

    public required BookingSearch Search { get; init; }

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

/// <summary>One booking as the admin's search shows it.</summary>
public sealed class AdminBookingSummary
{
    public required Guid BookingId { get; init; }

    public required BookingStatus Status { get; init; }

    public required string ContactName { get; init; }

    public required string ContactEmail { get; init; }

    public required TripSummary Trip { get; init; }

    public required IReadOnlyList<string> SeatNos { get; init; }

    /// <summary>Whole rupees.</summary>
    public required long TotalAmount { get; init; }

    /// <summary>Always INR.</summary>
    public required string Currency { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}

/// <summary>One page of matching bookings, newest first.</summary>
public sealed class AdminBookingPage
{
    public required IReadOnlyList<AdminBookingSummary> Items { get; init; }

    public required int Page { get; init; }

    public required int PageSize { get; init; }

    /// <summary>How many bookings match across all pages.</summary>
    public required int TotalItems { get; init; }
}

public sealed class SearchBookingsValidator : AbstractValidator<SearchBookingsRequest>
{
    public SearchBookingsValidator()
    {
        RuleFor(x => x.Search).Must(HasCriterion).OverridePropertyName("search")
            .WithMessage("Give at least one of bookingId, contactEmail or tripId.");
        RuleFor(x => x.Page).GreaterThanOrEqualTo(SearchBookingsRequest.DefaultPage)
            .WithMessage("The page must be 1 or more.");
        RuleFor(x => x.PageSize).InclusiveBetween(1, SearchBookingsRequest.MaxPageSize)
            .WithMessage("The page size must be 1 to 200.");
    }

    private static bool HasCriterion(BookingSearch search)
    {
        bool hasEmail = !string.IsNullOrWhiteSpace(search.ContactEmail);
        return search.BookingId != null || search.TripId != null || hasEmail;
    }
}

[ApiController]
[ApiExplorerSettings(GroupName = "Bookings")]
[Route("bookings")]
public sealed class SearchBookingsController : ControllerBase
{
    private readonly SearchBookingsHandler _handler;
    private readonly SearchBookingsValidator _validator = new SearchBookingsValidator();

    public SearchBookingsController(IReaderConnectionFactory readerFactory)
    {
        _handler = new SearchBookingsHandler(readerFactory);
    }

    /// <summary>Search all bookings by booking ID, contact email or trip (Admin).</summary>
    /// <remarks>
    /// Bookings in every status that match every criterion given, newest first by creation time. At
    /// least one criterion is required. The email is matched whole, ignoring letter case. A query by
    /// POST because the email must not appear in an address.
    /// </remarks>
    /// <param name="search">The criteria.</param>
    /// <param name="page">1-based page number; 1 when absent.</param>
    /// <param name="pageSize">Items per page, 1 to 200; 20 when absent.</param>
    /// <param name="cancellationToken">Cancels the search.</param>
    /// <response code="200">The requested page.</response>
    /// <response code="400">No criterion was given, or the page or page size is out of range (VALIDATION_FAILED).</response>
    /// <response code="401">No valid access token was presented (UNAUTHORIZED).</response>
    /// <response code="403">The caller is not an admin (FORBIDDEN).</response>
    [Authorize(Policy = Policies.Admin)]
    [HttpPost("search")]
    [ProducesResponseType(typeof(AdminBookingPage), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> SearchBookingsAsync(
        [FromBody] BookingSearch search, [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken cancellationToken)
    {
        var request = new SearchBookingsRequest { Search = search, Page = page, PageSize = pageSize };
        ValidationResult validation = await _validator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return ProblemResult.ValidationFailed(this, validation.ToDictionary());
        }

        AdminBookingPage result = await _handler.HandleAsync(request, cancellationToken);
        return Ok(result);
    }
}
