using MyloMail.Api.Errors;
using MyloMail.Api.Providers;
using MyloMail.Api.Providers.Contracts;
using Xunit;

namespace MyloMail.Api.Tests.Conformance;

/// <summary>
/// The shared conformance suite: one set of assertions every provider must satisfy.
/// </summary>
/// <remarks>
/// This is a first-class artefact, not merely tests. It is what makes stage B an
/// abstraction proven against three providers rather than three ad-hoc integrations, and it
/// remains the regression net permanently (§16).
/// <para>
/// Tagged <c>Deep</c> as well as <c>Conformance</c>: the suite spawns processes and
/// exercises three providers across three IMAP capability tiers, so it belongs in CI and
/// <c>pnpm check:deep</c>, never in the inner loop.
/// </para>
/// </remarks>
[Trait("Category", "Conformance")]
[Trait("Category", "Deep")]
public abstract class MailProviderConformanceTests : IAsyncLifetime
{
	private IConformanceHarness? harness;

	protected IConformanceHarness Harness =>
		harness ?? throw new InvalidOperationException("harness unavailable");

	protected abstract Task<IConformanceHarness> CreateHarnessAsync();

	/// <summary>
	/// Non-null when this subject cannot run here — no credentials, no container. §11
	/// requires such a suite to skip rather than fail, so the same tests run safely in any
	/// environment with only whatever happens to be reachable.
	/// </summary>
	protected virtual string? SkipReason => null;

	public async Task InitializeAsync()
	{
		if (SkipReason is null)
		{
			harness = await CreateHarnessAsync();
		}
	}

	public async Task DisposeAsync()
	{
		if (harness is not null)
		{
			await harness.DisposeAsync();
		}
	}

	private void Available() => Skip.If(SkipReason is not null, SkipReason ?? string.Empty);

	/// <summary>
	/// A move must report where the message ended up, or say that it cannot.
	/// </summary>
	/// <remarks>
	/// An IMAP move changes the UID, so the occurrence id the caller held is dead the moment
	/// the move succeeds. A provider that returns a bare success has silently orphaned the
	/// occurrence. Either the destination identity comes back, or
	/// <see cref="OccurrenceChange.RequiresDestinationReconciliation"/> is set so the caller
	/// reconciles rather than guessing (§2).
	/// </remarks>
	/// <summary>
	/// Every provider reports an inbox.
	/// </summary>
	/// <remarks>
	/// Trivial-looking, and it caught a real failure: on an IMAP server whose personal
	/// namespace prefix is "INBOX.", enumerating that namespace returns the folders beneath
	/// the inbox and not the inbox itself. The account then synced perfectly and appeared to
	/// have no inbox — visible only on one of the three tiers, which is what the matrix is for.
	/// </remarks>
	[SkippableFact]
	public async Task Topology_includes_an_inbox()
	{
		Available();

		var topology = await Harness.Provider.SyncMailboxTopologyAsync(
			Harness.Account,
			null,
			default
		);

		Assert.Contains(
			topology.Upserted,
			mailbox => mailbox.SpecialUse == Api.Domain.SpecialUse.Inbox
		);
	}

	[SkippableFact]
	public async Task Move_reports_destination_identity_or_demands_reconciliation()
	{
		Available();
		var occurrence = await Harness.SeedMessageAsync(Harness.Source);

		var result = await Harness.Provider.MoveMessagesAsync(
			Harness.Account,
			[occurrence],
			Harness.Destination,
			CancellationToken.None
		);

		var item = Assert.Single(result.Items);
		Assert.True(item.Succeeded, "seeded message should move successfully");

		var removal = Assert.Single(item.OccurrenceChanges, c => c.Removed);
		Assert.Equal(Harness.Source.Id, removal.MailboxId);

		var addition = Assert.Single(item.OccurrenceChanges, c => !c.Removed);
		Assert.Equal(Harness.Destination.Id, addition.MailboxId);

		// Driven by the response, not by the declaration. A provider that advertises UIDPLUS
		// but receives no COPYUID for this particular command is exactly the case the
		// reconciliation signal exists for, and asserting against the capability would let
		// that through.
		if (addition.NewProviderOccurrenceId is null)
		{
			Assert.True(
				addition.RequiresDestinationReconciliation,
				"an addition with no provider id must demand reconciliation, not be left orphaned"
			);
		}
		else
		{
			Assert.False(addition.RequiresDestinationReconciliation);
		}

		if (Harness.Provider.Capabilities.ReportsDestinationIdOnMove)
		{
			Assert.False(
				string.IsNullOrEmpty(addition.NewProviderOccurrenceId),
				"provider claims to report destination ids, so the move must carry one"
			);
		}
	}

	/// <summary>
	/// The canonical message survives a move. Only the occurrence changes.
	/// </summary>
	[SkippableFact]
	public async Task Move_preserves_canonical_message_identity()
	{
		Available();
		var occurrence = await Harness.SeedMessageAsync(Harness.Source);

		var result = await Harness.Provider.MoveMessagesAsync(
			Harness.Account,
			[occurrence],
			Harness.Destination,
			CancellationToken.None
		);

		var item = Assert.Single(result.Items);
		Assert.Equal(occurrence.MessageId, item.MessageId);
	}

	/// <summary>
	/// An unusable cursor surfaces as <see cref="ProviderCursorInvalidException"/> and
	/// nothing else.
	/// </summary>
	/// <remarks>
	/// All three providers invalidate cursors and all three need the same response — discard
	/// it, mark the mailbox for triggered resynchronisation, re-establish a baseline. Raising
	/// one exception type is what lets that be handled once rather than three times, so a
	/// provider leaking its native error (a raw <c>404</c>, <c>410</c>, or a
	/// <c>UIDVALIDITY</c> mismatch) breaks recovery for every caller (§3).
	/// </remarks>
	[SkippableFact]
	public async Task Expired_cursor_surfaces_as_ProviderCursorInvalidException()
	{
		Available();
		Skip.If(
			!Harness.CanProduceExpiredCursor,
			"This live provider account cannot deterministically produce an expired cursor."
		);
		var expired = await Harness.ExpiredCursorAsync(Harness.Source);

		await Assert.ThrowsAsync<ProviderCursorInvalidException>(
			() =>
				Harness.Provider.SyncMailboxAsync(
					Harness.Account,
					Harness.Source,
					expired,
					continuation: null,
					CancellationToken.None
				)
		);
	}

	/// <summary>
	/// A valid cursor does not raise, and any cursor returned is of the provider's own kind.
	/// </summary>
	[SkippableFact]
	public async Task Sync_returns_a_cursor_of_the_declared_kind()
	{
		Available();
		var baseline = await Harness.BaselineCursorAsync(Harness.Source);

		var result = await Harness.Provider.SyncMailboxAsync(
			Harness.Account,
			Harness.Source,
			baseline,
			continuation: null,
			CancellationToken.None
		);

		if (result.NewCursor is not null)
		{
			Assert.Equal(baseline.Kind, result.NewCursor.Kind);
		}
	}

	/// <summary>
	/// A provider must not hand back a cursor covering changes it has not yet delivered.
	/// </summary>
	/// <remarks>
	/// The caller commits the cursor in the same transaction as the page. If the cursor
	/// already covers pages still to come, those pages are skipped — permanently, and with no
	/// error anywhere. Gmail reports a `historyId` for the whole list and Graph withholds the
	/// `deltaLink` until the walk ends, so neither can advance mid-walk; IMAP can, because
	/// `HighestKnownUid` is a high-water mark over what has already been returned (§1, §3).
	/// </remarks>
	[SkippableFact]
	public async Task Cursor_is_not_advanced_past_undelivered_changes()
	{
		Available();
		if (Harness.Provider.Capabilities.AdvancesCursorMidWalk)
		{
			return;
		}

		var baseline = await Harness.BaselineCursorAsync(Harness.Source);
		await Harness.SeedPageOverflowAsync(Harness.Source);

		var result = await Harness.Provider.SyncMailboxAsync(
			Harness.Account,
			Harness.Source,
			baseline,
			continuation: null,
			CancellationToken.None
		);

		Assert.True(result.HasMore, "the harness seeded more than one page");
		Assert.Null(result.NewCursor);
		Assert.NotNull(result.Continuation);
	}

	/// <summary>
	/// Batch results are per item. Partial success is the normal case, not an edge case.
	/// </summary>
	/// <remarks>
	/// Multi-select routinely spans hundreds of messages, and one bad reference must not
	/// discard the outcome of the rest. A provider that collapses a batch into one aggregate
	/// result forces the caller to retry work that already succeeded (§2).
	/// </remarks>
	[SkippableFact]
	public async Task Batch_results_are_per_item()
	{
		Available();
		var good = await Harness.SeedMessageAsync(Harness.Source);
		var bad = await Harness.UnresolvableOccurrenceAsync(Harness.Source);

		var result = await Harness.Provider.SetFlagsAsync(
			Harness.Account,
			[good, bad],
			new FlagUpdate(IsRead: true, IsFlagged: null),
			CancellationToken.None
		);

		Assert.Equal(2, result.Items.Count);

		var goodItem = Assert.Single(
			result.Items,
			i => i.MessageId == good.MessageId && i.MailboxId == good.MailboxId
		);
		Assert.True(goodItem.Succeeded);
		Assert.Null(goodItem.Problem);

		var badItem = Assert.Single(
			result.Items,
			i => i.MessageId == bad.MessageId && i.MailboxId == bad.MailboxId
		);
		Assert.False(badItem.Succeeded);
		Assert.NotNull(badItem.Problem);
	}

	/// <summary>
	/// Two occurrences of one message in one batch produce two distinguishable results.
	/// </summary>
	/// <remarks>
	/// <c>RemoveFromMailbox</c> is membership-scoped (§6), and under Gmail's label model one
	/// message genuinely belongs to several mailboxes (§1), so a batch can carry two refs
	/// sharing a <c>MessageId</c>. Keyed on the message alone the caller cannot tell which
	/// membership succeeded, and would either retry a removal that already happened or
	/// abandon one that did not.
	/// </remarks>
	[SkippableFact]
	public async Task Occurrences_of_one_message_are_reported_separately()
	{
		Available();
		if (!Harness.Provider.Capabilities.SupportsMultipleMailboxMembership)
		{
			return;
		}

		var (first, second) = await Harness.SeedSharedMessageAsync(
			Harness.Source,
			Harness.Destination
		);
		Assert.Equal(first.MessageId, second.MessageId);

		var result = await Harness.Provider.RemoveFromMailboxAsync(
			Harness.Account,
			[first],
			CancellationToken.None
		);

		var item = Assert.Single(result.Items);
		Assert.Equal(first.MailboxId, item.MailboxId);
		Assert.NotEqual(second.MailboxId, item.MailboxId);

		// The untouched membership survives: removing one label is not removing the message.
		var stillThere = await Harness.Provider.SetFlagsAsync(
			Harness.Account,
			[second],
			new FlagUpdate(IsRead: true, IsFlagged: null),
			CancellationToken.None
		);
		Assert.True(
			Assert.Single(stillThere.Items).Succeeded,
			"removing one occurrence must not remove the other"
		);
	}

	/// <summary>
	/// A failed item carries a categorised problem, never a bare exception or flat string.
	/// </summary>
	/// <remarks>
	/// <see cref="ErrorCategory"/> drives UI behaviour uniformly, so an uncategorised failure
	/// cannot be rendered correctly. <see cref="ErrorCategory.Unknown"/> is a legitimate
	/// answer; not answering is not (§15).
	/// </remarks>
	[SkippableFact]
	public async Task Failed_batch_items_carry_a_categorised_problem()
	{
		Available();
		var bad = await Harness.UnresolvableOccurrenceAsync(Harness.Source);

		var result = await Harness.Provider.SetFlagsAsync(
			Harness.Account,
			[bad],
			new FlagUpdate(IsRead: true, IsFlagged: null),
			CancellationToken.None
		);

		var item = Assert.Single(result.Items);
		Assert.False(item.Succeeded);
		var problem = Assert.IsType<MutationProblemDetails>(item.Problem);
		Assert.True(
			Enum.IsDefined(problem.Category),
			"every failure maps into the shared error taxonomy"
		);
	}

	/// <summary>
	/// A partial flag update leaves untouched flags alone.
	/// </summary>
	/// <remarks>
	/// A mixed multi-select must not clobber flags the user did not touch, which is why each
	/// field is nullable. A provider that treats the update as absolute-per-message will
	/// silently unflag messages on a mark-as-read (§2).
	/// </remarks>
	[SkippableFact]
	public async Task Null_flag_fields_are_left_unchanged()
	{
		Available();
		var occurrence = await Harness.SeedMessageAsync(Harness.Source);
		var baseline = await Harness.BaselineCursorAsync(Harness.Source);

		await Harness.Provider.SetFlagsAsync(
			Harness.Account,
			[occurrence],
			new FlagUpdate(IsRead: null, IsFlagged: true),
			CancellationToken.None
		);
		await Harness.Provider.SetFlagsAsync(
			Harness.Account,
			[occurrence],
			new FlagUpdate(IsRead: true, IsFlagged: null),
			CancellationToken.None
		);

		var sync = await Harness.Provider.SyncMailboxAsync(
			Harness.Account,
			Harness.Source,
			baseline,
			continuation: null,
			CancellationToken.None
		);

		var message = Assert.Single(
			sync.Upserted,
			m => m.Occurrences.Any(o => o.ProviderOccurrenceId == occurrence.ProviderOccurrenceId)
		);
		Assert.True(message.IsRead, "the second update set read");
		Assert.True(message.IsFlagged, "the second update passed null for flagged, so it must survive");
	}

	/// <summary>
	/// Capability negotiation is honest: the tier a provider declares matches what it does.
	/// </summary>
	/// <remarks>
	/// Capabilities drive recovery policy and the IMAP sync tier. A provider that overstates
	/// one causes the caller to skip reconciliation it actually needs, and the resulting gap
	/// is silent (§3).
	/// </remarks>
	[SkippableFact]
	public void Capabilities_are_internally_consistent()
	{
		Available();
		var capabilities = Harness.Provider.Capabilities;

		Assert.Equal(Harness.Provider.Type, capabilities.Type);

		if (Harness.Provider.Type == Api.Domain.ProviderType.Imap)
		{
			Assert.NotEqual(ImapCapabilityTier.NotApplicable, capabilities.ImapTier);
			Assert.Equal(ChangeStreamScope.Mailbox, capabilities.ChangeStreamScope);
		}
		else
		{
			Assert.Equal(ImapCapabilityTier.NotApplicable, capabilities.ImapTier);
		}

		// Only QRESYNC reports expunges incrementally; the weaker tiers require UID-set
		// reconciliation, and claiming otherwise means expunges are silently missed (§3).
		if (capabilities.ImapTier is ImapCapabilityTier.Basic or ImapCapabilityTier.CondStore)
		{
			Assert.False(capabilities.ReportsExpungesIncrementally);
		}

		// The weakest tier has no incremental flag mechanism at all — flags come from a
		// cadenced scan over known UIDs.
		if (capabilities.ImapTier == ImapCapabilityTier.Basic)
		{
			Assert.False(capabilities.SupportsIncrementalFlagChanges);
		}
	}

	/// <summary>
	/// Gmail's change stream is account-scoped; Graph's and IMAP's are mailbox-scoped.
	/// Per-label Gmail cursors would be fiction and would race between label jobs (§1, §3).
	/// </summary>
	[SkippableFact]
	public void Change_stream_scope_matches_the_provider()
	{
		Available();
		var expected =
			Harness.Provider.Type == Api.Domain.ProviderType.Gmail
				? ChangeStreamScope.Account
				: ChangeStreamScope.Mailbox;

		Assert.Equal(expected, Harness.Provider.Capabilities.ChangeStreamScope);
	}

	/// <summary>
	/// Deleting a Gmail label leaves the messages in All Mail; deleting an IMAP folder or a
	/// Graph mail folder destroys them. The UI states the actual outcome, so the capability
	/// must be reported accurately rather than defaulted (§2).
	/// </summary>
	[SkippableFact]
	public void Mailbox_deletion_semantics_are_declared()
	{
		Available();
		var expected = Harness.Provider.Type != Api.Domain.ProviderType.Gmail;

		Assert.Equal(expected, Harness.Provider.Capabilities.DeletingMailboxDeletesMessages);
	}
}
