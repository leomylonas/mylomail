using MyloMail.Api.Scheduling;
using Xunit;

namespace MyloMail.Api.Tests.Scheduling;

public sealed class SingleFlightTests
{
	[Fact]
	public void A_second_caller_is_refused_while_the_first_runs_and_the_first_is_told_to_go_again()
	{
		var flight = new SingleFlight();
		var key = Guid.NewGuid();

		Assert.True(flight.TryEnter(key));
		Assert.False(flight.TryEnter(key));
		Assert.True(flight.Exit(key));
		Assert.True(flight.TryEnter(key));
	}

	[Fact]
	public void A_runner_nobody_was_refused_behind_finishes_without_a_rerun()
	{
		var flight = new SingleFlight();
		var key = Guid.NewGuid();

		Assert.True(flight.TryEnter(key));
		Assert.False(flight.Exit(key));
	}

	[Fact]
	public void Different_keys_do_not_block_each_other()
	{
		var flight = new SingleFlight();

		Assert.True(flight.TryEnter(Guid.NewGuid()));
		Assert.True(flight.TryEnter(Guid.NewGuid()));
	}
}
