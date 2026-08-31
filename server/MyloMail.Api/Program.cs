using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using MyloMail.Api.Credentials;
using MyloMail.Api.Persistence;
using MyloMail.Api.Scheduling;
using MyloMail.Api.Security;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:0");

// Resolved before the database path is known, and therefore before anything in AppSettings
// can be read (§15).
var dataDirectory = DataDirectory.Resolve(BootstrapConfig.Load().DataDirectoryOverride);
builder.Services.AddPersistence(dataDirectory);
builder.Services.AddSingleton<CredentialStoreSelector>(_ => new CredentialStoreSelector(dataDirectory));
builder.Services.AddScoped<ICredentialStore>(provider =>
	provider.GetRequiredService<CredentialStoreSelector>().Create(provider.GetRequiredService<MyloMailDbContext>()));
builder.Services.AddProviderClients(builder.Configuration);
builder.Services.AddMutations();
builder.Services.AddSync();
builder.Services.AddScheduling();
builder.Services.AddSchedulingWorkers();
builder.Services.AddControllers();

var app = builder.Build();
app.UseLaunchToken();
app.MapControllers();

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
}

await app.StartAsync();
var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses;
var address = addresses?.SingleOrDefault() ?? throw new InvalidOperationException("Backend did not bind a loopback address.");
Console.Out.WriteLine($"MYLOMAIL_PORT={new Uri(address).Port}");
await app.WaitForShutdownAsync();
