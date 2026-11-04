namespace RouteGo.Booking.Api.Infrastructure.Clients;

/// <summary>Whether another service answered at all.</summary>
public enum UpstreamReach
{
    /// <summary>The service answered; the status code and body say how.</summary>
    Answered,

    /// <summary>The service could not be reached.</summary>
    Down,

    /// <summary>The service did not answer in time.</summary>
    TimedOut,
}
