namespace RouteGo.Booking.Api.Features.Cancellations.CreateForceCancellation;

/// <summary>Why an admin cancels a booking, and how much of the money comes back.</summary>
public sealed class ForceCancellationRequest
{
    /// <summary>10 to 500 characters; kept on the cancellation and in the audit log.</summary>
    public string? Reason { get; init; }

    /// <summary>
    /// The refund share, a whole number 0 to 100; 100 when not given. Read as a decimal so that a
    /// fractional share such as 12.5 reaches validation and is refused by name instead of being cut.
    /// </summary>
    public decimal? RefundPercent { get; init; }
}

/// <summary>How a booking ended: who cancelled it, the refund and where the refund stands.</summary>
public sealed record Cancellation
{
    public required Guid BookingId { get; init; }

    /// <summary>adminCancelled for this operation.</summary>
    public required CancellationKind Kind { get; init; }

    /// <summary>The admin's reason.</summary>
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

public enum CreateForceCancellationOutcome
{
    Cancelled,
    AlreadyCancelled,
    NotFound,
    BookingNotConfirmed,
    ForceCancelWindowClosed,
}

/// <summary>How the force-cancellation ended.</summary>
public sealed record CreateForceCancellationResult
{
    public required CreateForceCancellationOutcome Outcome { get; init; }

    /// <summary>The cancellation, when the booking is cancelled now or was by an admin before.</summary>
    public Cancellation? Response { get; init; }

    /// <summary>The booking's status, when it is not confirmed.</summary>
    public BookingStatus BookingStatus { get; init; }

    /// <summary>When the trip departed, when the window has closed.</summary>
    public DateTimeOffset DepartedAt { get; init; }

    /// <summary>The force-cancel window in days, when it has closed.</summary>
    public int WindowDays { get; init; }
}

/// <summary>What an admin asks for.</summary>
/// <param name="BookingId">The booking to cancel.</param>
/// <param name="Reason">The admin's reason.</param>
/// <param name="RefundPercent">The refund share; null for the default.</param>
public sealed record ForceCancellation(Guid BookingId, string Reason, int? RefundPercent);

public sealed class CreateForceCancellationValidator : AbstractValidator<ForceCancellationRequest>
{
    // The cancellations table keeps a reason up to this long, and an admin should say more than a word.
    private const int MinReasonLength = 10;
    private const int MaxReasonLength = 500;

    public CreateForceCancellationValidator()
    {
        RuleFor(x => x.Reason).Must(IsAcceptableReason)
            .WithMessage("The reason must be 10 to 500 characters.");
        RuleFor(x => x.RefundPercent).Must(IsAcceptablePercent)
            .When(x => x.RefundPercent != null)
            .WithMessage("The refund percentage must be a whole number from 0 to 100.");
    }

    private static bool IsAcceptableReason(string? reason)
    {
        if (reason is null)
        {
            return false;
        }

        return reason.Length >= MinReasonLength && reason.Length <= MaxReasonLength;
    }

    private static bool IsAcceptablePercent(decimal? percent)
    {
        decimal value = percent!.Value;
        bool isWhole = decimal.Truncate(value) == value;
        bool isInRange = value >= int.MinValue && value <= int.MaxValue;
        if (!isWhole || !isInRange)
        {
            return false;
        }

        return RefundRules.IsValidPercent((int)value);
    }
}

[ApiController]
[ApiExplorerSettings(GroupName = "Cancellations")]
[Route("bookings")]
public sealed class CreateForceCancellationController : ControllerBase
{
    private readonly CreateForceCancellationHandler _handler;
    private readonly CreateForceCancellationValidator _validator = new CreateForceCancellationValidator();

    public CreateForceCancellationController(
        IWriterConnectionFactory writerFactory, IBookingSettings settings, IClock clock, ILogger<CreateForceCancellationHandler> logger)
    {
        _handler = new CreateForceCancellationHandler(writerFactory, settings, clock, logger);
    }

    /// <summary>Cancel a confirmed booking with a reason and a chosen refund, up to 7 days after departure (Admin).</summary>
    /// <remarks>
    /// Records an admin cancellation: the admin's reason, the refund percentage given (100 when none is),
    /// the amount rounded down; the seats are freed, the refund is asked of Payment when it is more than 0,
    /// and an audit record is sent. Allowed until the force-cancel window after the trip's departure. A
    /// booking an admin already cancelled returns that cancellation with 200, unchanged.
    /// </remarks>
    /// <param name="bookingId">The booking's identifier.</param>
    /// <param name="request">The reason and optionally the refund percentage.</param>
    /// <param name="cancellationToken">Cancels the request (not the booking).</param>
    /// <response code="201">The booking is cancelled; the cancellation is returned.</response>
    /// <response code="200">An admin already cancelled this booking; the existing cancellation is returned.</response>
    /// <response code="400">The reason or the percentage is not acceptable (VALIDATION_FAILED).</response>
    /// <response code="401">No valid access token was presented (UNAUTHORIZED).</response>
    /// <response code="403">The caller is not an admin (FORBIDDEN).</response>
    /// <response code="404">No booking has this identifier (NOT_FOUND).</response>
    /// <response code="409">The booking is not confirmed (BOOKING_NOT_CONFIRMED) or departed too long ago (FORCE_CANCEL_WINDOW_CLOSED).</response>
    [Authorize(Policy = Policies.Admin)]
    [HttpPost("{bookingId}/forceCancellations")]
    [ProducesResponseType(typeof(Cancellation), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(Cancellation), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateForceCancellationAsync(
        Guid bookingId, ForceCancellationRequest request, CancellationToken cancellationToken)
    {
        ValidationResult validation = await _validator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return ProblemResult.ValidationFailed(this, validation.ToDictionary());
        }

        Caller caller = CallerReader.Read(User);
        string correlationId = CorrelationIdMiddleware.GetCorrelationId(HttpContext);
        ForceCancellation forceCancellation = ToForceCancellation(bookingId, request);
        CreateForceCancellationResult result = await _handler.HandleAsync(forceCancellation, caller, correlationId, cancellationToken);
        switch (result.Outcome)
        {
            case CreateForceCancellationOutcome.Cancelled:
                return Created($"/bookings/{bookingId}", result.Response);
            case CreateForceCancellationOutcome.AlreadyCancelled:
                return Ok(result.Response);
            case CreateForceCancellationOutcome.NotFound:
                return ProblemResult.NotFound(this, $"There is no booking {bookingId}.");
            case CreateForceCancellationOutcome.BookingNotConfirmed:
                return ProblemResult.BookingNotConfirmed(
                    this, $"Booking {bookingId} is not confirmed.", ToWireStatus(result.BookingStatus));
            case CreateForceCancellationOutcome.ForceCancelWindowClosed:
                return ProblemResult.ForceCancelWindowClosed(
                    this, $"Booking {bookingId} departed too long ago to force-cancel.", result.DepartedAt, result.WindowDays);
            default:
                throw new InvalidOperationException($"Unhandled outcome '{result.Outcome}'.");
        }
    }

    // The validator has already shown the percentage is a whole number in range, so the cast loses nothing.
    private static ForceCancellation ToForceCancellation(Guid bookingId, ForceCancellationRequest request)
    {
        int? refundPercent = null;
        if (request.RefundPercent != null)
        {
            refundPercent = (int)request.RefundPercent.Value;
        }

        return new ForceCancellation(bookingId, request.Reason!, refundPercent);
    }

    // The wire spells a status in camelCase, as the contract's examples do.
    private static string ToWireStatus(BookingStatus status)
    {
        return JsonNamingPolicy.CamelCase.ConvertName(status.ToString());
    }
}
