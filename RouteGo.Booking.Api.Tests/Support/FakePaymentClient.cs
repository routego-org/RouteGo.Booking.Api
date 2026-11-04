namespace RouteGo.Booking.Api.Tests.Support;

/// <summary>
/// Stands in for Payment: starts every payment with the result a test sets (a new payment and its
/// page by default) and remembers each request, so a test can assert what Booking asked for.
/// </summary>
public sealed class FakePaymentClient : IPaymentClient
{
    /// <summary>The page every started payment sends the customer to.</summary>
    public const string DefaultCheckoutUrl = "http://localhost:5099/pay/test";

    /// <summary>The answer to a payment start; a started payment by default.</summary>
    public StartPaymentResult Result { get; set; } = new StartPaymentResult
    {
        Outcome = StartPaymentOutcome.Started,
        PaymentId = Guid.Parse("0a0a0a0a-0000-4000-8000-000000000001"),
        CheckoutUrl = DefaultCheckoutUrl,
    };

    /// <summary>Every payment start asked for: booking, amount, customer email.</summary>
    public List<FakePaymentStart> Starts { get; } = new List<FakePaymentStart>();

    /// <inheritdoc />
    public Task<StartPaymentResult> StartPaymentAsync(
        Guid bookingId, long amount, string customerEmail, string correlationId, CancellationToken cancellationToken)
    {
        Starts.Add(new FakePaymentStart(bookingId, amount, customerEmail));
        return Task.FromResult(Result);
    }
}
