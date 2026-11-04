namespace RouteGo.Booking.Api.Common.Security;

/// <summary>The authorization policies endpoints name.</summary>
public static class Policies
{
    /// <summary>The access token's role claim is Customer.</summary>
    public const string Customer = "Customer";

    /// <summary>The access token's role claim is Operator.</summary>
    public const string Operator = "Operator";

    /// <summary>The access token's role claim is Admin.</summary>
    public const string Admin = "Admin";

    /// <summary>The access token's role claim is Customer or Admin: the roles that may read a booking.</summary>
    public const string CustomerOrAdmin = "CustomerOrAdmin";
}
