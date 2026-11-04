using Amazon.SimpleSystemsManagement.Model;

namespace RouteGo.Booking.Api.Infrastructure.Settings;

/// <summary>
/// The business settings, read from SSM Parameter Store under one path and re-read every 60 seconds,
/// so a change takes effect without a restart or a release. It holds one immutable snapshot, replaced
/// as a whole, so a reader never sees half of an update. A read that fails keeps the last good
/// snapshot, and a parameter nobody has set keeps the specs' default.
/// </summary>
public sealed class SsmBookingSettings : BackgroundService, IBookingSettings
{
    private static readonly TimeSpan _refreshEvery = TimeSpan.FromSeconds(60);

    private readonly IAmazonSimpleSystemsManagement _ssm;
    private readonly string _path;
    private readonly ILogger<SsmBookingSettings> _logger;
    private volatile BookingSettingsValues _current;

    public SsmBookingSettings(IAmazonSimpleSystemsManagement ssm, string path, ILogger<SsmBookingSettings> logger)
    {
        _ssm = ssm;
        _path = path;
        _logger = logger;
        _current = BookingSettingsValues.CreateDefault();
    }

    /// <inheritdoc />
    public BookingSettingsValues Current
    {
        get
        {
            return _current;
        }
    }

    /// <summary>Reads the parameters once, before the host starts serving, so the first request already sees them.</summary>
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await TryRefreshAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    /// <summary>Reads the parameters and replaces the snapshot; returns the problems found with unusable values.</summary>
    public async Task<IReadOnlyList<string>> RefreshAsync(CancellationToken cancellationToken)
    {
        Dictionary<string, string> parameters = await ReadParametersAsync(cancellationToken);
        var problems = new List<string>();
        _current = BookingSettingsReader.Read(_current, parameters, problems);
        return problems;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_refreshEvery);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await TryRefreshAsync(stoppingToken);
        }
    }

    private async Task TryRefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyList<string> problems = await RefreshAsync(cancellationToken);
            foreach (string problem in problems)
            {
                _logger.LogWarning("Booking setting under {Path}: {Problem}", _path, problem);
            }
        }
        // The settings store being unreachable is not a reason to stop booking: the last good settings stay in force.
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "Booking settings under {Path} could not be read; keeping the last good ones", _path);
        }
    }

    private async Task<Dictionary<string, string>> ReadParametersAsync(CancellationToken cancellationToken)
    {
        var found = new Dictionary<string, string>();
        string? nextToken = null;
        do
        {
            var request = new GetParametersByPathRequest { Path = _path, Recursive = false, NextToken = nextToken };
            GetParametersByPathResponse response = await _ssm.GetParametersByPathAsync(request, cancellationToken);
            foreach (Parameter parameter in response.Parameters)
            {
                string shortName = parameter.Name.Substring(parameter.Name.LastIndexOf('/') + 1);
                found[shortName] = parameter.Value;
            }

            nextToken = response.NextToken;
        }
        while (!string.IsNullOrEmpty(nextToken));

        return found;
    }
}
