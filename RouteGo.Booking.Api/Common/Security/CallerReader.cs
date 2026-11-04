using System.Security.Claims;

namespace RouteGo.Booking.Api.Common.Security;

/// <summary>Reads the <see cref="Caller"/> out of a validated access token.</summary>
public static class CallerReader
{
    /// <summary>
    /// The caller the token describes. Only called behind an authorization policy, so the subject,
    /// email and role claims are present on every token that gets here.
    /// </summary>
    public static Caller Read(ClaimsPrincipal principal)
    {
        string accountId = principal.FindFirstValue(TokenClaims.Subject) ?? string.Empty;
        string email = principal.FindFirstValue(TokenClaims.Email) ?? string.Empty;
        string role = principal.FindFirstValue(TokenClaims.Role) ?? string.Empty;
        string? operatorId = principal.FindFirstValue(TokenClaims.OperatorId);
        return new Caller(accountId, email, role, operatorId);
    }
}
