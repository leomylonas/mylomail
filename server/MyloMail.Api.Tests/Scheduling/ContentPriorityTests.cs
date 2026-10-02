using MyloMail.Api.Scheduling;
using Xunit;

namespace MyloMail.Api.Tests.Scheduling;

public sealed class ContentPriorityTests
{
	[Fact]
	public void The_most_recent_request_still_pending_comes_first()
	{
		var priority = new ContentPriority();
		var account = Guid.NewGuid();
		var (a, b, c) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
		priority.Add(account, [a, b, c]);

		Assert.Equal(c, priority.Next(account, new HashSet<Guid> { a, b, c }));
		Assert.Equal(b, priority.Next(account, new HashSet<Guid> { a, b }));
	}

	[Fact]
	public void Asking_again_moves_a_message_to_the_front()
	{
		var priority = new ContentPriority();
		var account = Guid.NewGuid();
		var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
		priority.Add(account, [a, b]);
		priority.Add(account, [a]);

		Assert.Equal(a, priority.Next(account, new HashSet<Guid> { a, b }));
	}

	[Fact]
	public void Requests_are_per_account_and_nothing_pending_means_no_priority()
	{
		var priority = new ContentPriority();
		var (one, two) = (Guid.NewGuid(), Guid.NewGuid());
		var message = Guid.NewGuid();
		priority.Add(one, [message]);

		Assert.Null(priority.Next(two, new HashSet<Guid> { message }));
		Assert.Null(priority.Next(one, new HashSet<Guid>()));
	}
}
