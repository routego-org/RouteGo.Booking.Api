using System.Diagnostics;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Trace;
using Scalar.AspNetCore;
using RouteGo.Booking.Api.Infrastructure;
using RouteGo.Booking.Api.Observability;
// FILLED BY the sibling slice skill — the usings for what it wires below (e.g. using RouteGo.Booking.Api.Infrastructure.Pgsql;
// when the first data slice lands). Non-System usings only: System.* sort first (IDE0055).
using RouteGo.Booking.Api.Features.Bookings.ApplyPaymentFailed;
using RouteGo.Booking.Api.Features.Bookings.ApplyPaymentSucceeded;
using RouteGo.Booking.Api.Features.Bookings.ExpireBooking;
using RouteGo.Booking.Api.Features.Bookings.PublishBookingEvents;
using RouteGo.Booking.Api.Features.Cancellations.CancelTripBookings;
using RouteGo.Booking.Api.Features.Cancellations.CompleteRefund;
using RouteGo.Booking.Api.Features.TripSnapshots.CreateTripSnapshot;
using RouteGo.Booking.Api.Features.TripSnapshots.UpdateTripFares;
using RouteGo.Booking.Api.Infrastructure.Clients;
using RouteGo.Booking.Api.Infrastructure.Pgsql;
using RouteGo.Booking.Api.Infrastructure.Settings;

// Configuration reaches this app through ONE channel in every environment: environment variables,
// flat-named so there is no ':' / '__' separator to mistype (Infrastructure/ConfigKeys.cs). The
// deployed values come from the deployment template; the local ones from LocalEntryPoints, which
// MUST run before the builder is created — its environment-variable provider snapshots the process
// block once, so anything set after that line never reaches IConfiguration
// (references/configuration.md).
LocalEntryPoints.Apply();
// FILLED BY the sibling slice skill when the first data slice lands — BEFORE the builder, so nothing
// can query first: Dapper bakes its name mapping per (query, type) at first execution, so a late call
// is a silent no-op (skills-db-postgres #11/#25; its check_dapper_mapping.py D3 anchors on this line).
// The timeout is DERIVED from this API's caller deadline, with the derivation in a why-comment here:
//   DapperConfiguration.Apply(commandTimeout: TimeSpan.FromSeconds(...));
// 10 s: inside the caller's own deadline, so a query that runs past it has already failed upstream.
DapperConfiguration.Apply(commandTimeout: TimeSpan.FromSeconds(10));

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// DI scope validation, on in EVERY environment (skills-db-postgres #26). CreateBuilder turns
// these on in Development only, so a captive dependency throws on a laptop and goes unnoticed in
// production: a singleton that takes a scoped IWriterConnectionFactory or ICurrentActor resolves
// it from the ROOT scope and freezes the first request's actor for the life of the process, and
// every later user's writes are stamped with that identity. ValidateOnBuild catches the capture
// at startup; ValidateScopes catches a scoped service pulled from app.Services at runtime.
// A startup failure here is the finding, not a regression - read the exception, fix the capture.
builder.Host.UseDefaultServiceProvider((_, options) =>
{
    options.ValidateOnBuild = true;
    options.ValidateScopes = true;
});

// Fail at startup naming every missing key at once, rather than on whichever request first needs
// one. List only what the app cannot start without; a legitimately optional key is left out.
// FILLED BY the sibling slice skill as it introduces keys:
//   builder.Configuration.RequireKeys(ConfigKeys.BackendBaseUrl, ConfigKeys.ClientId);
builder.Configuration.RequireKeys(
    ConfigKeys.DbConnectionProfile, ConfigKeys.DbMaxPoolSize,
    ConfigKeys.IdentityJwksUrl, ConfigKeys.IdentityIssuer, ConfigKeys.IdentityAudience,
    ConfigKeys.SeatInventoryBaseUrl, ConfigKeys.PaymentBaseUrl, ConfigKeys.SettingsPath,
    ConfigKeys.BookingEventsTopicArn, ConfigKeys.AuditEventsTopicArn,
    ConfigKeys.PaymentQueueUrl, ConfigKeys.PaymentDeadLetterQueueUrl,
    ConfigKeys.InventoryQueueUrl, ConfigKeys.InventoryDeadLetterQueueUrl,
    ConfigKeys.FleetQueueUrl, ConfigKeys.FleetDeadLetterQueueUrl);

// Log levels, in code for the same reason every other setting is: one place, and no appsettings
// file to disagree with it. Everything defaults to Information; the framework's own request/routing
// chatter stays at Warning so the telemetry pipeline is not flooded, except in Development where it
// is the most useful thing in the console. A deployed run can still be turned up without a code
// change: Logging__LogLevel__Default is an ordinary environment variable on the function.
LogLevel frameworkLogLevel = LogLevel.Warning;
if (builder.Environment.IsDevelopment())
{
    frameworkLogLevel = LogLevel.Information;
}

builder.Logging.SetMinimumLevel(LogLevel.Information);
builder.Logging.AddFilter("Microsoft.AspNetCore", frameworkLogLevel);

// LMI hosting — no-op outside Lambda (references/lambda-hosting.md).
builder.Services.AddAWSLambdaHosting(LambdaEventSource.RestApi);

// Secrets: standard AWS Secrets Manager access (references/secrets.md). ONE fixed name per secret;
// the SOURCE is the environment — Development reads configuration, everything else Secrets Manager.
builder.Services.AddSingleton<SecretResolver>();

// Wire naming contract (references/host-components.md): camelCase properties are the web
// default; enums serialize as camelCase STRINGS (without this converter they serialize as
// numbers and every enum in every response breaks the authored contract). Same policy as
// Common/WireJson.cs, which covers HttpClient reads and hand-rolled serialization.
builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(
        new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));

// Observability: vendor-neutral OpenTelemetry (traces + logs) over OTLP — baked in
// (references/observability.md). Deployed Lambda -> ADOT Collector on localhost:4318; local dev ->
// Otel:Exporter:* from user-secrets; unset -> graceful no-op. Flush on shutdown is automatic.
builder.Services.AddObservability(
    builder.Configuration, builder.Environment, builder.Logging,
    configureTracing: tracing =>
    {
        tracing.AddAspNetCoreInstrumentation(o =>
        {
            o.RecordException = true;
            o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health");
        });
        tracing.AddProcessor<PiiRedactingProcessor>();
        // FILLED BY the sibling slice skill when the first data slice lands — the database driver's
        // spans, subscribed by SOURCE NAME so this file keeps no driver reference and the architecture
        // test's DB-driver rule holds (skills-db-postgres #20):
        //   tracing.AddSource("Npgsql");
        tracing.AddSource("Npgsql");
    });

// Development only: OTel records the request line and the response status of every outbound call
// but never the bodies, so a 4xx from a dependency reads as a bare status code in the dashboard.
// This puts the payloads on the enclosing span as an "http.payload" event — see
// HttpPayloadDiagnosticsHandler for what it declines to read and why successful responses are
// left out. ConfigureHttpClientDefaults reaches every IHttpClientFactory client at once, so no
// individual client registration has to know about it.
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddTransient<HttpPayloadDiagnosticsHandler>();
    builder.Services.ConfigureHttpClientDefaults(
        http => http.AddHttpMessageHandler<HttpPayloadDiagnosticsHandler>());
}

// Errors: RFC 7807 ProblemDetails + unhandled-exception handler (references/host-components.md).
builder.Services.AddProblemDetails(o => o.CustomizeProblemDetails =
    c => c.ProblemDetails.Extensions["traceId"] = Activity.Current?.TraceId.ToString());
builder.Services.AddExceptionHandler<UnhandledExceptionHandler>();

// Infrastructure ports — registered once. No per-slice registrations.
builder.Services.AddSingleton<IClock, SystemClock>();
// FILLED BY skills-aspnetcore-api-vertical-slice when the first data slice lands:
//   the reader/writer connection-port registrations (files copied from skills-db-postgres —
//   reader singleton; writer scoped, it carries the per-request actor) (+ any other ports).
// Database (skills-db-postgres, Setting up a project, step 2). The secret's NAME is a fixed platform
// constant; the SOURCE is the environment — Development reads user-secrets, anything else Secrets
// Manager through the execution role — and the one shared mapper turns its JSON into a connection string.
string? databaseSecretJson = await new SecretResolver(builder.Configuration, builder.Environment)
    .ResolveAsync(DbSecret.SecretId);
if (string.IsNullOrEmpty(databaseSecretJson))
{
    string databaseSecretHelp = $"grant the execution role secretsmanager:GetSecretValue on '{DbSecret.SecretId}'";
    if (builder.Environment.IsDevelopment())
    {
        databaseSecretHelp = $"set it once: dotnet user-secrets set \"{DbSecret.SecretId}\" '<the platform JSON>' --id routego-platform";
    }
    throw new InvalidOperationException($"No database secret — {databaseSecretHelp}.");
}
// The pool profile rides on the same seam: the deployment template names the Lambda execution model
// (DB_CONNECTION_PROFILE, DB_MAX_POOL_SIZE) and the profile turns that into pool settings plus whether a
// dropped connection also clears the idle pool (Infrastructure/Pgsql/ConnectionProfile.cs).
var databaseProfile = ConnectionProfile.FromConfiguration(builder.Configuration);
string databaseConnectionString = databaseProfile.Apply(DbSecretValue.ToConnectionString(databaseSecretJson));
// ActorClaimOptions defaults match the RouteGo token; builder.Services.Configure<ActorClaimOptions>(...) overrides them.
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentActor, HttpContextActor>();
builder.Services.AddSingleton<IReaderConnectionFactory>(new ReaderConnectionFactory(databaseConnectionString));
builder.Services.AddScoped<IWriterConnectionFactory>(provider => new WriterConnectionFactory(
    databaseConnectionString, provider.GetRequiredService<ICurrentActor>(), databaseProfile.Retry));
// The queue workers and the event publisher run on their own, outside any request, so their writes are
// attributed to the system actor through a writer of their own. Request writes keep the scoped writer above,
// which stamps the signed-in person.
var backgroundWriterFactory = new WriterConnectionFactory(databaseConnectionString, SystemActor.Default, databaseProfile.Retry);

// Access tokens are issued by RouteGo.Identity.Api and checked here exactly as Identity checks its own:
// RS256 signature against Identity's published keys, issuer, audience, and exact expiry with no extra
// minutes of grace. Booking never holds a signing key. Identity is reached over RouteGo's private network
// on plain HTTP, so the key download cannot insist on HTTPS.
var signingKeys = new ConfigurationManager<OpenIdConnectConfiguration>(
    builder.Configuration[ConfigKeys.IdentityJwksUrl]!,
    new JwksConfigurationRetriever(),
    new HttpDocumentRetriever { RequireHttps = false });
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    options.MapInboundClaims = false;
    options.ConfigurationManager = signingKeys;
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidIssuer = builder.Configuration[ConfigKeys.IdentityIssuer],
        ValidAudience = builder.Configuration[ConfigKeys.IdentityAudience],
        ValidAlgorithms = new[] { SecurityAlgorithms.RsaSha256 },
        NameClaimType = TokenClaims.Email,
        RoleClaimType = TokenClaims.Role,
        ClockSkew = TimeSpan.Zero,
    };
    options.Events = new JwtBearerEvents
    {
        OnChallenge = AuthenticationProblems.WriteUnauthorizedAsync,
        OnForbidden = AuthenticationProblems.WriteForbiddenAsync,
    };
});
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(Policies.Customer, policy => policy.RequireRole(Policies.Customer));
    options.AddPolicy(Policies.Operator, policy => policy.RequireRole(Policies.Operator));
    options.AddPolicy(Policies.Admin, policy => policy.RequireRole(Policies.Admin));
    options.AddPolicy(Policies.CustomerOrAdmin, policy => policy.RequireRole(Policies.Customer, Policies.Admin));
});

// Validation errors name fields as the request schema does (camelCase), so a client can highlight them.
ValidatorOptions.Global.PropertyNameResolver = (_, member, _) => JsonNamingPolicy.CamelCase.ConvertName(member!.Name);

// The internal key is a secret, read once here; without it Booking cannot call Seat Inventory or Payment
// and refuses to start rather than start and fail every booking.
string? internalKey = await new SecretResolver(builder.Configuration, builder.Environment)
    .ResolveAsync(InternalKeySecret.SecretId);
if (string.IsNullOrEmpty(internalKey))
{
    string internalKeyHelp = $"grant the execution role secretsmanager:GetSecretValue on '{InternalKeySecret.SecretId}'";
    if (builder.Environment.IsDevelopment())
    {
        internalKeyHelp = $"set it once: dotnet user-secrets set \"{InternalKeySecret.SecretId}\" '<the internal key>' --id routego-platform";
    }

    throw new InvalidOperationException($"No internal key — {internalKeyHelp}.");
}

// A booking request asks Seat Inventory and then Payment in turn inside the caller's own deadline (the
// gateway in front of the Portal closes at about 30 s), so each call gets 8 s: two slow answers still leave
// room to release the hold and answer the customer. The calls are never retried here: a hold, payment start,
// confirm and release are repeat-safe, but a retry past the caller's deadline serves nobody; a lost confirm
// is retried by its queue message instead.
var upstreamTimeout = TimeSpan.FromSeconds(8);
builder.Services.AddTransient(_ => new InternalKeyHandler(internalKey));
builder.Services.AddApiClient(builder.Configuration, HttpClients.SeatInventory, ConfigKeys.SeatInventoryBaseUrl)
    .ConfigureHttpClient(client => client.Timeout = upstreamTimeout)
    .AddHttpMessageHandler<InternalKeyHandler>();
builder.Services.AddApiClient(builder.Configuration, HttpClients.Payment, ConfigKeys.PaymentBaseUrl)
    .ConfigureHttpClient(client => client.Timeout = upstreamTimeout)
    .AddHttpMessageHandler<InternalKeyHandler>();
builder.Services.AddSingleton<ISeatInventoryClient>(provider => new SeatInventoryClient(
    provider.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClients.SeatInventory),
    provider.GetRequiredService<ILogger<SeatInventoryClient>>()));
builder.Services.AddSingleton<IPaymentClient>(provider => new PaymentClient(
    provider.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClients.Payment),
    provider.GetRequiredService<ILogger<PaymentClient>>()));

// The business settings: one snapshot read from SSM and refreshed every 60 seconds by its own background service.
builder.Services.AddSingleton(provider => new SsmBookingSettings(
    AwsClients.CreateSsm(builder.Configuration),
    builder.Configuration[ConfigKeys.SettingsPath]!,
    provider.GetRequiredService<ILogger<SsmBookingSettings>>()));
builder.Services.AddSingleton<IBookingSettings>(provider => provider.GetRequiredService<SsmBookingSettings>());
builder.Services.AddHostedService(provider => provider.GetRequiredService<SsmBookingSettings>());

// Queues and topics: the regional endpoint and the execution role when deployed, LocalStack with
// placeholder credentials when AwsServiceUrl is set.
builder.Services.AddSingleton(AwsClients.CreateSqs(builder.Configuration));
builder.Services.AddSingleton<IBookingEventPublisher>(new SnsBookingEventPublisher(
    AwsClients.CreateSns(builder.Configuration),
    builder.Configuration[ConfigKeys.BookingEventsTopicArn]!,
    builder.Configuration[ConfigKeys.AuditEventsTopicArn]!));

// WORKERS — the background services that run inside this API, on every running copy of it. The event
// publisher takes a database lock so only one copy sends at a time; the queue workers need none, because
// a queue hands each message to one copy only and the database makes a repeat harmless. Several slices can
// read one queue, so each queue service dispatches an event to the slice's worker that names its type.
builder.Services.AddHostedService(provider => new PublishBookingEventsService(
    backgroundWriterFactory,
    provider.GetRequiredService<IBookingEventPublisher>(),
    provider.GetRequiredService<IClock>(),
    provider.GetRequiredService<ILoggerFactory>()));
// The three queue services share one type, and AddHostedService(factory) adds a hosted service only when its
// type is not registered yet: used here, it would start the fleet reader and silently drop the other two.
// AddSingleton<IHostedService> registers each one.
builder.Services.AddSingleton<IHostedService>(provider => QueueServices.Create(
    provider,
    builder.Configuration[ConfigKeys.FleetQueueUrl]!,
    builder.Configuration[ConfigKeys.FleetDeadLetterQueueUrl]!,
    ProblemTypes.FleetEventNotApplied,
    new IEventWorker[]
    {
        new CreateTripSnapshotHandler(backgroundWriterFactory, provider.GetRequiredService<ILogger<CreateTripSnapshotHandler>>()),
        new UpdateTripFaresHandler(backgroundWriterFactory, provider.GetRequiredService<ILogger<UpdateTripFaresHandler>>()),
        new CancelTripBookingsHandler(
            backgroundWriterFactory, provider.GetRequiredService<IClock>(), provider.GetRequiredService<ILogger<CancelTripBookingsHandler>>()),
    }));
builder.Services.AddSingleton<IHostedService>(provider => QueueServices.Create(
    provider,
    builder.Configuration[ConfigKeys.PaymentQueueUrl]!,
    builder.Configuration[ConfigKeys.PaymentDeadLetterQueueUrl]!,
    ProblemTypes.PaymentEventNotApplied,
    new IEventWorker[]
    {
        new ApplyPaymentSucceededHandler(
            backgroundWriterFactory,
            provider.GetRequiredService<ISeatInventoryClient>(),
            provider.GetRequiredService<IClock>(),
            provider.GetRequiredService<ILogger<ApplyPaymentSucceededHandler>>()),
        new ApplyPaymentFailedHandler(
            backgroundWriterFactory, provider.GetRequiredService<IClock>(), provider.GetRequiredService<ILogger<ApplyPaymentFailedHandler>>()),
        new CompleteRefundHandler(
            backgroundWriterFactory, provider.GetRequiredService<IClock>(), provider.GetRequiredService<ILogger<CompleteRefundHandler>>()),
    }));
builder.Services.AddSingleton<IHostedService>(provider => QueueServices.Create(
    provider,
    builder.Configuration[ConfigKeys.InventoryQueueUrl]!,
    builder.Configuration[ConfigKeys.InventoryDeadLetterQueueUrl]!,
    ProblemTypes.SeatEventNotApplied,
    new IEventWorker[]
    {
        new ExpireBookingHandler(
            backgroundWriterFactory, provider.GetRequiredService<IClock>(), provider.GetRequiredService<ILogger<ExpireBookingHandler>>()),
    }));

// OpenAPI: one document per Feature Area, from the explicit list (references/host-components.md).
// new_slice.py appends an area string here when a NEW area's first slice lands.
string[] areas = { "Bookings", "Cancellations", "TripSnapshots" };
foreach (string area in areas)
{
    builder.Services.AddOpenApi(area, o =>
    {
        string areaName = area;
        o.ShouldInclude = api =>
            api.GroupName is null || string.Equals(api.GroupName, areaName, StringComparison.OrdinalIgnoreCase);
        o.AddOperationTransformer<ResponseHeadersTransformer>();
    });
}

WebApplication app = builder.Build();

app.UseMiddleware<RequestTelemetryMiddleware>(); // OUTERMOST — identity + request logging; it ENRICHES the instrumentation's span (references/observability.md)
app.UseMiddleware<CorrelationIdMiddleware>();    // the ID is on every log line, error and event the request causes
app.UseExceptionHandler();                       // unhandled -> RFC 7807 problem+json
app.UseMiddleware<ResponseMetadataMiddleware>(); // X-Trace-Id, Server-Timing, ... on every response
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapOpenApi();                                // /openapi/{area}.json per document
if (app.Environment.IsDevelopment())             // dev-only — the doc is not the published contract
{
    app.MapScalarApiReference(o =>
    {
        o.WithTitle("API Reference");
        for (int i = 0; i < areas.Length; i++)
        {
            o.AddDocument(areas[i], areas[i], $"/openapi/{areas[i]}.json", isDefault: i == 0);
        }
    });
    app.MapGet("/", () => Results.Redirect("/scalar")); // one URL to remember
}
app.MapGet("/health", () => Results.Ok("healthy"));            // liveness: answers whenever the process runs
app.MapGet("/health/ready", ReadinessCheck.CheckAsync);        // readiness: the database and the three event queues answer

app.Run();

public partial class Program { } // exposes Program to WebApplicationFactory<Program> in tests
