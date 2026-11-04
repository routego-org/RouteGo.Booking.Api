namespace RouteGo.Booking.Api.Common.Ports;

/// <summary>The answer to a payment start: the outcome and, when started, the payment and its page.</summary>
public sealed record StartPaymentResult
{
    /// <summary>How Payment answered.</summary>
    public required StartPaymentOutcome Outcome { get; init; }

    /// <summary>Payment's identifier for the booking's one payment; present when started.</summary>
    public Guid? PaymentId { get; init; }

    /// <summary>The payment page address; present when started.</summary>
    public string? CheckoutUrl { get; init; }
}
