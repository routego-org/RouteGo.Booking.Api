namespace RouteGo.Booking.Api.Common.Problems;

/// <summary>
/// Projection of the product's problem-type catalog (contracts/problem-types.md in the
/// product-specs repo). Codes are never minted here — a new code starts life in the catalog
/// and lands here as a constant carrying the same code and the same type URI, so this class
/// can be diffed against the catalog rather than trusted to match it.
/// </summary>
public static class ProblemTypes
{
    private const string ProblemsBase = "urn:routego:booking:problems";

    /// <summary>400 — the request failed shape validation; the `errors` member names each field.</summary>
    public static readonly ProblemType ValidationFailed =
        new("VALIDATION_FAILED", $"{ProblemsBase}:validation-failed");

    /// <summary>401 — missing, expired or invalid access token. Written by AuthenticationProblems.</summary>
    public static readonly ProblemType Unauthorized =
        new("UNAUTHORIZED", $"{ProblemsBase}:unauthorized");

    /// <summary>403 — the caller's role may not perform the operation, or the booking or trip belongs to someone else.</summary>
    public static readonly ProblemType Forbidden =
        new("FORBIDDEN", $"{ProblemsBase}:forbidden");

    /// <summary>404 — the booking does not exist, or the trip has no trip snapshot.</summary>
    public static readonly ProblemType NotFound =
        new("NOT_FOUND", $"{ProblemsBase}:not-found");

    /// <summary>409 — state changed since read, or the request duplicates existing state.</summary>
    public static readonly ProblemType Conflict =
        new("CONFLICT", $"{ProblemsBase}:conflict");

    /// <summary>422 — a business rule refused the request and no specific code exists yet.</summary>
    public static readonly ProblemType BusinessRuleViolated =
        new("BUSINESS_RULE_VIOLATED", $"{ProblemsBase}:business-rule-violated");

    /// <summary>502 — Seat Inventory or Payment is down or gave an unusable answer; nothing is left held.</summary>
    public static readonly ProblemType UpstreamUnavailable =
        new("UPSTREAM_UNAVAILABLE", $"{ProblemsBase}:upstream-unavailable");

    /// <summary>504 — Seat Inventory or Payment did not answer in time; nothing is left held.</summary>
    public static readonly ProblemType UpstreamTimeout =
        new("UPSTREAM_TIMEOUT", $"{ProblemsBase}:upstream-timeout");

    /// <summary>500 — unhandled server fault. Emitted by UnhandledExceptionHandler, never by a slice.</summary>
    public static readonly ProblemType InternalError =
        new("INTERNAL_ERROR", $"{ProblemsBase}:internal-error");

    /// <summary>409 — a booking or seat change names seats that are not free; nothing held or moved.</summary>
    public static readonly ProblemType SeatsUnavailable =
        new("SEATS_UNAVAILABLE", $"{ProblemsBase}:seats-unavailable");

    /// <summary>422 — seat labels the trip does not have.</summary>
    public static readonly ProblemType UnknownSeats =
        new("UNKNOWN_SEATS", $"{ProblemsBase}:unknown-seats");

    /// <summary>409 — a booking or seat change on a trip Fleet cancelled.</summary>
    public static readonly ProblemType TripCancelled =
        new("TRIP_CANCELLED", $"{ProblemsBase}:trip-cancelled");

    /// <summary>409 — less than the booking cutoff before departure, or after it.</summary>
    public static readonly ProblemType BookingClosed =
        new("BOOKING_CLOSED", $"{ProblemsBase}:booking-closed");

    /// <summary>409 — the expected total differs from the total of the seats' current fares.</summary>
    public static readonly ProblemType PriceChanged =
        new("PRICE_CHANGED", $"{ProblemsBase}:price-changed");

    /// <summary>409 — the customer already has a booking held but not yet paid.</summary>
    public static readonly ProblemType UnpaidBookingExists =
        new("UNPAID_BOOKING_EXISTS", $"{ProblemsBase}:unpaid-booking-exists");

    /// <summary>409 — abandoning a booking that is no longer waiting for payment.</summary>
    public static readonly ProblemType BookingNotUnpaid =
        new("BOOKING_NOT_UNPAID", $"{ProblemsBase}:booking-not-unpaid");

    /// <summary>409 — a seat change, cancellation preview, cancellation or force-cancel of a booking that is not Confirmed.</summary>
    public static readonly ProblemType BookingNotConfirmed =
        new("BOOKING_NOT_CONFIRMED", $"{ProblemsBase}:booking-not-confirmed");

    /// <summary>422 — the booking has already had the most seat changes allowed.</summary>
    public static readonly ProblemType SeatChangeLimitReached =
        new("SEAT_CHANGE_LIMIT_REACHED", $"{ProblemsBase}:seat-change-limit-reached");

    /// <summary>422 — a seat change moves from seats that are not the booking's.</summary>
    public static readonly ProblemType SeatsNotInBooking =
        new("SEATS_NOT_IN_BOOKING", $"{ProblemsBase}:seats-not-in-booking");

    /// <summary>422 — a seat change moves to a seat of a different class.</summary>
    public static readonly ProblemType SeatClassMismatch =
        new("SEAT_CLASS_MISMATCH", $"{ProblemsBase}:seat-class-mismatch");

    /// <summary>409 — a cancellation preview or cancellation after the trip's departure time.</summary>
    public static readonly ProblemType TripDeparted =
        new("TRIP_DEPARTED", $"{ProblemsBase}:trip-departed");

    /// <summary>409 — an admin force-cancel more than the force-cancel window after departure.</summary>
    public static readonly ProblemType ForceCancelWindowClosed =
        new("FORCE_CANCEL_WINDOW_CLOSED", $"{ProblemsBase}:force-cancel-window-closed");

    /// <summary>Worker alert — a Payment event could not be applied.</summary>
    public static readonly ProblemType PaymentEventNotApplied =
        new("PAYMENT_EVENT_NOT_APPLIED", $"{ProblemsBase}:payment-event-not-applied");

    /// <summary>Worker alert — a Seat Inventory event could not be applied.</summary>
    public static readonly ProblemType SeatEventNotApplied =
        new("SEAT_EVENT_NOT_APPLIED", $"{ProblemsBase}:seat-event-not-applied");

    /// <summary>Worker alert — a Fleet event could not be applied.</summary>
    public static readonly ProblemType FleetEventNotApplied =
        new("FLEET_EVENT_NOT_APPLIED", $"{ProblemsBase}:fleet-event-not-applied");

    /// <summary>Worker alert — the oldest booking event not yet sent is more than 5 minutes old.</summary>
    public static readonly ProblemType BookingEventDelayed =
        new("BOOKING_EVENT_DELAYED", $"{ProblemsBase}:booking-event-delayed");
}
