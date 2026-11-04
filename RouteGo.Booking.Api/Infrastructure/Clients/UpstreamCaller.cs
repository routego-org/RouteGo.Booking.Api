namespace RouteGo.Booking.Api.Infrastructure.Clients;

/// <summary>
/// Makes one POST to another RouteGo service and turns whatever happens into an
/// <see cref="UpstreamReply"/>: an answer, "could not be reached", or "did not answer in time". The
/// correlation ID goes on the request itself, never on the shared client's default headers.
/// </summary>
public static class UpstreamCaller
{
    /// <summary>Sends the body to the path and reads the answer; never throws for a network or timeout failure.</summary>
    public static async Task<UpstreamReply> PostAsync(
        HttpClient client, string path, object? body, string correlationId, ILogger logger, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, path);
        if (body is not null)
        {
            message.Content = JsonContent.Create(body, options: WireJson.Options);
        }

        message.Headers.Add(CorrelationIdMiddleware.HeaderName, correlationId);
        try
        {
            using HttpResponseMessage response = await client.SendAsync(message, cancellationToken);
            JsonElement? json = await ReadBodyAsync(response, cancellationToken);
            return new UpstreamReply(UpstreamReach.Answered, (int)response.StatusCode, json);
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "{Path} could not be reached", path);
            return new UpstreamReply(UpstreamReach.Down, 0, null);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("{Path} did not answer in time", path);
            return new UpstreamReply(UpstreamReach.TimedOut, 0, null);
        }
    }

    // A body that is not JSON (a gateway's HTML error page) is simply no body: the status still says what happened.
    private static async Task<JsonElement?> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        string text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
