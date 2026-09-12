using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using MyloMail.Api.Credentials;
using MyloMail.Api.Providers;

namespace MyloMail.Api.Errors;

/// <summary>Builds and transports the one RFC 7807 error shape used by REST and SignalR.</summary>
public static class MutationProblemTransport
{
	private static readonly JsonSerializerOptions HubJson = new(JsonSerializerDefaults.Web);

	public static MutationProblemDetails Create(
		ErrorCategory category,
		string? detail = null,
		int? status = null,
		string? title = null,
		string? providerCode = null
	) =>
		new()
		{
			Category = category,
			Detail = detail,
			Status = status ?? StatusFor(category),
			Title = title ?? TitleFor(category),
			ProviderCode = providerCode,
		};

	public static MutationProblemDetails FromException(Exception exception) =>
		exception switch
		{
			MutationHubException hub => hub.Problem,
			ProviderAuthenticationException ex => Create(ErrorCategory.Auth, ex.Message),
			ProviderThrottledException ex => WithRetryAfter(Create(ErrorCategory.RateLimit, ex.Message), ex.RetryAfter),
			ProviderConflictException ex => Create(ErrorCategory.Conflict, ex.Message),
			DbUpdateConcurrencyException ex => Create(ErrorCategory.Conflict, ex.Message),
			ProviderContactRejectedException ex => Create(ErrorCategory.ProviderRejected, ex.Message),
			ProviderNotConfiguredException ex => Create(
				ErrorCategory.Unknown,
				ex.Message,
				StatusCodes.Status501NotImplemented,
				"Provider not configured"
			),
			CredentialStoreUnavailableException ex => Create(
				ErrorCategory.Unknown,
				ex.Message,
				StatusCodes.Status503ServiceUnavailable,
				"Credential store unavailable"
			),
			HttpRequestException ex => Create(ErrorCategory.Network, ex.Message),
			SocketException ex => Create(ErrorCategory.Network, ex.Message),
			IOException ex => Create(ErrorCategory.Network, ex.Message),
			TimeoutException ex => Create(ErrorCategory.Network, ex.Message),
			HubException ex => Create(ErrorCategory.Validation, ex.Message),
			ArgumentException ex => Create(ErrorCategory.Validation, ex.Message),
			KeyNotFoundException ex => Create(ErrorCategory.Validation, ex.Message),
			InvalidOperationException ex => Create(ErrorCategory.Validation, ex.Message),
			_ => Create(ErrorCategory.Unknown),
		};

	public static MutationProblemDetails FromProviderException(Exception exception)
	{
		var problem = FromException(exception);
		return problem.Category is ErrorCategory.Unknown or ErrorCategory.Validation
			? Create(ErrorCategory.ProviderRejected, exception.Message)
			: problem;
	}

	public static MutationProblemDetails FromStatus(
		int status,
		string? detail = null,
		string? title = null
	) => Create(CategoryFor(status), detail, status, title);

	public static string Serialize(MutationProblemDetails problem) =>
		JsonSerializer.Serialize(problem, HubJson);

	public static async Task WriteExceptionAsync(HttpContext context)
	{
		var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;
		if (exception is null)
		{
			return;
		}

		context.RequestServices
			.GetRequiredService<ILoggerFactory>()
			.CreateLogger("MyloMail.Api.Errors")
			.LogError(exception, "Unhandled API request failed.");
		var problem = FromException(exception);
		context.Response.StatusCode = problem.Status ?? StatusCodes.Status500InternalServerError;
		context.Response.ContentType = "application/problem+json";
		await context.Response.WriteAsJsonAsync(problem, HubJson, context.RequestAborted);
	}

	public static async Task WriteStatusAsync(StatusCodeContext statusContext)
	{
		var response = statusContext.HttpContext.Response;
		if (response.HasStarted || response.ContentLength is > 0 || !string.IsNullOrEmpty(response.ContentType))
		{
			return;
		}

		var problem = FromStatus(response.StatusCode);
		response.ContentType = "application/problem+json";
		await response.WriteAsJsonAsync(problem, HubJson, statusContext.HttpContext.RequestAborted);
	}

	private static MutationProblemDetails WithRetryAfter(MutationProblemDetails problem, TimeSpan retryAfter)
	{
		problem.Extensions["retryAfterSeconds"] = Math.Max(0, (int)Math.Ceiling(retryAfter.TotalSeconds));
		return problem;
	}

	private static ErrorCategory CategoryFor(int status) =>
		status switch
		{
			StatusCodes.Status401Unauthorized or StatusCodes.Status403Forbidden => ErrorCategory.Auth,
			StatusCodes.Status409Conflict => ErrorCategory.Conflict,
			StatusCodes.Status429TooManyRequests => ErrorCategory.RateLimit,
			StatusCodes.Status502BadGateway
				or StatusCodes.Status503ServiceUnavailable
				or StatusCodes.Status504GatewayTimeout => ErrorCategory.Network,
			>= 400 and < 500 => ErrorCategory.Validation,
			_ => ErrorCategory.Unknown,
		};

	private static int StatusFor(ErrorCategory category) =>
		category switch
		{
			ErrorCategory.Network => StatusCodes.Status503ServiceUnavailable,
			ErrorCategory.Auth => StatusCodes.Status401Unauthorized,
			ErrorCategory.RateLimit => StatusCodes.Status429TooManyRequests,
			ErrorCategory.Validation => StatusCodes.Status400BadRequest,
			ErrorCategory.ProviderRejected => StatusCodes.Status422UnprocessableEntity,
			ErrorCategory.Conflict => StatusCodes.Status409Conflict,
			_ => StatusCodes.Status500InternalServerError,
		};

	private static string TitleFor(ErrorCategory category) =>
		category switch
		{
			ErrorCategory.Network => "Network unavailable",
			ErrorCategory.Auth => "Authentication required",
			ErrorCategory.RateLimit => "Provider rate limit",
			ErrorCategory.Validation => "Invalid request",
			ErrorCategory.ProviderRejected => "Provider rejected the request",
			ErrorCategory.Conflict => "Conflicting change",
			_ => "Unexpected error",
		};
}

/// <summary>A SignalR error whose message is the same JSON object REST returns.</summary>
public sealed class MutationHubException : HubException
{
	public MutationHubException(ErrorCategory category, string detail, string? title = null)
		: this(MutationProblemTransport.Create(category, detail, title: title)) { }

	public MutationHubException(Exception exception)
		: this(MutationProblemTransport.FromException(exception)) { }

	public static MutationHubException FromProvider(Exception exception) =>
		new(MutationProblemTransport.FromProviderException(exception));

	public MutationHubException(MutationProblemDetails problem)
		: base(MutationProblemTransport.Serialize(problem))
	{
		Problem = problem;
	}

	public MutationProblemDetails Problem { get; }
}

/// <summary>Last-resort boundary: no hub invocation can leak a flat string or provider exception.</summary>
public sealed class MutationProblemHubFilter(ILogger<MutationProblemHubFilter> logger) : IHubFilter
{
	public async ValueTask<object?> InvokeMethodAsync(
		HubInvocationContext invocationContext,
		Func<HubInvocationContext, ValueTask<object?>> next
	)
	{
		try
		{
			return await next(invocationContext);
		}
		catch (MutationHubException)
		{
			throw;
		}
		catch (OperationCanceledException) when (invocationContext.Context.ConnectionAborted.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception exception)
		{
			var problem = MutationProblemTransport.FromException(exception);
			if (problem.Category == ErrorCategory.Unknown)
			{
				logger.LogError(exception, "Unhandled SignalR invocation failed.");
			}
			throw new MutationHubException(problem);
		}
	}
}

/// <summary>Normalises controller and framework-generated non-success results.</summary>
public sealed class MutationProblemResultFilter : IAsyncAlwaysRunResultFilter
{
	public Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
	{
		var status = context.Result switch
		{
			ObjectResult result => result.StatusCode ?? (result.Value as ProblemDetails)?.Status,
			StatusCodeResult result => result.StatusCode,
			_ => null,
		};
		if (status is not >= 400)
		{
			return next();
		}

		if (context.Result is ObjectResult { Value: MutationProblemDetails mutation })
		{
			mutation.Status ??= status;
			context.Result = new ObjectResult(mutation)
			{
				StatusCode = status,
				ContentTypes = { "application/problem+json" },
			};
			return next();
		}

		var source = (context.Result as ObjectResult)?.Value;
		var standard = source as ProblemDetails;
		var problem = MutationProblemTransport.FromStatus(
			status.Value,
			standard?.Detail ?? source as string,
			standard?.Title
		);
		if (standard is not null)
		{
			problem.Type = standard.Type;
			problem.Instance = standard.Instance;
			foreach (var extension in standard.Extensions)
			{
				problem.Extensions[extension.Key] = extension.Value;
			}
			if (standard is ValidationProblemDetails validation)
			{
				problem.Extensions["errors"] = validation.Errors;
			}
		}
		context.Result = new ObjectResult(problem)
		{
			StatusCode = status,
			ContentTypes = { "application/problem+json" },
		};
		return next();
	}
}

public static class MutationProblemControllerExtensions
{
	public static ObjectResult MutationProblem(
		this ControllerBase controller,
		string? detail = null,
		string? instance = null,
		int? statusCode = null,
		string? title = null,
		string? type = null,
		ErrorCategory category = ErrorCategory.Validation
	)
	{
		var problem = MutationProblemTransport.Create(
			category,
			detail,
			statusCode,
			title
		);
		problem.Instance = instance;
		problem.Type = type;
		return new ObjectResult(problem) { StatusCode = problem.Status };
	}
}
