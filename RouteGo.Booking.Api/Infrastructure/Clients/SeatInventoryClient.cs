namespace RouteGo.Booking.Api.Infrastructure.Clients;

/// <summary>
/// Talks to Seat Inventory's internal operations: hold, confirm, release and seat change, each
/// repeat-safe on its side. An unreachable, slow or unintelligible Seat Inventory becomes a named
/// outcome. Registered once through <see cref="HttpClients.SeatInventory"/>; holds nothing that changes
/// per call.
/// </summary>
public sealed class SeatInventoryClient : ISeatInventoryClient
{
    private const string HoldNotActiveCode = "HOLD_NOT_ACTIVE";
    private const string TripCancelledCode = "TRIP_CANCELLED";
    private const string SeatsUnavailableCode = "SEATS_UNAVAILABLE";
    private const string UnknownSeatsCode = "UNKNOWN_SEATS";
    private const string AlreadyConfirmedCode = "HOLD_ALREADY_CONFIRMED";
    private const string SeatsNotOwnedCode = "SEATS_NOT_OWNED";
    private const string SeatClassMismatchCode = "SEAT_CLASS_MISMATCH";

    private readonly HttpClient _client;
    private readonly ILogger<SeatInventoryClient> _logger;

    public SeatInventoryClient(HttpClient client, ILogger<SeatInventoryClient> logger)
    {
        _client = client;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<HoldSeatsResult> HoldSeatsAsync(
        Guid tripId, Guid bookingId, IReadOnlyList<string> seatNos, int holdSeconds, string correlationId, CancellationToken cancellationToken)
    {
        var body = new { bookingId, seatNos, holdSeconds };
        string path = $"internal/trips/{tripId:D}/holds";
        UpstreamReply reply = await UpstreamCaller.PostAsync(_client, path, body, correlationId, _logger, cancellationToken);
        if (reply.IsSuccess)
        {
            return ReadHeld(reply);
        }

        return ReadHoldRefusal(reply);
    }

    /// <inheritdoc />
    public async Task<ConfirmSeatsResult> ConfirmSeatsAsync(
        Guid tripId, Guid bookingId, string correlationId, CancellationToken cancellationToken)
    {
        string path = $"internal/trips/{tripId:D}/holds/{bookingId:D}/confirm";
        UpstreamReply reply = await UpstreamCaller.PostAsync(_client, path, null, correlationId, _logger, cancellationToken);
        return new ConfirmSeatsResult(ReadConfirmOutcome(reply));
    }

    /// <inheritdoc />
    public async Task<ReleaseSeatsResult> ReleaseSeatsAsync(
        Guid tripId, Guid bookingId, string correlationId, CancellationToken cancellationToken)
    {
        string path = $"internal/trips/{tripId:D}/holds/{bookingId:D}/release";
        UpstreamReply reply = await UpstreamCaller.PostAsync(_client, path, null, correlationId, _logger, cancellationToken);
        return new ReleaseSeatsResult(ReadReleaseOutcome(reply));
    }

    /// <inheritdoc />
    public async Task<ChangeSeatsResult> ChangeSeatsAsync(
        Guid tripId, Guid bookingId, IReadOnlyList<SeatMove> moves, string correlationId, CancellationToken cancellationToken)
    {
        var body = new { changes = moves };
        string path = $"internal/trips/{tripId:D}/holds/{bookingId:D}/seatChanges";
        UpstreamReply reply = await UpstreamCaller.PostAsync(_client, path, body, correlationId, _logger, cancellationToken);
        if (reply.IsSuccess)
        {
            return new ChangeSeatsResult { Outcome = ChangeSeatsOutcome.Changed };
        }

        return ReadChangeRefusal(reply);
    }

    private HoldSeatsResult ReadHeld(UpstreamReply reply)
    {
        string? expiry = reply.ReadText("holdExpiresAt");
        bool isTime = DateTimeOffset.TryParse(expiry, System.Globalization.CultureInfo.InvariantCulture, out DateTimeOffset holdExpiresAt);
        if (!isTime)
        {
            _logger.LogWarning("Seat Inventory answered a hold without a usable expiry");
            return new HoldSeatsResult { Outcome = HoldSeatsOutcome.Unavailable };
        }

        return new HoldSeatsResult { Outcome = HoldSeatsOutcome.Held, HoldExpiresAt = holdExpiresAt.ToUniversalTime() };
    }

    private HoldSeatsResult ReadHoldRefusal(UpstreamReply reply)
    {
        if (reply.Reach == UpstreamReach.TimedOut)
        {
            return new HoldSeatsResult { Outcome = HoldSeatsOutcome.TimedOut };
        }

        if (reply.Reach == UpstreamReach.Down || reply.StatusCode >= 500)
        {
            return new HoldSeatsResult { Outcome = HoldSeatsOutcome.Unavailable };
        }

        switch (reply.ProblemCode)
        {
            case SeatsUnavailableCode:
                return new HoldSeatsResult { Outcome = HoldSeatsOutcome.SeatsUnavailable, NamedSeats = reply.ReadTexts("unavailableSeats") };
            case UnknownSeatsCode:
                return new HoldSeatsResult { Outcome = HoldSeatsOutcome.UnknownSeats, NamedSeats = reply.ReadTexts("unknownSeats") };
            case TripCancelledCode:
                return new HoldSeatsResult { Outcome = HoldSeatsOutcome.TripCancelled };
            default:
                _logger.LogWarning("Seat Inventory refused a hold with status {StatusCode} and code {Code}", reply.StatusCode, reply.ProblemCode);
                return new HoldSeatsResult { Outcome = HoldSeatsOutcome.Refused };
        }
    }

    private ConfirmSeatsOutcome ReadConfirmOutcome(UpstreamReply reply)
    {
        if (reply.IsSuccess)
        {
            return ConfirmSeatsOutcome.Confirmed;
        }

        if (reply.Reach == UpstreamReach.TimedOut)
        {
            return ConfirmSeatsOutcome.TimedOut;
        }

        if (reply.Reach == UpstreamReach.Answered && reply.StatusCode == 409 && reply.ProblemCode == HoldNotActiveCode)
        {
            return ConfirmSeatsOutcome.HoldNotActive;
        }

        if (reply.Reach == UpstreamReach.Answered && reply.StatusCode == 409 && reply.ProblemCode == TripCancelledCode)
        {
            return ConfirmSeatsOutcome.TripCancelled;
        }

        _logger.LogWarning("Seat Inventory could not confirm: status {StatusCode}, code {Code}", reply.StatusCode, reply.ProblemCode);
        return ConfirmSeatsOutcome.Unavailable;
    }

    private ReleaseSeatsOutcome ReadReleaseOutcome(UpstreamReply reply)
    {
        if (reply.IsSuccess)
        {
            return ReleaseSeatsOutcome.Released;
        }

        if (reply.Reach == UpstreamReach.TimedOut)
        {
            return ReleaseSeatsOutcome.TimedOut;
        }

        if (reply.Reach == UpstreamReach.Answered && reply.StatusCode == 409 && reply.ProblemCode == AlreadyConfirmedCode)
        {
            return ReleaseSeatsOutcome.AlreadyConfirmed;
        }

        _logger.LogWarning("Seat Inventory could not release: status {StatusCode}, code {Code}", reply.StatusCode, reply.ProblemCode);
        return ReleaseSeatsOutcome.Unavailable;
    }

    private ChangeSeatsResult ReadChangeRefusal(UpstreamReply reply)
    {
        if (reply.Reach == UpstreamReach.TimedOut)
        {
            return new ChangeSeatsResult { Outcome = ChangeSeatsOutcome.TimedOut };
        }

        if (reply.Reach == UpstreamReach.Down || reply.StatusCode >= 500)
        {
            return new ChangeSeatsResult { Outcome = ChangeSeatsOutcome.Unavailable };
        }

        switch (reply.ProblemCode)
        {
            case SeatsUnavailableCode:
                return new ChangeSeatsResult { Outcome = ChangeSeatsOutcome.SeatsUnavailable, NamedSeats = reply.ReadTexts("unavailableSeats") };
            case UnknownSeatsCode:
                return new ChangeSeatsResult { Outcome = ChangeSeatsOutcome.UnknownSeats, NamedSeats = reply.ReadTexts("unknownSeats") };
            case SeatsNotOwnedCode:
                return new ChangeSeatsResult { Outcome = ChangeSeatsOutcome.SeatsNotOwned, NamedSeats = reply.ReadTexts("seatNos") };
            case SeatClassMismatchCode:
                return new ChangeSeatsResult { Outcome = ChangeSeatsOutcome.SeatClassMismatch, NamedMoves = reply.ReadMoves("changes") };
            case TripCancelledCode:
                return new ChangeSeatsResult { Outcome = ChangeSeatsOutcome.TripCancelled };
            default:
                _logger.LogWarning("Seat Inventory refused a seat change with status {StatusCode} and code {Code}", reply.StatusCode, reply.ProblemCode);
                return new ChangeSeatsResult { Outcome = ChangeSeatsOutcome.Refused };
        }
    }
}
