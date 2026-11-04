namespace RouteGo.Booking.Api.Domain;

/// <summary>Who started the cancellation of a confirmed booking.</summary>
public enum CancellationKind
{
    /// <summary>The booking's customer.</summary>
    CustomerCancelled,

    /// <summary>An admin, using the force-cancel.</summary>
    AdminCancelled,

    /// <summary>The operator, by cancelling the trip in Fleet.</summary>
    OperatorCancelled,
}
