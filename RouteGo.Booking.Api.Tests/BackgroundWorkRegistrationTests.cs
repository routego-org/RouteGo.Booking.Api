using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace RouteGo.Booking.Api.Tests;

/// <summary>
/// The background workers are registered through factories, and the three queue readers share one type.
/// <c>AddHostedService(factory)</c> skips a service whose type is already registered, so a reader added
/// that way is silently never started: payment results and seat releases then wait on their queues for
/// ever while every HTTP test stays green. This pins the count of registered workers.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class BackgroundWorkRegistrationTests
{
    // Settings refresher, event publisher, and the fleet, payment and inventory queue readers.
    private const int ExpectedWorkers = 5;

    private readonly DatabaseFixture _database;

    public BackgroundWorkRegistrationTests(DatabaseFixture database)
    {
        _database = database;
    }

    [Fact]
    public void Host_RegistersEveryBackgroundWorker_IncludingEachQueueReader()
    {
        // Arrange
        int registered = 0;
        void CountThenRemove(IServiceCollection services)
        {
            var workers = services.Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationFactory != null).ToList();
            registered = workers.Count;
            foreach (ServiceDescriptor worker in workers)
            {
                services.Remove(worker);
            }
        }

        // Act
        using ApiFactory factory = TestHost.For(_database, TestAccessTokens.TrustTestSigningKey, CountThenRemove);
        using HttpClient client = factory.CreateClient();

        // Assert
        Assert.Equal(ExpectedWorkers, registered);
    }
}
