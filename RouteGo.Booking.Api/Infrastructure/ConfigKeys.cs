namespace RouteGo.Booking.Api.Infrastructure;

/// <summary>
/// The name of every environment-supplied configuration value, spelled exactly once. The names are
/// deliberately FLAT: each maps 1:1 to an environment variable of the same spelling, so no ':' or
/// '__' separator can be mistyped between the deployment template, the pipeline, and the reader
/// here. LocalEntryPoints and every consumer go through these constants, so a rename is a compile
/// error rather than a value that silently arrives null (references/configuration.md).
/// </summary>
public static class ConfigKeys
{
    /// <summary>The AWS SDK's own region variable, and the ONLY region source the app reads.
    /// Lambda sets it; LocalEntryPoints seeds it on a laptop. There is deliberately no app-owned
    /// AWS:Region key that could disagree with the runtime's own value.</summary>
    public const string AwsRegion = "AWS_REGION";

    // ══════════════════════════════════════════════════════════════════════════
    // FILLED BY the sibling slice skill and by feature work: one const per value
    // that varies by environment — a base URL, a client id, a public origin.
    //
    //   public const string BackendBaseUrl = "BackendBaseUrl";
    //
    // Adding one is four more edits, none of them a new file: a local value in
    // LocalEntryPoints, a parameter + env var in the deployment template, a
    // pipeline variable, and a RequireKeys entry if the app cannot start without
    // it. NEVER add a configuration key to an appsettings*.json.
    //
    // Otel:Exporter:* and AWS:Profile do NOT belong here — see
    // references/configuration.md for why those two keep their nested names.
    // ══════════════════════════════════════════════════════════════════════════
    /// <summary>Which Lambda execution model this process runs on — Lambda (one invocation per process,
    /// frozen between them) or ManagedInstances (many concurrent invocations in one always-on process).
    /// It picks the database pool profile (Infrastructure/Pgsql/ConnectionProfile). Set by the deployment
    /// template, never sniffed from the runtime. Required.</summary>
    public const string DbConnectionProfile = "DB_CONNECTION_PROFILE";

    /// <summary>The most database connections one process may hold: 2-4 on Lambda (a handler's opens are
    /// sequential), at least the per-environment concurrency on ManagedInstances. Set by the deployment
    /// template beside DbConnectionProfile. Required.</summary>
    public const string DbMaxPoolSize = "DB_MAX_POOL_SIZE";

    /// <summary>The one AWS-compatible endpoint a developer machine names (LocalStack); absent when deployed.</summary>
    public const string AwsServiceUrl = "AwsServiceUrl";

    /// <summary>Where Identity publishes the keys that sign access tokens.</summary>
    public const string IdentityJwksUrl = "IdentityJwksUrl";

    /// <summary>The issuer an access token must name.</summary>
    public const string IdentityIssuer = "IdentityIssuer";

    /// <summary>The audience an access token must name.</summary>
    public const string IdentityAudience = "IdentityAudience";

    /// <summary>Seat Inventory's address, which Booking calls to hold, confirm, release and change seats.</summary>
    public const string SeatInventoryBaseUrl = "SeatInventoryBaseUrl";

    /// <summary>Payment's address, which Booking calls to start a payment.</summary>
    public const string PaymentBaseUrl = "PaymentBaseUrl";

    /// <summary>The SSM path whose parameters are Booking's business settings, ending in a slash.</summary>
    public const string SettingsPath = "SettingsPath";

    /// <summary>The topic Booking's booking events go to.</summary>
    public const string BookingEventsTopicArn = "BookingEventsTopicArn";

    /// <summary>The topic Booking's audit events go to.</summary>
    public const string AuditEventsTopicArn = "AuditEventsTopicArn";

    /// <summary>The queue of Payment's results and refund completions.</summary>
    public const string PaymentQueueUrl = "PaymentQueueUrl";

    /// <summary>Where a payment event that can never be applied is set aside.</summary>
    public const string PaymentDeadLetterQueueUrl = "PaymentDeadLetterQueueUrl";

    /// <summary>The queue of Seat Inventory's released seats.</summary>
    public const string InventoryQueueUrl = "InventoryQueueUrl";

    /// <summary>Where a seat event that can never be applied is set aside.</summary>
    public const string InventoryDeadLetterQueueUrl = "InventoryDeadLetterQueueUrl";

    /// <summary>The queue of Fleet's trip announcements, fare changes and cancellations.</summary>
    public const string FleetQueueUrl = "FleetQueueUrl";

    /// <summary>Where a Fleet event that can never be applied is set aside.</summary>
    public const string FleetDeadLetterQueueUrl = "FleetDeadLetterQueueUrl";
}
