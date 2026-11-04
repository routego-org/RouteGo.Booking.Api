namespace RouteGo.Booking.Api.Common.Ports;

/// <summary>
/// What Booking asks of Payment, answering at once. Starting a payment twice for the same booking
/// returns the same payment, so a retry after a network error never charges twice. An unreachable or
/// unusable Payment is an outcome, not an exception.
/// </summary>
public interface IPaymentClient
{
    /// <summary>Starts the booking's one payment and returns the payment page to send the customer to.</summary>
    Task<StartPaymentResult> StartPaymentAsync(
        Guid bookingId, long amount, string customerEmail, string correlationId, CancellationToken cancellationToken);
}
