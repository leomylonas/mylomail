using MyloMail.Api.Sync;
using Xunit;

namespace MyloMail.Api.Tests.Sync;

public sealed class MailboxSyncGateTests
{
	[Fact]
	public void A_second_request_while_one_runs_does_not_start_another_but_makes_the_first_go_round_once_more()
	{
		var gate = new MailboxSyncGate();
		var mailbox = Guid.NewGuid();

		Assert.True(gate.TryEnter(mailbox));
		Assert.False(gate.TryEnter(mailbox));
		Assert.False(gate.TryEnter(mailbox));

		Assert.True(gate.ShouldRepeat(mailbox));
		Assert.False(gate.ShouldRepeat(mailbox));
		// Released: the next request starts a sync of its own.
		Assert.True(gate.TryEnter(mailbox));
	}

	[Fact]
	public void Mailboxes_do_not_hold_each_other_up_and_a_failed_pass_releases_its_mailbox()
	{
		var gate = new MailboxSyncGate();
		var first = Guid.NewGuid();

		Assert.True(gate.TryEnter(first));
		Assert.True(gate.TryEnter(Guid.NewGuid()));

		gate.Release(first);
		Assert.True(gate.TryEnter(first));
	}
}
