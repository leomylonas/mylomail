using System.Text.Json;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;
using MyloMail.Api.Credentials;
using MyloMail.Api.Domain;
using MyloMail.Api.FaultInjection;
using MyloMail.Api.Hubs;
using MyloMail.Api.Persistence;
using MyloMail.Api.Providers;
using MyloMail.Api.Scheduling;

namespace MyloMail.Api.Contacts;

public sealed record ContactInput(
	Guid? ContactId,
	Guid AccountId,
	string DisplayName,
	IReadOnlyList<string> Emails,
	string? ExpectedRevision
);

public sealed record ContactSuggestionEntry(
	string DisplayName,
	IReadOnlyList<string> Emails
);

/// <summary>
/// Owns account-local contacts and their durable provider operations. Provider calls occur only
/// after the operation is synchronously marked dispatched. A missing response is reconciled by
/// observation and is never interpreted as remote failure.
/// </summary>
public sealed class ContactService(
	MyloMailDbContext context,
	IServiceProvider services,
	ContactGate gate,
	AccountGate providerGate,
	IHubEvents events,
	IFaultInjector faults,
	IBackgroundJobClient? jobs = null
)
{
	public async Task<IReadOnlyList<Contact>> ListAsync(Guid accountId, string? search, CancellationToken ct)
	{
		var query = context.Contacts.Include(c => c.Addresses)
			.Where(c => c.AccountId == accountId
				&& c.ProviderMissingSince == null
				&& (!context.ContactOperations.Any(operation =>
						operation.ContactId == c.Id
						&& operation.Kind == ContactOperationKind.Delete
						&& (operation.State == ContactOperationState.Pending
							|| operation.State == ContactOperationState.Dispatched
							|| operation.State == ContactOperationState.Ambiguous))
					|| context.ContactOperations.Any(operation =>
						operation.ContactId == c.Id
						&& operation.Kind == ContactOperationKind.Create
						&& (operation.State == ContactOperationState.Dispatched
							|| operation.State == ContactOperationState.Ambiguous))));
		if (!string.IsNullOrWhiteSpace(search))
		{
			var needle = Normalize(search);
			query = query.Where(c => c.DisplayName.ToLower().Contains(needle)
				|| c.Addresses.Any(a => a.NormalizedEmail.Contains(needle)));
		}
		return await query.OrderBy(c => c.DisplayName).ToListAsync(ct);
	}

	public async Task<IReadOnlyList<ContactSuggestionEntry>> ListSuggestionsAsync(
		Guid accountId,
		CancellationToken ct
	)
	{
		var contacts = await ListAsync(accountId, null, ct);
		var knownEmails = contacts
			.SelectMany(contact => contact.Addresses)
			.Select(address => address.NormalizedEmail)
			.ToHashSet(StringComparer.Ordinal);
		var result = contacts
			.Select(contact => new ContactSuggestionEntry(
				contact.DisplayName,
				contact.Addresses.Select(address => address.Email).ToArray()
			))
			.ToList();
		var suggestions = await context.ContactSuggestions
			.Where(suggestion => suggestion.AccountId == accountId)
			.OrderBy(suggestion => suggestion.DisplayName)
			.ThenBy(suggestion => suggestion.Email)
			.ToListAsync(ct);
		result.AddRange(suggestions
			.Where(suggestion => !knownEmails.Contains(suggestion.NormalizedEmail))
			.Select(suggestion => new ContactSuggestionEntry(
				string.IsNullOrWhiteSpace(suggestion.DisplayName)
					? suggestion.Email
					: suggestion.DisplayName,
				[suggestion.Email]
			)));
		return result;
	}

	public async Task<bool> RefreshAsync(Guid accountId, CancellationToken ct)
	{
		using var lease = await gate.EnterAsync(accountId, ct);
		var account = await context.Accounts.SingleOrDefaultAsync(a => a.Id == accountId, ct);
		if (account is null || account.ProviderType == ProviderType.Imap) return false;
		if (!account.IsEnabled
			|| !account.PollingEnabled
			|| account.AuthState == AuthState.NeedsReauth) return false;
		var providerDelay = providerGate.Delay(accountId);
		if (providerDelay > TimeSpan.Zero)
			throw new ProviderThrottledException(providerDelay, "Account is throttled.");

		ContactPullResult pull;
		try
		{
			pull = await ProviderFor(account).PullAsync(account, useCursor: true, ct);
		}
		catch (ProviderThrottledException ex)
		{
			providerGate.Throttle(account.Id, ex.RetryAfter);
			await Accounts.AccountDtoFactory.AnnounceStatusAsync(
				context,
				events,
				account,
				CancellationToken.None,
				providerGate
			);
			throw;
		}
		catch (ProviderNotConfiguredException)
		{
			return false;
		}
		catch (CredentialStoreUnavailableException ex)
		{
			await SetCredentialStoreUnavailableAsync(account, ex.Message);
			return true;
		}
		catch (ProviderAuthenticationException ex)
		{
			await PauseForAuthenticationAsync(account, ex.Message);
			return false;
		}
		await ClearCredentialStoreUnavailableAsync(account, ct);
		var remote = pull.Contacts;
		var local = await context.Contacts.Include(c => c.Addresses)
			.Where(c => c.AccountId == accountId).ToListAsync(ct);
		var unresolved = await context.ContactOperations
			.Where(x => x.State == ContactOperationState.Pending
				|| x.State == ContactOperationState.Dispatched
				|| x.State == ContactOperationState.Ambiguous)
			.Join(
				context.Contacts.Where(c => c.AccountId == accountId),
				operation => operation.ContactId,
				contact => contact.Id,
				(operation, _) => operation
			)
			.ToListAsync(ct);
		var visibleRemoteIds = remote.Where(item => item.Emails.Count > 0)
			.Select(item => item.ProviderContactId)
			.ToHashSet(StringComparer.Ordinal);
		var removedRemoteIds = pull.DeletedProviderContactIds
			.Concat(remote.Where(item => item.Emails.Count == 0)
				.SelectMany(item => new[] { item.ProviderContactId }
					.Concat(item.PreviousProviderContactIds ?? [])))
			.ToHashSet(StringComparer.Ordinal);
		var unresolvedCreates = unresolved.Where(x => x.Kind == ContactOperationKind.Create
			&& x.State is ContactOperationState.Dispatched or ContactOperationState.Ambiguous).ToArray();
		var providerOwners = local
			.Where(contact => contact.ProviderContactId is not null)
			.ToDictionary(
				contact => contact.ProviderContactId!,
				contact => contact.Id,
				StringComparer.Ordinal
			);
		var reservedRemoteIds = remote
			.Where(item => unresolvedCreates.Any(operation =>
				(!providerOwners.TryGetValue(item.ProviderContactId, out var ownerId)
					|| ownerId == operation.ContactId)
				&& Same(item, operation.DisplayName, Emails(operation))))
			.Select(item => item.ProviderContactId)
			.ToHashSet(StringComparer.Ordinal);
		var adoptedContactIds = new List<Guid>();
		foreach (var operation in unresolvedCreates)
		{
			var matches = remote.Where(item =>
				(!providerOwners.TryGetValue(item.ProviderContactId, out var ownerId)
					|| ownerId == operation.ContactId)
				&& Same(item, operation.DisplayName, Emails(operation))).ToArray();
			if (matches.Length != 1
				|| unresolvedCreates.Any(other => other.Id != operation.Id
					&& Same(matches[0], other.DisplayName, Emails(other)))) continue;
			var contact = local.SingleOrDefault(c => c.Id == operation.ContactId);
			if (contact is not null && contact.ProviderContactId is null)
			{
				await ResolveCreatedIdentityAsync(contact, operation, matches[0], ct);
				adoptedContactIds.Add(contact.Id);
			}
		}

		foreach (var item in remote.Where(x => x.Emails.Count > 0))
		{
			var contact = local.SingleOrDefault(c => c.ProviderContactId == item.ProviderContactId);
			if (contact is null && item.PreviousProviderContactIds is { Count: > 0 } previousIds)
			{
				var previousOwners = local.Where(candidate =>
					candidate.ProviderContactId is { } providerId
					&& previousIds.Contains(providerId, StringComparer.Ordinal)
				).ToArray();
				if (previousOwners.Length == 1) contact = previousOwners[0];
			}
			if (contact is null && reservedRemoteIds.Contains(item.ProviderContactId)) continue;
			if (contact is null)
			{
				contact = new Contact { Id = Guid.NewGuid(), AccountId = accountId };
				context.Contacts.Add(contact);
			}
			contact.ProviderMissingSince = null;
			contact.ProviderContactId = item.ProviderContactId;
			contact.ProviderContainerId = item.ProviderContainerId;
			if (!contact.SyncConflict
				&& !unresolved.Any(x => x.ContactId == contact.Id && !IsTerminal(x.State)))
			{
				Apply(contact, item.DisplayName, item.Emails);
				contact.ProviderRevision = item.Revision;
			}
		}

		foreach (var contact in local.Where(c => c.ProviderContactId is not null
			&& (pull.IsFullSnapshot
				? !visibleRemoteIds.Contains(c.ProviderContactId)
				: removedRemoteIds.Contains(c.ProviderContactId))
			&& !c.SyncConflict
			&& !unresolved.Any(x => x.ContactId == c.Id && !IsTerminal(x.State))))
		{
			if (contact.ProviderMissingSince is null)
				contact.ProviderMissingSince = DateTimeOffset.UtcNow;
		}
		context.ChangeTracker.DetectChanges();
		var changed = context.ChangeTracker.Entries()
			.Any(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted);
		if (account.ProviderType == ProviderType.Gmail)
			account.ContactSyncCursor = pull.NextCursor;
		faults.Reached(FaultPoints.ContactRefreshBeforeCommit);
		await context.SaveChangesAsync(ct);
		foreach (var contactId in adoptedContactIds)
			await DispatchNextAsync(contactId, ct);
		if (changed) await events.ContactsChangedAsync(accountId);
		return true;
	}

	public async Task<Contact> SaveAsync(ContactInput input, CancellationToken ct)
	{
		var emails = Validated(input.Emails);
		if (emails.Count == 0) throw new InvalidOperationException("A contact needs at least one email address.");
		using var lease = await gate.EnterAsync(input.AccountId, ct);
		var contact = input.ContactId is { } id
			? await context.Contacts.Include(c => c.Addresses).SingleAsync(c => c.Id == id, ct)
			: new Contact { Id = Guid.NewGuid(), AccountId = input.AccountId };
		if (contact.AccountId != input.AccountId) throw new InvalidOperationException("Contact belongs to another account.");
		if (input.ContactId is null) context.Contacts.Add(contact);

		Apply(contact, input.DisplayName, emails);
		var account = await context.Accounts.SingleAsync(a => a.Id == input.AccountId, ct);
		if (account.ProviderType == ProviderType.Imap)
		{
			await context.SaveChangesAsync(ct);
			await events.ContactsChangedAsync(contact.AccountId);
			return contact;
		}
		var pending = await context.ContactOperations
			.Where(x => x.ContactId == contact.Id && x.State == ContactOperationState.Pending)
			.ToListAsync(ct);
		pending.Sort((left, right) => left.Sequence.CompareTo(right.Sequence));
		var replacesCreate = pending.Any(x => x.Kind == ContactOperationKind.Create);
		var hasDispatchedCreate = await context.ContactOperations.AnyAsync(
			x => x.ContactId == contact.Id
				&& x.Kind == ContactOperationKind.Create
				&& (x.State == ContactOperationState.Dispatched || x.State == ContactOperationState.Ambiguous),
			ct
		);
		var kind = contact.ProviderContactId is null && (replacesCreate || !hasDispatchedCreate)
			? ContactOperationKind.Create
			: ContactOperationKind.Update;
		var operation = pending.FirstOrDefault() ?? await NewOperationAsync(contact, kind, emails, input.ExpectedRevision, ct);
		if (pending.Count == 0) context.ContactOperations.Add(operation);
		else context.ContactOperations.RemoveRange(pending.Skip(1));
		operation.Kind = kind;
		operation.DisplayName = contact.DisplayName;
		operation.EmailsJson = JsonSerializer.Serialize(emails);
		operation.ExpectedRevision = input.ExpectedRevision;
		await context.SaveChangesAsync(ct);
		Dispatch(operation.Id);
		await events.ContactsChangedAsync(contact.AccountId);
		return contact;
	}

	public async Task DeleteAsync(Guid contactId, string? expectedRevision, CancellationToken ct)
	{
		var accountId = await context.Contacts.Where(c => c.Id == contactId)
			.Select(c => c.AccountId).SingleAsync(ct);
		using var lease = await gate.EnterAsync(accountId, ct);
		var contact = await context.Contacts.Include(c => c.Addresses).SingleAsync(c => c.Id == contactId, ct);
		if (contact.SyncConflict)
			throw new InvalidOperationException(
				"Resolve the contact conflict before deleting this contact."
			);
		var unresolvedCreate = contact.ProviderContactId is null
			&& await context.ContactOperations.AnyAsync(
				x => x.ContactId == contact.Id
					&& x.Kind == ContactOperationKind.Create
					&& (x.State == ContactOperationState.Dispatched
						|| x.State == ContactOperationState.Ambiguous),
				ct
			);
		if (contact.ProviderContactId is null && !unresolvedCreate)
		{
			context.Contacts.Remove(contact);
			await context.SaveChangesAsync(ct);
			await events.ContactsChangedAsync(accountId);
			return;
		}
		var providerType = await context.Accounts.Where(account => account.Id == accountId)
			.Select(account => account.ProviderType)
			.SingleAsync(ct);
		if (providerType == ProviderType.Gmail)
			throw new InvalidOperationException(
				"Google does not support revision-checked contact deletion."
			);
		var operation = await NewOperationAsync(
			contact,
			ContactOperationKind.Delete,
			contact.Addresses.Select(a => a.Email),
			expectedRevision,
			ct
		);
		context.ContactOperations.Add(operation);
		await context.SaveChangesAsync(ct);
		Dispatch(operation.Id);
		await events.ContactsChangedAsync(accountId);
	}

	public async Task<Contact> ResolveConflictAsync(Guid contactId, bool keepMine, CancellationToken ct)
	{
		var accountId = await context.Contacts.Where(c => c.Id == contactId)
			.Select(c => c.AccountId).SingleAsync(ct);
		using var lease = await gate.EnterAsync(accountId, ct);
		var contact = await context.Contacts.Include(c => c.Addresses).SingleAsync(c => c.Id == contactId, ct);
		if (!contact.SyncConflict) return contact;
		var conflict = await context.ContactOperations
			.Where(operation => operation.ContactId == contactId
				&& operation.State == ContactOperationState.Conflict)
			.OrderByDescending(operation => operation.Sequence)
			.FirstAsync(ct);
		var later = await PendingAfterAsync(conflict, ct);
		var account = await context.Accounts.SingleAsync(a => a.Id == accountId, ct);
		var providerDelay = providerGate.Delay(account.Id);
		if (providerDelay > TimeSpan.Zero)
			throw new ProviderThrottledException(providerDelay, "Account is throttled.");
		ProviderContact? remote;
		try
		{
			remote = (await ProviderFor(account).PullAsync(account, useCursor: false, ct))
				.Contacts.SingleOrDefault(item =>
					item.ProviderContactId == contact.ProviderContactId
					|| (item.PreviousProviderContactIds?.Contains(
						contact.ProviderContactId!,
						StringComparer.Ordinal
					) ?? false));
			await ClearCredentialStoreUnavailableAsync(account, ct);
		}
		catch (ProviderThrottledException ex)
		{
			providerGate.Throttle(account.Id, ex.RetryAfter);
			await Accounts.AccountDtoFactory.AnnounceStatusAsync(
				context,
				events,
				account,
				CancellationToken.None,
				providerGate
			);
			throw;
		}
		catch (CredentialStoreUnavailableException ex)
		{
			await SetCredentialStoreUnavailableAsync(account, ex.Message);
			throw;
		}
		catch (ProviderAuthenticationException ex)
		{
			await PauseForAuthenticationAsync(account, ex.Message);
			throw;
		}

		contact.SyncConflict = false;
		if (keepMine)
		{
			if (remote is null)
			{
				contact.ProviderContactId = null;
				contact.ProviderContainerId = null;
				contact.ProviderRevision = null;
				if (conflict.Kind == ContactOperationKind.Delete)
				{
					Complete(conflict);
					await PrepareAfterRemoteDeletionAsync(contact, conflict.Sequence, ct);
				}
				else
				{
					conflict.Kind = ContactOperationKind.Create;
					ResetPending(conflict, null);
				}
			}
			else
			{
				contact.ProviderContactId = remote.ProviderContactId;
				contact.ProviderContainerId = remote.ProviderContainerId;
				contact.ProviderRevision = remote.Revision;
				if (later.Count == 0) Apply(contact, conflict.DisplayName, Emails(conflict));
				ResetPending(conflict, remote.Revision);
			}
		}
		else
		{
			MarkNotApplied(conflict);
			if (remote is null || remote.Emails.Count == 0)
			{
				await PrepareAfterRemoteDeletionAsync(contact, conflict.Sequence, ct);
			}
			else
			{
				contact.ProviderContactId = remote.ProviderContactId;
				contact.ProviderContainerId = remote.ProviderContainerId;
				contact.ProviderRevision = remote.Revision;
				await RebasePendingAfterAsync(conflict, remote.Revision, ct);
				if (later.Count == 0) Apply(contact, remote.DisplayName, remote.Emails);
			}
		}

		await context.SaveChangesAsync(ct);
		await DispatchNextAsync(contact.Id, ct);
		await events.ContactsChangedAsync(accountId);
		return contact;
	}
	public async Task AbandonAmbiguousCreateAsync(Guid contactId, CancellationToken ct)
	{
		var accountId = await context.Contacts.Where(contact => contact.Id == contactId)
			.Select(contact => contact.AccountId).SingleAsync(ct);
		using var lease = await gate.EnterAsync(accountId, ct);
		var contact = await context.Contacts.SingleAsync(item => item.Id == contactId, ct);
		var operation = await context.ContactOperations
			.Where(item => item.ContactId == contactId
				&& item.Kind == ContactOperationKind.Create
				&& (item.State == ContactOperationState.Dispatched
					|| item.State == ContactOperationState.Ambiguous))
			.OrderBy(item => item.Sequence)
			.FirstOrDefaultAsync(ct)
			?? throw new InvalidOperationException("The contact has no ambiguous create to discard.");
		MarkNotApplied(operation);
		context.Contacts.Remove(contact);
		await context.SaveChangesAsync(ct);
		await events.ContactsChangedAsync(accountId);
	}


	public async Task ExecuteAsync(Guid operationId, CancellationToken ct)
	{
		var accountId = await AccountIdForOperationAsync(operationId, ct);
		if (accountId is null) return;
		using var lease = await gate.EnterAsync(accountId.Value, ct);
		var operation = await context.ContactOperations.SingleOrDefaultAsync(x => x.Id == operationId, ct);
		if (operation is null || IsTerminal(operation.State)) return;
		var contact = await context.Contacts.Include(c => c.Addresses)
			.SingleOrDefaultAsync(c => c.Id == operation.ContactId, ct);
		if (contact is null) return;
		if (await HasUnsettledPredecessorAsync(operation, ct)) return;
		var account = await context.Accounts.SingleAsync(a => a.Id == contact.AccountId, ct);
		if (!account.IsEnabled || account.AuthState == AuthState.NeedsReauth) return;
		if (account.ProviderType == ProviderType.Imap)
		{
			Complete(operation);
			await context.SaveChangesAsync(ct);
			await DispatchNextAsync(contact.Id, ct);
			await events.ContactsChangedAsync(account.Id);
			return;
		}
		var providerDelay = providerGate.Delay(account.Id);
		if (providerDelay > TimeSpan.Zero)
		{
			ScheduleExecution(operation.Id, providerDelay);
			return;
		}
		if (operation.State == ContactOperationState.Dispatched)
		{
			operation.State = ContactOperationState.Ambiguous;
			await context.SaveChangesAsync(ct);
			ScheduleReconciliation(operation.Id);
			return;
		}
		if (operation.State == ContactOperationState.Ambiguous) return;
		IContactProvider provider;
		try
		{
			provider = ProviderFor(account);
		}
		catch (ProviderNotConfiguredException)
		{
			return;
		}

		var dispatchedAt = DateTimeOffset.UtcNow;
		var claimed = await context.ContactOperations
			.Where(x => x.Id == operation.Id && x.State == ContactOperationState.Pending)
			.ExecuteUpdateAsync(
				setters => setters
					.SetProperty(x => x.State, ContactOperationState.Dispatched)
					.SetProperty(x => x.DispatchedAt, dispatchedAt),
				ct
			);
		if (claimed == 0) return;
		await context.Entry(operation).ReloadAsync(ct);
		faults.Reached(FaultPoints.ContactAfterDispatchedBeforeProviderCall);

		try
		{
			var write = new ProviderContactWrite(operation.DisplayName, Emails(operation));
			ProviderContact? result = null;
			switch (operation.Kind)
			{
				case ContactOperationKind.Create:
					result = await provider.CreateAsync(account, write, ct);
					break;
				case ContactOperationKind.Update:
					result = await provider.UpdateAsync(
						account,
						contact.ProviderContactId!,
						contact.ProviderContainerId,
						operation.ExpectedRevision,
						write,
						ct
					);
					break;
				case ContactOperationKind.Delete:
					await provider.DeleteAsync(
						account,
						contact.ProviderContactId!,
						contact.ProviderContainerId,
						operation.ExpectedRevision,
						ct
					);
					break;
				default:
					throw new ArgumentOutOfRangeException();
			}
			faults.Reached(FaultPoints.ContactAfterProviderCallBeforeResults);
			if (operation.Kind == ContactOperationKind.Delete)
			{
				Complete(operation);
				await PrepareAfterRemoteDeletionAsync(contact, operation.Sequence, ct);
			}
			else if (result is not null)
			{
				await ResolveCreatedOrUpdatedIdentityAsync(contact, operation, result, ct);
				Complete(operation);
			}
			await context.SaveChangesAsync(ct);
			await ClearCredentialStoreUnavailableAsync(account, ct);
			await DispatchNextAsync(contact.Id, ct);
			await events.ContactsChangedAsync(account.Id);
		}
		catch (ProviderThrottledException ex)
		{
			providerGate.Throttle(account.Id, ex.RetryAfter);
			ResetPending(operation, operation.ExpectedRevision);
			await context.SaveChangesAsync(CancellationToken.None);
			await Accounts.AccountDtoFactory.AnnounceStatusAsync(
				context,
				events,
				account,
				CancellationToken.None,
				providerGate
			);
			ScheduleExecution(operation.Id, providerGate.Delay(account.Id));
		}
		catch (ProviderAuthenticationException ex)
		{
			ResetPending(operation, operation.ExpectedRevision);
			await PauseForAuthenticationAsync(account, ex.Message);
		}
		catch (ProviderContactRejectedException)
		{
			operation.State = ContactOperationState.Rejected;
			operation.SettledAt = DateTimeOffset.UtcNow;
			if (operation.Kind == ContactOperationKind.Create)
				await PrepareAfterRejectedCreateAsync(contact, operation.Sequence, CancellationToken.None);
			await context.SaveChangesAsync(CancellationToken.None);
			await DispatchNextAsync(contact.Id, CancellationToken.None);
			await events.ContactsChangedAsync(account.Id);
		}
		catch (ProviderConflictException)
		{
			operation.State = ContactOperationState.Conflict;
			operation.SettledAt = DateTimeOffset.UtcNow;
			contact.SyncConflict = true;
			await context.SaveChangesAsync(ct);
			await events.ContactsChangedAsync(account.Id);
		}
		catch (CredentialStoreUnavailableException ex)
		{
			ResetPending(operation, operation.ExpectedRevision);
			await context.SaveChangesAsync(CancellationToken.None);
			await SetCredentialStoreUnavailableAsync(account, ex.Message);
			ScheduleExecution(operation.Id, TimeSpan.FromMinutes(1));
		}
		catch (OperationCanceledException)
		{
			operation.State = ContactOperationState.Ambiguous;
			await context.SaveChangesAsync(CancellationToken.None);
			await events.ContactsChangedAsync(account.Id);
			ScheduleReconciliation(operation.Id);
			throw;
		}
		catch (Exception ex) when (ex is not OperationCanceledException and not SimulatedCrashException)
		{
			operation.State = ContactOperationState.Ambiguous;
			await context.SaveChangesAsync(CancellationToken.None);
			await events.ContactsChangedAsync(account.Id);
			ScheduleReconciliation(operation.Id);
			throw;
		}
	}

	public async Task ReconcileAsync(Guid operationId, CancellationToken ct)
	{
		var accountId = await AccountIdForOperationAsync(operationId, ct);
		if (accountId is null) return;
		using var lease = await gate.EnterAsync(accountId.Value, ct);
		var operation = await context.ContactOperations.SingleOrDefaultAsync(x => x.Id == operationId, ct);
		if (operation is null
			|| operation.State is not (ContactOperationState.Ambiguous or ContactOperationState.Dispatched)) return;
		if (operation.State == ContactOperationState.Dispatched)
		{
			operation.State = ContactOperationState.Ambiguous;
			await context.SaveChangesAsync(ct);
			await events.ContactsChangedAsync(accountId.Value);
		}
		var contact = await context.Contacts.Include(c => c.Addresses)
			.SingleOrDefaultAsync(c => c.Id == operation.ContactId, ct);
		if (contact is null) return;
		var account = await context.Accounts.SingleAsync(a => a.Id == accountId.Value, ct);
		if (!account.IsEnabled || account.AuthState == AuthState.NeedsReauth) return;
		var providerDelay = providerGate.Delay(account.Id);
		if (providerDelay > TimeSpan.Zero)
		{
			ScheduleReconciliation(operation.Id, providerDelay);
			return;
		}
		IReadOnlyList<ProviderContact> remote;
		try
		{
			remote = (await ProviderFor(account).PullAsync(account, useCursor: false, ct)).Contacts;
			await ClearCredentialStoreUnavailableAsync(account, ct);
		}
		catch (ProviderThrottledException ex)
		{
			providerGate.Throttle(account.Id, ex.RetryAfter);
			await Accounts.AccountDtoFactory.AnnounceStatusAsync(
				context,
				events,
				account,
				CancellationToken.None,
				providerGate
			);
			ScheduleReconciliation(operation.Id, providerGate.Delay(account.Id));
			return;
		}
		catch (CredentialStoreUnavailableException ex)
		{
			await SetCredentialStoreUnavailableAsync(account, ex.Message);
			ScheduleReconciliation(operation.Id);
			return;
		}
		catch (ProviderAuthenticationException ex)
		{
			await PauseForAuthenticationAsync(account, ex.Message);
			return;
		}
		catch (ProviderNotConfiguredException)
		{
			return;
		}
		catch (OperationCanceledException)
		{
			ScheduleReconciliation(operation.Id);
			throw;
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			ScheduleReconciliation(operation.Id);
			throw;
		}
		var current = contact.ProviderContactId is null
			? null
			: remote.SingleOrDefault(x => x.ProviderContactId == contact.ProviderContactId);
		var ownedProviderIds = await context.Contacts
			.Where(other => other.AccountId == account.Id
				&& other.Id != contact.Id
				&& other.ProviderContactId != null)
			.Select(other => other.ProviderContactId!)
			.ToListAsync(ct);
		var matches = remote.Where(x => !ownedProviderIds.Contains(x.ProviderContactId)
			&& Same(x, operation.DisplayName, Emails(operation))).ToArray();
		var hasCompetingCreate = false;
		if (operation.Kind == ContactOperationKind.Create && matches.Length == 1)
		{
			var competing = await context.ContactOperations
				.Where(other => other.Id != operation.Id
					&& other.Kind == ContactOperationKind.Create
					&& (other.State == ContactOperationState.Dispatched
						|| other.State == ContactOperationState.Ambiguous))
				.Join(
					context.Contacts.Where(otherContact => otherContact.AccountId == account.Id),
					other => other.ContactId,
					otherContact => otherContact.Id,
					(other, _) => other
				)
				.ToListAsync(ct);
			hasCompetingCreate = competing.Any(other =>
				Same(matches[0], other.DisplayName, Emails(other)));
		}
		ContactOperation? retry = null;

		if (operation.Kind == ContactOperationKind.Create
			&& matches.Length == 1
			&& !hasCompetingCreate)
		{
			await ResolveCreatedIdentityAsync(contact, operation, matches[0], ct);
		}
		else if (operation.Kind == ContactOperationKind.Update
			&& current is not null
			&& Same(current, operation.DisplayName, Emails(operation)))
		{
			contact.ProviderRevision = current.Revision;
			contact.ProviderContainerId = current.ProviderContainerId;
			await RebasePendingAfterAsync(operation, current.Revision, ct);
			Complete(operation);
		}
		else if (operation.Kind == ContactOperationKind.Delete && current is null)
		{
			Complete(operation);
			await PrepareAfterRemoteDeletionAsync(contact, operation.Sequence, ct);
		}
		else if (operation.Kind is ContactOperationKind.Update or ContactOperationKind.Delete
			&& current is not null
			&& current.Revision == operation.ExpectedRevision)
		{
			ResetPending(operation, current.Revision);
			retry = operation;
		}
		else if (operation.Kind is ContactOperationKind.Update or ContactOperationKind.Delete)
		{
			operation.State = ContactOperationState.Conflict;
			operation.SettledAt = DateTimeOffset.UtcNow;
			contact.SyncConflict = true;
		}

		await context.SaveChangesAsync(ct);
		if (retry is not null) Dispatch(retry.Id);
		else if (operation.State is ContactOperationState.Completed or ContactOperationState.NotApplied)
			await DispatchNextAsync(operation.ContactId, ct);
		await events.ContactsChangedAsync(account.Id);
	}

	private async Task<Guid?> AccountIdForOperationAsync(
		Guid operationId,
		CancellationToken ct
	) => await context.ContactOperations.AsNoTracking()
		.Where(x => x.Id == operationId)
		.Join(
			context.Contacts,
			operation => operation.ContactId,
			contact => contact.Id,
			(_, contact) => (Guid?)contact.AccountId
		)
		.SingleOrDefaultAsync(ct);

	private async Task<bool> HasUnsettledPredecessorAsync(
		ContactOperation operation,
		CancellationToken ct
	)
	{
		var states = await context.ContactOperations
			.Where(x => x.ContactId == operation.ContactId && x.Sequence < operation.Sequence)
			.Select(x => x.State)
			.ToListAsync(ct);
		return states.Any(state => !SettlesPredecessor(state));
	}

	private async Task ResolveCreatedIdentityAsync(
		Contact contact,
		ContactOperation operation,
		ProviderContact result,
		CancellationToken ct
	)
	{
		contact.ProviderContactId = result.ProviderContactId;
		contact.ProviderContainerId = result.ProviderContainerId;
		contact.ProviderRevision = result.Revision;
		var later = await RebasePendingAfterAsync(operation, result.Revision, ct);
		if (later.Count == 0) Apply(contact, result.DisplayName, result.Emails);
		Complete(operation);
	}

	private async Task ResolveCreatedOrUpdatedIdentityAsync(
		Contact contact,
		ContactOperation operation,
		ProviderContact result,
		CancellationToken ct
	)
	{
		contact.ProviderContactId = result.ProviderContactId;
		contact.ProviderContainerId = result.ProviderContainerId;
		contact.ProviderRevision = result.Revision;
		var later = await RebasePendingAfterAsync(operation, result.Revision, ct);
		if (later.Count == 0) Apply(contact, result.DisplayName, result.Emails);
	}

	private async Task<List<ContactOperation>> PendingAfterAsync(
		ContactOperation operation,
		CancellationToken ct
	) => await context.ContactOperations
		.Where(next => next.ContactId == operation.ContactId
			&& next.Sequence > operation.Sequence
			&& next.State == ContactOperationState.Pending)
		.OrderBy(next => next.Sequence)
		.ToListAsync(ct);

	private async Task<List<ContactOperation>> RebasePendingAfterAsync(
		ContactOperation operation,
		string? revision,
		CancellationToken ct
	)
	{
		var later = await PendingAfterAsync(operation, ct);
		foreach (var next in later)
		{
			if (next.Kind == ContactOperationKind.Create) next.Kind = ContactOperationKind.Update;
			next.ExpectedRevision = revision;
		}
		return later;
	}

	private async Task PrepareAfterRemoteDeletionAsync(
		Contact contact,
		long settledSequence,
		CancellationToken ct
	)
	{
		contact.ProviderContactId = null;
		contact.ProviderContainerId = null;
		contact.ProviderRevision = null;
		var later = await context.ContactOperations
			.Where(next => next.ContactId == contact.Id
				&& next.Sequence > settledSequence
				&& next.State == ContactOperationState.Pending)
			.OrderBy(next => next.Sequence)
			.ToListAsync(ct);
		foreach (var redundantDelete in later.TakeWhile(next => next.Kind == ContactOperationKind.Delete))
			Complete(redundantDelete);
		var nextIntent = later.FirstOrDefault(next => next.State == ContactOperationState.Pending);
		if (nextIntent is null)
		{
			context.Contacts.Remove(contact);
			return;
		}
		nextIntent.Kind = ContactOperationKind.Create;
		foreach (var pending in later.Where(next => next.State == ContactOperationState.Pending))
			pending.ExpectedRevision = null;
	}

	private async Task PrepareAfterRejectedCreateAsync(
		Contact contact,
		long rejectedSequence,
		CancellationToken ct
	)
	{
		var later = await context.ContactOperations
			.Where(next => next.ContactId == contact.Id
				&& next.Sequence > rejectedSequence
				&& next.State == ContactOperationState.Pending)
			.OrderBy(next => next.Sequence)
			.ToListAsync(ct);
		var deletes = later.TakeWhile(next => next.Kind == ContactOperationKind.Delete).ToArray();
		foreach (var delete in deletes) Complete(delete);
		var nextIntent = later.FirstOrDefault(next => next.State == ContactOperationState.Pending);
		if (nextIntent is null)
		{
			if (deletes.Length > 0) context.Contacts.Remove(contact);
			return;
		}
		nextIntent.Kind = ContactOperationKind.Create;
		foreach (var pending in later.Where(next => next.State == ContactOperationState.Pending))
			pending.ExpectedRevision = null;
	}

	private async Task DispatchNextAsync(Guid contactId, CancellationToken ct)
	{
		var nextId = await context.ContactOperations
			.Where(x => x.ContactId == contactId && x.State == ContactOperationState.Pending)
			.OrderBy(x => x.Sequence)
			.Select(x => (Guid?)x.Id)
			.FirstOrDefaultAsync(ct);
		if (nextId is { } id) Dispatch(id);
	}

	private static bool IsTerminal(ContactOperationState state) =>
		state is ContactOperationState.Completed
			or ContactOperationState.Conflict
			or ContactOperationState.NotApplied
			or ContactOperationState.Rejected;
	private static bool SettlesPredecessor(ContactOperationState state) =>
		state is ContactOperationState.Completed
			or ContactOperationState.NotApplied
			or ContactOperationState.Rejected;

	private static void Complete(ContactOperation operation)
	{
		operation.State = ContactOperationState.Completed;
		operation.SettledAt = DateTimeOffset.UtcNow;
	}
	private static void MarkNotApplied(ContactOperation operation)
	{
		operation.State = ContactOperationState.NotApplied;
		operation.SettledAt = DateTimeOffset.UtcNow;
	}

	private static void ResetPending(ContactOperation operation, string? expectedRevision)
	{
		operation.State = ContactOperationState.Pending;
		operation.ExpectedRevision = expectedRevision;
		operation.DispatchedAt = null;
		operation.SettledAt = null;
	}


	private async Task<ContactOperation> NewOperationAsync(
		Contact contact,
		ContactOperationKind kind,
		IEnumerable<string> emails,
		string? expectedRevision,
		CancellationToken ct
	)
	{
		var sequences = await context.ContactOperations
			.Where(x => x.ContactId == contact.Id)
			.Select(x => x.Sequence)
			.ToListAsync(ct);
		return new ContactOperation
		{
			Id = Guid.NewGuid(),
			ContactId = contact.Id,
			Sequence = sequences.Count == 0 ? 1 : sequences.Max() + 1,
			Kind = kind,
			State = ContactOperationState.Pending,
			DisplayName = contact.DisplayName,
			EmailsJson = JsonSerializer.Serialize(Normalized(emails)),
			ExpectedRevision = expectedRevision,
			CreatedAt = DateTimeOffset.UtcNow,
		};
	}

	private void Dispatch(Guid operationId) =>
		jobs?.Enqueue<Scheduling.ContactJobs>(job => job.ExecuteAsync(operationId, default));

	private void ScheduleExecution(Guid operationId, TimeSpan delay) =>
		jobs?.Schedule<Scheduling.ContactJobs>(
			job => job.ExecuteAsync(operationId, default),
			delay
		);

	private void ScheduleReconciliation(Guid operationId, TimeSpan? delay = null) =>
		jobs?.Schedule<Scheduling.ContactJobs>(
			job => job.ReconcileAsync(operationId, default),
			delay ?? TimeSpan.FromMinutes(1)
		);

	private async Task PauseForAuthenticationAsync(Account account, string message)
	{
		account.AuthState = AuthState.NeedsReauth;
		account.LastAuthError = message;
		await context.SaveChangesAsync(CancellationToken.None);
		await Accounts.AccountDtoFactory.AnnounceStatusAsync(
			context,
			events,
			account,
			CancellationToken.None
		);
	}

	private async Task SetCredentialStoreUnavailableAsync(Account account, string message)
	{
		account.AuthState = AuthState.CredentialStoreUnavailable;
		account.LastAuthError = message;
		await context.SaveChangesAsync(CancellationToken.None);
		await Accounts.AccountDtoFactory.AnnounceStatusAsync(
			context,
			events,
			account,
			CancellationToken.None
		);
	}

	private async Task ClearCredentialStoreUnavailableAsync(Account account, CancellationToken ct)
	{
		if (account.AuthState != AuthState.CredentialStoreUnavailable) return;
		account.AuthState = AuthState.Connected;
		account.LastAuthError = null;
		await context.SaveChangesAsync(ct);
		await Accounts.AccountDtoFactory.AnnounceStatusAsync(context, events, account, ct);
	}

	private IContactProvider ProviderFor(Account account) =>
		services.GetRequiredService<IContactProviderFactory>().For(account);

	private static string[] Emails(ContactOperation operation) =>
		JsonSerializer.Deserialize<string[]>(operation.EmailsJson) ?? [];

	private static bool Same(ProviderContact contact, string name, IReadOnlyList<string> emails) =>
		Normalize(contact.DisplayName) == Normalize(name)
		&& Normalized(contact.Emails).Order().SequenceEqual(Normalized(emails).Order(), StringComparer.Ordinal);

	private static void Apply(Contact contact, string displayName, IEnumerable<string> emails)
	{
		contact.DisplayName = displayName.Trim();
		var desired = Normalized(emails)
			.ToDictionary(Normalize, email => email, StringComparer.Ordinal);
		foreach (var existing in contact.Addresses.ToArray())
		{
			if (!desired.TryGetValue(existing.NormalizedEmail, out var desiredEmail))
			{
				contact.Addresses.Remove(existing);
				continue;
			}
			existing.Email = desiredEmail;
			desired.Remove(existing.NormalizedEmail);
		}
		foreach (var (normalized, email) in desired)
		{
			contact.Addresses.Add(new ContactAddress
			{
				Id = Guid.NewGuid(),
				ContactId = contact.Id,
				Email = email,
				NormalizedEmail = normalized,
			});
		}
	}

	private static List<string> Validated(IEnumerable<string> emails)
	{
		var parsed = new List<string>();
		foreach (var raw in emails)
		{
			if (!ContactEmail.TryParse(raw, out var email))
				throw new InvalidOperationException($"\"{raw.Trim()}\" is not a valid email address.");
			parsed.Add(email);
		}
		return parsed
			.DistinctBy(Normalize, StringComparer.Ordinal)
			.ToList();
	}

	private static List<string> Normalized(IEnumerable<string> emails) => emails
		.Select(email => email.Trim())
		.Where(email => email.Length > 0)
		.DistinctBy(Normalize, StringComparer.Ordinal)
		.ToList();

	public static string Normalize(string value) => value.Trim().ToLowerInvariant();
}

internal static class ContactEmail
{
	public static bool TryParse(string raw, out string email)
	{
		email = string.Empty;
		if (!MailboxAddress.TryParse(raw.Trim(), out var mailbox)
			|| mailbox.Address.IndexOf('@') <= 0
			|| mailbox.Address.EndsWith('@')) return false;
		email = mailbox.Address;
		return true;
	}
}
