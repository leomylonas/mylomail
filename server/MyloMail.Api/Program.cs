using Hangfire;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using MyloMail.Api.Content;
using MyloMail.Api.Credentials;
using MyloMail.Api.Errors;
using MyloMail.Api.FaultInjection;
using MyloMail.Api.Hubs;
using MyloMail.Api.Logging;
using MyloMail.Api.Persistence;
using MyloMail.Api.Scheduling;
using MyloMail.Api.Security;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:0");

// Resolved before the database path is known, and therefore before anything in AppSettings
// can be read (§15).
var dataDirectory = DataDirectory.Resolve(
	ProcessFaultInjector.DataDirectoryOverrideFromEnvironment()
		?? BootstrapConfig.Load().DataDirectoryOverride
);

// Async sink, and never message bodies/subjects/credentials — MessageId/AccountId/operation/
// exception only (§10). That discipline is enforced by what call sites choose to log, not by
// this policy; EmailMaskingDestructuringPolicy only covers the one identifying-field case §10
// calls out as worth partially keeping (an @-destructured Address).
builder.Host.UseSerilog(
	(context, services, configuration) =>
		configuration
			.MinimumLevel.Information()
			// EF Core's own query/migration diagnostics are noisy at Information (one line
			// per SQL statement) and add nothing over the exception/operation logging our own
			// code does — §10 wants MessageId/AccountId/operation/exception, not a SQL trace.
			.MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
			.MinimumLevel.Override("System", Serilog.Events.LogEventLevel.Warning)
			.Enrich.FromLogContext()
			.Enrich.WithThreadId()
			.Destructure.With<EmailMaskingDestructuringPolicy>()
			.WriteTo.Async(sink =>
				sink.File(
					Path.Combine(dataDirectory, "logs", "mylomail-.log"),
					rollingInterval: RollingInterval.Day,
					retainedFileCountLimit: 14,
					outputTemplate:
						"{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [{ThreadId}] {Message:lj}{NewLine}{Exception}"
				)
			)
);

if (ProcessFaultInjector.FromEnvironment() is { } processFaults)
{
	builder.Services.AddSingleton<IFaultInjector>(processFaults);
}

builder.Services.AddPersistence(dataDirectory);
builder.Services.AddSingleton<CredentialStoreSelector>(_ => new CredentialStoreSelector(dataDirectory));
builder.Services.AddScoped<ICredentialStore>(provider =>
	provider.GetRequiredService<CredentialStoreSelector>().Create(provider.GetRequiredService<MyloMailDbContext>()));
builder.Services.AddProviderClients(builder.Configuration);
builder.Services.AddMutations();
builder.Services.AddSync();
builder.Services.AddScheduling();
builder.Services.AddSchedulingWorkers();
builder.Services.AddControllers(options => options.Filters.Add<MutationProblemResultFilter>());
builder.Services.AddSignalR(options => options.AddFilter<MutationProblemHubFilter>());

// The real publisher replaces the no-op default only where a hub actually exists.
builder.Services.AddSingleton<IHubEvents, HubEvents>();

// Opt-in, default off, and deployment configuration rather than a user-facing app setting
// (§15) — like provider client registration above, it comes from appsettings.json or
// Telemetry__Enabled / Telemetry__OtelEndpoint-style environment variables, never from the
// database. No exporter is added at all when telemetry is off or no endpoint is configured, so
// a user who never opts in pays nothing for it.
var telemetryEnabled = builder.Configuration.GetValue<bool>("Telemetry:Enabled");
var otelEndpoint = builder.Configuration["Telemetry:OtelEndpoint"];
if (telemetryEnabled && !string.IsNullOrEmpty(otelEndpoint))
{
	builder.Services
		.AddOpenTelemetry()
		.ConfigureResource(resource => resource.AddService("MyloMail.Api"))
		.WithTracing(tracing =>
			tracing
				.AddAspNetCoreInstrumentation()
				.AddHttpClientInstrumentation()
				// No-PII discipline (§10): auto-instrumentation captures routes, status
				// codes and durations, never request/response bodies — the same rule
				// application logging follows for message addresses, subjects and bodies.
				.AddOtlpExporter(otlp => otlp.Endpoint = new Uri(otelEndpoint))
		);
}

var app = builder.Build();
app.UseExceptionHandler(exceptionApp => exceptionApp.Run(MutationProblemTransport.WriteExceptionAsync));
app.UseStatusCodePages(MutationProblemTransport.WriteStatusAsync);
app.UseLaunchToken();

// Method, path, status code and duration only — never headers or bodies (§10's no-PII rule,
// the same one AddOtlpExporter's own auto-instrumentation follows below).
app.UseSerilogRequestLogging();

var attachmentTemp = app.Services.GetRequiredService<AttachmentTempDirectory>();
app.Lifetime.ApplicationStopping.Register(attachmentTemp.Cleanup);

// Eagerly resolved so its OS network-availability subscription is live from startup, not from
// whenever the first minutely probe happens to construct it.
app.Services.GetRequiredService<ConnectivityMonitor>();

// The renderer is served from this origin so that one cookie authenticates every request it
// makes, including the WebSocket handshake. Serving it from file:// is what forced a token
// into the hub URL.
if (Environment.GetEnvironmentVariable("MYLOMAIL_RENDERER_PATH") is string rendererPath
	&& Directory.Exists(rendererPath))
{
	var files = new PhysicalFileProvider(Path.GetFullPath(rendererPath));
	app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
	app.UseStaticFiles(new StaticFileOptions { FileProvider = files });
}

app.MapControllers();
app.MapHub<MailHub>("/hub");

// Migration runs at startup, behind a VACUUM INTO backup, and surfaces failure rather than
// retrying (§9).
await using (var scope = app.Services.CreateAsyncScope())
{
	await scope.ServiceProvider.GetRequiredService<DatabaseBootstrapper>().MigrateAsync();
	try
	{
		await scope.ServiceProvider.GetRequiredService<CredentialStoreSelector>()
			.InitializeAsync(scope.ServiceProvider.GetRequiredService<MyloMailDbContext>(), CancellationToken.None);
	}
	catch (CredentialStoreUnavailableException)
	{
		Environment.ExitCode = CredentialStoreUnavailableException.ExitCode;
		return;
	}

	// With in-memory job storage this is the sole recovery mechanism, not a safety net
	// behind a durable queue (§6). It runs after migration and before any job can be
	// enqueued, so nothing outstanding is left unowned.
	await scope.ServiceProvider.GetRequiredService<StartupScheduler>().ScheduleAsync();

	// Must run after migration: on a fresh install AppSettings' table does not exist until
	// MigrateAsync creates it just above, so reading it any earlier throws before the
	// database is even there to read (§9).
	var context = scope.ServiceProvider.GetRequiredService<MyloMailDbContext>();
	var settings = await context.AppSettings.FirstOrDefaultAsync();
	// A missing row is equivalent to all-defaults (§9), and the default is true.
	if (settings?.AttachmentTempCleanupOnStartup ?? true)
	{
		attachmentTemp.Cleanup();
	}
}

// The low-frequency half of connectivity discovery (§3, §15) — the OS event handles an
// interface going up or down immediately; this catches what that can't, at the coarsest
// cadence Hangfire's recurring scheduler offers, which is exactly the "low-frequency" this
// signal calls for.
RecurringJob.AddOrUpdate<ConnectivityMonitor>(
	"connectivity-probe",
	monitor => monitor.ProbeAsync(default),
	Cron.Minutely
);

await app.StartAsync();
var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses;
var address = addresses?.SingleOrDefault() ?? throw new InvalidOperationException("Backend did not bind a loopback address.");
Console.Out.WriteLine($"MYLOMAIL_PORT={new Uri(address).Port}");
await app.WaitForShutdownAsync();
