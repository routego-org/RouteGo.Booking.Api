namespace RouteGo.Booking.Api.Infrastructure.Clients;

/// <summary>
/// What another RouteGo service answered to one call, read into the few facts the clients need: the
/// status, the problem <c>code</c> of a refusal, and the body's members. No HTTP type leaves the client.
/// </summary>
public sealed class UpstreamReply
{
    private readonly JsonElement? _body;

    public UpstreamReply(UpstreamReach reach, int statusCode, JsonElement? body)
    {
        Reach = reach;
        StatusCode = statusCode;
        _body = body;
    }

    /// <summary>Whether the service answered.</summary>
    public UpstreamReach Reach { get; }

    /// <summary>The HTTP status; 0 when the service did not answer.</summary>
    public int StatusCode { get; }

    /// <summary>Whether the service answered with a success status.</summary>
    public bool IsSuccess
    {
        get
        {
            return Reach == UpstreamReach.Answered && StatusCode >= 200 && StatusCode < 300;
        }
    }

    /// <summary>The refusal's cataloged problem code, or an empty string when the body has none.</summary>
    public string ProblemCode
    {
        get
        {
            return ReadText("code") ?? string.Empty;
        }
    }

    /// <summary>The text member of the body, or null when absent or not text.</summary>
    public string? ReadText(string name)
    {
        if (_body is null || _body.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        bool hasMember = _body.Value.TryGetProperty(name, out JsonElement member);
        if (!hasMember || member.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return member.GetString();
    }

    /// <summary>The body's list of texts under this name, empty when absent.</summary>
    public IReadOnlyList<string> ReadTexts(string name)
    {
        var texts = new List<string>();
        if (_body is null || _body.Value.ValueKind != JsonValueKind.Object)
        {
            return texts;
        }

        bool hasMember = _body.Value.TryGetProperty(name, out JsonElement member);
        if (!hasMember || member.ValueKind != JsonValueKind.Array)
        {
            return texts;
        }

        foreach (JsonElement item in member.EnumerateArray())
        {
            string? text = item.GetString();
            if (text is not null)
            {
                texts.Add(text);
            }
        }

        return texts;
    }

    /// <summary>The body's list of seat moves under this name, empty when absent.</summary>
    public IReadOnlyList<SeatMove> ReadMoves(string name)
    {
        var moves = new List<SeatMove>();
        if (_body is null || _body.Value.ValueKind != JsonValueKind.Object)
        {
            return moves;
        }

        bool hasMember = _body.Value.TryGetProperty(name, out JsonElement member);
        if (!hasMember || member.ValueKind != JsonValueKind.Array)
        {
            return moves;
        }

        foreach (JsonElement item in member.EnumerateArray())
        {
            string? from = ReadMoveSeat(item, "fromSeatNo");
            string? to = ReadMoveSeat(item, "toSeatNo");
            if (from is not null && to is not null)
            {
                moves.Add(new SeatMove(from, to));
            }
        }

        return moves;
    }

    private static string? ReadMoveSeat(JsonElement item, string name)
    {
        bool hasMember = item.TryGetProperty(name, out JsonElement member);
        if (!hasMember || member.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return member.GetString();
    }
}
