namespace RouteGo.Booking.Api.Infrastructure.Clients;

/// <summary>
/// Names the secret that holds the RouteGo internal key: the shared key RouteGo's own services present
/// when they call each other's internal operations (here, Seat Inventory's holds and Payment's payment
/// start). One name in every environment: Development reads it from user-secrets, every other
/// environment from Secrets Manager. The value is the key itself, as plain text.
/// </summary>
public static class InternalKeySecret
{
    /// <summary>The only lookup key, everywhere; shared by every RouteGo service, so not under routego/booking/.</summary>
    public const string SecretId = "routego/internal-key";
}
