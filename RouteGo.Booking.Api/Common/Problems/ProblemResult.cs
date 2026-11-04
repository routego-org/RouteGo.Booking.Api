using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace RouteGo.Booking.Api.Common.Problems;

/// <summary>
/// An RFC 7807 problem response reporting one cataloged problem type. Every business refusal
/// in every slice is built here, so the same refusal always carries the same type URI, title,
/// status, and machine-readable code — and the catalog row stays inspectable, which is what
/// lets the codebase be checked against the product's problem-type catalog rather than
/// trusted to match it.
/// <para>
/// Extending this: one factory method per row in the product's problem-type catalog, and
/// never a method for a code the catalog does not define. The <c>detail</c> sentence is
/// always a parameter — it is the one part the catalog leaves to the operation — and no
/// slice-specific logic belongs here. The constructor is private so a problem response can
/// only ever be created through a cataloged factory.
/// </para>
/// </summary>
public sealed class ProblemResult : ObjectResult
{
    private readonly ProblemDetails _problemDetails;

    private ProblemResult(ProblemDetails problemDetails, ProblemType problemType)
        : base(problemDetails)
    {
        _problemDetails = problemDetails;
        ProblemType = problemType;
        StatusCode = problemDetails.Status;
    }

    /// <summary>The catalog row this response reports. Clients switch on its code.</summary>
    public ProblemType ProblemType { get; }

    /// <summary>400 — the request failed shape validation; each field is named in <c>errors</c>.</summary>
    public static ProblemResult ValidationFailed(ControllerBase controller, IDictionary<string, string[]> errors)
    {
        var modelState = new ModelStateDictionary();
        foreach (KeyValuePair<string, string[]> field in errors)
        {
            string fieldName = ToContractFieldName(field.Key);
            foreach (string message in field.Value)
            {
                modelState.AddModelError(fieldName, message);
            }
        }

        // Built through the factory for the same traceId reason as Build below.
        ValidationProblemDetails problemDetails = controller.ProblemDetailsFactory.CreateValidationProblemDetails(
            controller.HttpContext,
            modelState,
            statusCode: StatusCodes.Status400BadRequest,
            title: "One or more fields are invalid.",
            type: ProblemTypes.ValidationFailed.Type);
        problemDetails.Extensions["code"] = ProblemTypes.ValidationFailed.Code;
        return new ProblemResult(problemDetails, ProblemTypes.ValidationFailed);
    }

    /// <summary>403 — the caller's role may not perform the operation, or the booking or trip is someone else's.</summary>
    public static ProblemResult Forbidden(ControllerBase controller, string detail)
    {
        return Build(controller, ProblemTypes.Forbidden, "You may not perform this action.",
            StatusCodes.Status403Forbidden, detail);
    }

    /// <summary>409 — some seats are not free; nothing was held or moved.</summary>
    public static ProblemResult SeatsUnavailable(ControllerBase controller, string detail, IReadOnlyList<string> unavailableSeats)
    {
        ProblemResult result = Build(controller, ProblemTypes.SeatsUnavailable, "Some seats are not available.",
            StatusCodes.Status409Conflict, detail);
        result.AddExtension("unavailableSeats", unavailableSeats);
        return result;
    }

    /// <summary>422 — seat labels the trip does not have.</summary>
    public static ProblemResult UnknownSeats(ControllerBase controller, string detail, IReadOnlyList<string> unknownSeats)
    {
        ProblemResult result = Build(controller, ProblemTypes.UnknownSeats, "Some seats do not exist on this trip.",
            StatusCodes.Status422UnprocessableEntity, detail);
        result.AddExtension("unknownSeats", unknownSeats);
        return result;
    }

    /// <summary>409 — Fleet cancelled the trip.</summary>
    public static ProblemResult TripCancelled(ControllerBase controller, string detail)
    {
        return Build(controller, ProblemTypes.TripCancelled, "This trip was cancelled.",
            StatusCodes.Status409Conflict, detail);
    }

    /// <summary>409 — less than the booking cutoff before departure, or after it.</summary>
    public static ProblemResult BookingClosed(ControllerBase controller, string detail, DateTimeOffset departureAt, int cutoffMinutes)
    {
        ProblemResult result = Build(controller, ProblemTypes.BookingClosed, "Booking for this trip has closed.",
            StatusCodes.Status409Conflict, detail);
        result.AddExtension("departureAt", departureAt);
        result.AddExtension("cutoffMinutes", cutoffMinutes);
        return result;
    }

    /// <summary>409 — the fares changed since the customer saw them; nothing was held.</summary>
    public static ProblemResult PriceChanged(ControllerBase controller, string detail, long currentTotal, IReadOnlyList<SeatFareProblemItem> seatFares)
    {
        ProblemResult result = Build(controller, ProblemTypes.PriceChanged, "The price has changed.",
            StatusCodes.Status409Conflict, detail);
        result.AddExtension("currentTotal", currentTotal);
        result.AddExtension("seatFares", seatFares);
        return result;
    }

    /// <summary>409 — the customer already has a booking waiting for payment.</summary>
    public static ProblemResult UnpaidBookingExists(ControllerBase controller, string detail, Guid bookingId, DateTimeOffset? holdExpiresAt)
    {
        ProblemResult result = Build(controller, ProblemTypes.UnpaidBookingExists, "You already have a booking waiting for payment.",
            StatusCodes.Status409Conflict, detail);
        result.AddExtension("bookingId", bookingId);
        if (holdExpiresAt.HasValue)
        {
            result.AddExtension("holdExpiresAt", holdExpiresAt.Value);
        }

        return result;
    }

    /// <summary>409 — the booking is no longer waiting for payment.</summary>
    public static ProblemResult BookingNotUnpaid(ControllerBase controller, string detail, string bookingStatus)
    {
        ProblemResult result = Build(controller, ProblemTypes.BookingNotUnpaid, "This booking is no longer waiting for payment.",
            StatusCodes.Status409Conflict, detail);
        result.AddExtension("bookingStatus", bookingStatus);
        return result;
    }

    /// <summary>409 — the booking is not Confirmed.</summary>
    public static ProblemResult BookingNotConfirmed(ControllerBase controller, string detail, string bookingStatus)
    {
        ProblemResult result = Build(controller, ProblemTypes.BookingNotConfirmed, "This booking is not confirmed.",
            StatusCodes.Status409Conflict, detail);
        result.AddExtension("bookingStatus", bookingStatus);
        return result;
    }

    /// <summary>422 — the booking has already had the most seat changes allowed.</summary>
    public static ProblemResult SeatChangeLimitReached(ControllerBase controller, string detail, int maxSeatChanges)
    {
        ProblemResult result = Build(controller, ProblemTypes.SeatChangeLimitReached, "No more seat changes are possible for this booking.",
            StatusCodes.Status422UnprocessableEntity, detail);
        result.AddExtension("maxSeatChanges", maxSeatChanges);
        return result;
    }

    /// <summary>422 — a seat change moves from seats that are not the booking's.</summary>
    public static ProblemResult SeatsNotInBooking(ControllerBase controller, string detail, IReadOnlyList<string> seatNos)
    {
        ProblemResult result = Build(controller, ProblemTypes.SeatsNotInBooking, "Some seats are not part of this booking.",
            StatusCodes.Status422UnprocessableEntity, detail);
        result.AddExtension("seatNos", seatNos);
        return result;
    }

    /// <summary>422 — a seat change moves to a seat of a different class.</summary>
    public static ProblemResult SeatClassMismatch(ControllerBase controller, string detail, IReadOnlyList<SeatMoveProblemItem> changes)
    {
        ProblemResult result = Build(controller, ProblemTypes.SeatClassMismatch, "A seat can only be changed for one of the same class.",
            StatusCodes.Status422UnprocessableEntity, detail);
        result.AddExtension("changes", changes);
        return result;
    }

    /// <summary>409 — a customer's cancellation preview or cancellation after the trip's departure.</summary>
    public static ProblemResult TripDeparted(ControllerBase controller, string detail, DateTimeOffset departedAt)
    {
        ProblemResult result = Build(controller, ProblemTypes.TripDeparted, "This trip has departed.",
            StatusCodes.Status409Conflict, detail);
        result.AddExtension("departedAt", departedAt);
        return result;
    }

    /// <summary>409 — an admin force-cancel more than the force-cancel window after departure.</summary>
    public static ProblemResult ForceCancelWindowClosed(ControllerBase controller, string detail, DateTimeOffset departedAt, int windowDays)
    {
        ProblemResult result = Build(controller, ProblemTypes.ForceCancelWindowClosed, "This booking is too old to force-cancel.",
            StatusCodes.Status409Conflict, detail);
        result.AddExtension("departedAt", departedAt);
        result.AddExtension("windowDays", windowDays);
        return result;
    }

    /// <summary>404 — the resource does not exist.</summary>
    public static ProblemResult NotFound(ControllerBase controller, string detail)
    {
        return Build(controller, ProblemTypes.NotFound, "The requested resource does not exist.",
            StatusCodes.Status404NotFound, detail);
    }

    /// <summary>409 — the request conflicts with existing state.</summary>
    public static ProblemResult Conflict(ControllerBase controller, string detail)
    {
        return Build(controller, ProblemTypes.Conflict, "The request conflicts with existing state.",
            StatusCodes.Status409Conflict, detail);
    }

    /// <summary>422 — a business rule refused an otherwise well-formed request.</summary>
    public static ProblemResult BusinessRuleViolated(ControllerBase controller, string detail)
    {
        return Build(controller, ProblemTypes.BusinessRuleViolated, "A business rule refused the request.",
            StatusCodes.Status422UnprocessableEntity, detail);
    }

    /// <summary>
    /// 502 — a required dependency is down or returned an unusable response, leaving no usable
    /// data. A fan-out that got data from at least one source is a SUCCESS: return 200 with the
    /// contract's partial-success fields instead of a problem.
    /// </summary>
    public static ProblemResult UpstreamUnavailable(ControllerBase controller, string detail, bool retryable)
    {
        ProblemResult result = Build(controller, ProblemTypes.UpstreamUnavailable,
            "A required upstream service is unavailable.",
            StatusCodes.Status502BadGateway, detail);
        result.AddExtension("retryable", retryable);
        return result;
    }

    /// <summary>
    /// 504 — a required dependency did not respond in time and no data could be served. Pass
    /// retryAfterSeconds when the delay is known; the catalog documents it as a member clients
    /// act on. Same partial-success rule as UpstreamUnavailable.
    /// </summary>
    public static ProblemResult UpstreamTimeout(
        ControllerBase controller, string detail, bool retryable, int? retryAfterSeconds)
    {
        ProblemResult result = Build(controller, ProblemTypes.UpstreamTimeout,
            "A required upstream service did not respond in time.",
            StatusCodes.Status504GatewayTimeout, detail);
        result.AddExtension("retryable", retryable);
        if (retryAfterSeconds.HasValue)
        {
            result.AddExtension("retryAfterSeconds", retryAfterSeconds.Value);
        }

        return result;
    }

    /// <summary>
    /// Adds one extension member. Every member added here must appear in the catalog row's
    /// Extensions column — a member no consumer was told about is a member no consumer reads.
    /// </summary>
    private void AddExtension(string name, object value)
    {
        _problemDetails.Extensions[name] = value;
    }

    private static string ToContractFieldName(string fieldPath)
    {
        string[] segments = fieldPath.Split('.');
        for (int i = 0; i < segments.Length; i++)
        {
            segments[i] = JsonNamingPolicy.CamelCase.ConvertName(segments[i]);
        }

        return string.Join('.', segments);
    }

    private static ProblemResult Build(
        ControllerBase controller, ProblemType problemType, string title, int statusCode, string detail)
    {
        // Built through the controller's ProblemDetailsFactory rather than by hand, because
        // that factory is what applies the host's customization — the traceId correlating this
        // response with server-side telemetry. A hand-constructed ProblemDetails loses it
        // silently, and the loss only shows up when someone tries to trace a reported error.
        ProblemDetails problemDetails = controller.ProblemDetailsFactory.CreateProblemDetails(
            controller.HttpContext,
            statusCode: statusCode,
            title: title,
            type: problemType.Type,
            detail: detail);
        problemDetails.Extensions["code"] = problemType.Code;
        return new ProblemResult(problemDetails, problemType);
    }
}
