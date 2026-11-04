namespace RouteGo.Booking.Api.Common.Security;

/// <summary>
/// Who is calling, as the access token says: the controller reads it from the token and passes it to
/// the handler, so a handler never touches the principal and stays callable from anywhere.
/// </summary>
/// <param name="AccountId">The token's subject: the customer, operator or admin account.</param>
/// <param name="Email">The token's email.</param>
/// <param name="Role">Customer, Operator or Admin.</param>
/// <param name="OperatorId">The operator's identifier; present only for an operator.</param>
public sealed record Caller(string AccountId, string Email, string Role, string? OperatorId)
{
    /// <summary>Whether the caller is an admin.</summary>
    public bool IsAdmin
    {
        get
        {
            return Role == Policies.Admin;
        }
    }
}
