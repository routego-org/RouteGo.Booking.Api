namespace RouteGo.Booking.Api.Common.Ports;

/// <summary>How Payment answered a start request.</summary>
public enum StartPaymentOutcome
{
    /// <summary>The payment exists and its payment page is ready.</summary>
    Started,

    /// <summary>Payment is down, refused the request, or gave an unusable answer.</summary>
    Unavailable,

    /// <summary>Payment did not answer in time.</summary>
    TimedOut,
}
