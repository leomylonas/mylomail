using Hangfire;
using Hangfire.Common;
using Hangfire.States;

namespace MyloMail.Api.Tests.Fakes;

/// <summary>
/// Records enqueued jobs instead of running them.
/// </summary>
/// <remarks>
/// Tests drive services directly, so a real Hangfire client buys nothing and costs a great
/// deal: <c>AddHangfire</c> installs process-wide state, and once one test's container is
/// disposed the next test's job creation fails on a dead logger factory. That surfaced as an
/// intermittent failure in an unrelated test rather than as anything pointing at Hangfire.
/// </remarks>
internal sealed class RecordingJobClient : IBackgroundJobClient
{
	public List<Job> Created { get; } = [];

	public string Create(Job job, IState state)
	{
		Created.Add(job);
		return Guid.NewGuid().ToString();
	}

	public bool ChangeState(string jobId, IState state, string? expectedState) => true;
}
