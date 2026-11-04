using Amazon.SQS.Model;

namespace RouteGo.Booking.Api.Infrastructure;

/// <summary>
/// Readiness: healthy only when Booking can reach its database and can receive its events, so a
/// not-ready instance receives no traffic. Liveness is a separate, dependency-free answer.
/// </summary>
public static class ReadinessCheck
{
    /// <summary>Answers 200 when the database and the three event queues respond, otherwise 503 naming what did not.</summary>
    public static async Task<IResult> CheckAsync(
        IReaderConnectionFactory readerFactory, IAmazonSQS sqs, IConfiguration configuration, CancellationToken cancellationToken)
    {
        var failures = new List<string>();
        await CheckDatabaseAsync(readerFactory, failures, cancellationToken);
        await CheckQueueAsync(sqs, configuration[ConfigKeys.PaymentQueueUrl]!, "payment queue", failures, cancellationToken);
        await CheckQueueAsync(sqs, configuration[ConfigKeys.InventoryQueueUrl]!, "seat queue", failures, cancellationToken);
        await CheckQueueAsync(sqs, configuration[ConfigKeys.FleetQueueUrl]!, "trip queue", failures, cancellationToken);
        if (failures.Count == 0)
        {
            return Results.Ok("ready");
        }

        return Results.Json(new { status = "not ready", failures }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    private static async Task CheckDatabaseAsync(
        IReaderConnectionFactory readerFactory, List<string> failures, CancellationToken cancellationToken)
    {
        try
        {
            await using DbConnection connection = readerFactory.CreateReadConnection();
            await connection.OpenAsync(cancellationToken);
            await connection.ExecuteScalarAsync<int>(new CommandDefinition("SELECT 1", cancellationToken: cancellationToken));
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            failures.Add($"database: {exception.GetType().Name}");
        }
    }

    private static async Task CheckQueueAsync(
        IAmazonSQS sqs, string queueUrl, string name, List<string> failures, CancellationToken cancellationToken)
    {
        try
        {
            var request = new GetQueueAttributesRequest { QueueUrl = queueUrl, AttributeNames = new List<string> { "QueueArn" } };
            await sqs.GetQueueAttributesAsync(request, cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            failures.Add($"{name}: {exception.GetType().Name}");
        }
    }
}
