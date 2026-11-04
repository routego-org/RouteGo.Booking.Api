namespace RouteGo.Booking.Api.Infrastructure.Clients;

/// <summary>
/// Talks to Payment's internal payment start. Payment returns the same payment when asked twice for
/// one booking, so a repeat after a network error is harmless. An unreachable, slow, refusing or
/// unintelligible Payment becomes a named outcome. Registered once through
/// <see cref="HttpClients.Payment"/>.
/// </summary>
public sealed class PaymentClient : IPaymentClient
{
    private const string PaymentsPath = "internal/payments";
    private const string Currency = "INR";

    private readonly HttpClient _client;
    private readonly ILogger<PaymentClient> _logger;

    public PaymentClient(HttpClient client, ILogger<PaymentClient> logger)
    {
        _client = client;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<StartPaymentResult> StartPaymentAsync(
        Guid bookingId, long amount, string customerEmail, string correlationId, CancellationToken cancellationToken)
    {
        var body = new { bookingId, amount, currency = Currency, customerEmail };
        UpstreamReply reply = await UpstreamCaller.PostAsync(_client, PaymentsPath, body, correlationId, _logger, cancellationToken);
        if (reply.Reach == UpstreamReach.TimedOut)
        {
            return new StartPaymentResult { Outcome = StartPaymentOutcome.TimedOut };
        }

        if (!reply.IsSuccess)
        {
            _logger.LogWarning("Payment could not start a payment: status {StatusCode}, code {Code}", reply.StatusCode, reply.ProblemCode);
            return new StartPaymentResult { Outcome = StartPaymentOutcome.Unavailable };
        }

        return ReadStarted(reply);
    }

    private StartPaymentResult ReadStarted(UpstreamReply reply)
    {
        bool hasPaymentId = Guid.TryParse(reply.ReadText("paymentId"), out Guid paymentId);
        string? checkoutUrl = reply.ReadText("checkoutUrl");
        if (!hasPaymentId || string.IsNullOrEmpty(checkoutUrl))
        {
            _logger.LogWarning("Payment answered a payment start without a payment and a payment page");
            return new StartPaymentResult { Outcome = StartPaymentOutcome.Unavailable };
        }

        return new StartPaymentResult { Outcome = StartPaymentOutcome.Started, PaymentId = paymentId, CheckoutUrl = checkoutUrl };
    }
}
