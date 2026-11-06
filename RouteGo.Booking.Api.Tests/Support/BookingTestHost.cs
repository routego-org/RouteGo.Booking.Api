using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RouteGo.Booking.Api.Infrastructure.Clients;

namespace RouteGo.Booking.Api.Tests.Support;

/// <summary>
/// The real host over the test database, trusting the test signing key so slice tests can call it
/// with tokens from <see cref="TestAccessTokens"/>, with Seat Inventory, Payment and the settings
/// replaced by the fakes a test passes in. The queue workers, the settings refresher and the event
/// publisher are left out: an HTTP test asserts on the events a request recorded, and a publisher
/// running underneath it would race the assertion. Their own tests drive them directly.
/// </summary>
internal static class BookingTestHost
{
    // The same value in every host of a run: the environment block is process-global.
    private const string TestInternalKey = "test-internal-key";

    /// <summary>
    /// Supplies the internal key the host refuses to start without. Any test that builds the host
    /// WITHOUT going through <see cref="For"/> must call this first: otherwise it only passes when an
    /// earlier test in the run happened to set the (process-global) variable, or on a laptop whose
    /// user-secrets hold the real key — and fails on a clean CI runner.
    /// </summary>
    public static void UseTestInternalKey()
    {
        Environment.SetEnvironmentVariable(InternalKeySecret.SecretId, TestInternalKey);
    }

    public static ApiFactory For(DatabaseFixture database, params Action<IServiceCollection>[] overrides)
    {
        UseTestInternalKey();
        var all = new List<Action<IServiceCollection>> { TestAccessTokens.TrustTestSigningKey, RemoveBackgroundWork };
        all.AddRange(overrides);
        return TestHost.For(database, all.ToArray());
    }

    /// <summary>A host override that replaces the clock, Seat Inventory, Payment and the settings with test doubles.</summary>
    public static Action<IServiceCollection> Doubles(
        IClock clock, ISeatInventoryClient seatInventory, IPaymentClient payment, IBookingSettings settings)
    {
        return services =>
        {
            services.AddSingleton(clock);
            services.AddSingleton(seatInventory);
            services.AddSingleton(payment);
            services.AddSingleton(settings);
        };
    }

    private static void RemoveBackgroundWork(IServiceCollection services)
    {
        var backgroundWork = services.Where(IsBackgroundWork).ToList();
        foreach (ServiceDescriptor descriptor in backgroundWork)
        {
            services.Remove(descriptor);
        }
    }

    // Program.cs registers every background worker through a factory, so they are the hosted services
    // with a factory; the framework's own hosted services are registered by type and stay.
    private static bool IsBackgroundWork(ServiceDescriptor descriptor)
    {
        if (descriptor.ServiceType != typeof(IHostedService))
        {
            return false;
        }

        return descriptor.ImplementationFactory != null;
    }
}
