using Dapper;
using Npgsql;

namespace RouteGo.Booking.Api.Tests.Support;

/// <summary>Reads rows straight from the test database, bypassing the app, so a test asserts on what was really recorded.</summary>
public static class TestQueries
{
    /// <summary>Every row the query returns, mapped by column name to <typeparamref name="T"/>'s properties.</summary>
    public static async Task<IReadOnlyList<T>> QueryAsync<T>(DatabaseFixture database, string sql, object? parameters = null)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        IEnumerable<T> rows = await connection.QueryAsync<T>(sql, parameters);
        return rows.ToList();
    }

    /// <summary>The one row the query returns.</summary>
    public static async Task<T> QuerySingleAsync<T>(DatabaseFixture database, string sql, object? parameters = null)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        return await connection.QuerySingleAsync<T>(sql, parameters);
    }
}
