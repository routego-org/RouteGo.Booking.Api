namespace RouteGo.Booking.Api.Infrastructure.Clients;

/// <summary>
/// Adds the internal key to every call Booking makes to another RouteGo service. The key is read once
/// at start-up and is never logged. One handler serves each pooled client, so it holds nothing that
/// changes per request.
/// </summary>
public sealed class InternalKeyHandler : DelegatingHandler
{
    /// <summary>The header RouteGo's internal operations read the key from.</summary>
    public const string HeaderName = "X-Internal-Key";

    private readonly string _key;

    public InternalKeyHandler(string key)
    {
        _key = key;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Add(HeaderName, _key);
        return base.SendAsync(request, cancellationToken);
    }
}
