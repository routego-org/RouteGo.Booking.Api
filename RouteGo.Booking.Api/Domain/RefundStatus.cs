namespace RouteGo.Booking.Api.Domain;

/// <summary>Whether Payment has returned a refund's money.</summary>
public enum RefundStatus
{
    /// <summary>Booking has asked Payment to return the money.</summary>
    Requested,

    /// <summary>Payment reported the money returned.</summary>
    Completed,
}
