namespace RouteGo.Booking.Api.Tests.Support;

/// <summary>
/// Stands in for Seat Inventory: answers every call with the result a test sets (success by default)
/// and remembers every call it received, in order, so a test can assert what Booking asked for.
/// </summary>
public sealed class FakeSeatInventoryClient : ISeatInventoryClient
{
    private static readonly DateTimeOffset _defaultHoldExpiry = new DateTimeOffset(2099, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>The answer to a hold; the seats are held until far in the future by default.</summary>
    public HoldSeatsResult HoldResult { get; set; } = new HoldSeatsResult
    {
        Outcome = HoldSeatsOutcome.Held,
        HoldExpiresAt = _defaultHoldExpiry,
    };

    /// <summary>The answer to a confirm; confirmed by default.</summary>
    public ConfirmSeatsResult ConfirmResult { get; set; } = new ConfirmSeatsResult(ConfirmSeatsOutcome.Confirmed);

    /// <summary>The answer to a release; released by default.</summary>
    public ReleaseSeatsResult ReleaseResult { get; set; } = new ReleaseSeatsResult(ReleaseSeatsOutcome.Released);

    /// <summary>The answer to a seat change; changed by default.</summary>
    public ChangeSeatsResult ChangeResult { get; set; } = new ChangeSeatsResult { Outcome = ChangeSeatsOutcome.Changed };

    /// <summary>Every hold asked for: trip, booking, seats, hold seconds, correlation ID.</summary>
    public List<FakeHoldCall> Holds { get; } = new List<FakeHoldCall>();

    /// <summary>Every confirm asked for, as "tripId/bookingId".</summary>
    public List<string> Confirms { get; } = new List<string>();

    /// <summary>Every release asked for, as "tripId/bookingId".</summary>
    public List<string> Releases { get; } = new List<string>();

    /// <summary>Every seat change asked for: trip, booking, moves.</summary>
    public List<FakeChangeCall> Changes { get; } = new List<FakeChangeCall>();

    /// <inheritdoc />
    public Task<HoldSeatsResult> HoldSeatsAsync(
        Guid tripId, Guid bookingId, IReadOnlyList<string> seatNos, int holdSeconds, string correlationId, CancellationToken cancellationToken)
    {
        Holds.Add(new FakeHoldCall(tripId, bookingId, seatNos.ToList(), holdSeconds, correlationId));
        return Task.FromResult(HoldResult);
    }

    /// <inheritdoc />
    public Task<ConfirmSeatsResult> ConfirmSeatsAsync(Guid tripId, Guid bookingId, string correlationId, CancellationToken cancellationToken)
    {
        Confirms.Add($"{tripId}/{bookingId}");
        return Task.FromResult(ConfirmResult);
    }

    /// <inheritdoc />
    public Task<ReleaseSeatsResult> ReleaseSeatsAsync(Guid tripId, Guid bookingId, string correlationId, CancellationToken cancellationToken)
    {
        Releases.Add($"{tripId}/{bookingId}");
        return Task.FromResult(ReleaseResult);
    }

    /// <inheritdoc />
    public Task<ChangeSeatsResult> ChangeSeatsAsync(
        Guid tripId, Guid bookingId, IReadOnlyList<SeatMove> moves, string correlationId, CancellationToken cancellationToken)
    {
        Changes.Add(new FakeChangeCall(tripId, bookingId, moves.ToList()));
        return Task.FromResult(ChangeResult);
    }
}
