using RouteGo.Booking.Api.Infrastructure.Pgsql;

namespace RouteGo.Booking.Api.Tests.Support;

/// <summary>
/// The real database connection factories over the test database, for tests that drive a handler
/// directly instead of going through the host. Writes are attributed to the system actor, as the
/// workers' are.
/// </summary>
public static class TestWriters
{
    /// <summary>A writer factory over the test database.</summary>
    public static IWriterConnectionFactory CreateWriter(DatabaseFixture database)
    {
        return new WriterConnectionFactory(database.ConnectionString, SystemActor.Default, ConnectionRetryOptions.ManagedInstances);
    }

    /// <summary>A reader factory over the test database.</summary>
    public static IReaderConnectionFactory CreateReader(DatabaseFixture database)
    {
        return new ReaderConnectionFactory(database.ConnectionString);
    }
}
