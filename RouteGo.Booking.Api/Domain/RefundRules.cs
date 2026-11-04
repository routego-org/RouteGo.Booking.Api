namespace RouteGo.Booking.Api.Domain;

/// <summary>The rules for how much of a booking's money comes back, and the keys that make a refund happen once.</summary>
public static class RefundRules
{
    private const int FullRefundPercent = 100;

    /// <summary>The refund share for a customer cancelling with this much time left; exactly on a boundary the higher tier applies.</summary>
    /// <param name="timeToDeparture">Time left before departure at the moment of cancelling.</param>
    /// <param name="tiers">The refund schedule, in any order.</param>
    public static int CalculatePercent(TimeSpan timeToDeparture, IReadOnlyList<RefundTier> tiers)
    {
        var byHoursBeforeDescending = tiers.OrderByDescending(tier => tier.HoursBefore).ToList();
        foreach (RefundTier tier in byHoursBeforeDescending)
        {
            if (timeToDeparture >= TimeSpan.FromHours(tier.HoursBefore))
            {
                return tier.RefundPercent;
            }
        }

        return 0;
    }

    /// <summary>The booking's total times the share, rounded down to the whole rupee.</summary>
    public static long CalculateAmount(long totalAmount, int refundPercent)
    {
        return totalAmount * refundPercent / FullRefundPercent;
    }

    /// <summary>Whether the schedule is usable: at least one tier, every share 0 to 100, no negative hours.</summary>
    public static bool IsValidSchedule(IReadOnlyList<RefundTier> tiers)
    {
        if (tiers.Count == 0)
        {
            return false;
        }

        foreach (RefundTier tier in tiers)
        {
            bool isShareValid = tier.RefundPercent >= 0 && tier.RefundPercent <= FullRefundPercent;
            if (!isShareValid || tier.HoursBefore < 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Whether a percentage is a share of money: a whole number 0 to 100.</summary>
    public static bool IsValidPercent(int? refundPercent)
    {
        if (refundPercent is null)
        {
            return false;
        }

        return refundPercent.Value >= 0 && refundPercent.Value <= FullRefundPercent;
    }

    /// <summary>The share an admin's force-cancel refunds when the admin names none.</summary>
    public static int DefaultForceCancelPercent()
    {
        return FullRefundPercent;
    }

    /// <summary>The refund key of a cancellation's refund; Payment refunds once per key.</summary>
    public static string CancellationKey(Guid bookingId)
    {
        return $"{bookingId:D}:cancel";
    }

    /// <summary>The refund key of a payment Booking could not use; Payment refunds once per key.</summary>
    public static string PaymentKey(Guid bookingId)
    {
        return $"{bookingId:D}:payment";
    }
}
