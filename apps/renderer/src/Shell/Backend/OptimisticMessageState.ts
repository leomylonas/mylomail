import type { QueryClient } from "@tanstack/react-query";

type OptimisticMessageClaims = Record<string, string[]>;

export interface OptimisticMessageClaim {
	messageId: string;
	claimId: string;
}

interface MutationAcceptance {
	messageId: string;
	mutationItemId: string;
}

const settledMutationIdsKey = ["settled-message-mutations"] as const;

/** Cache-only per-window projection while a membership-changing mutation is outstanding. */
export function optimisticMessageIdsKey(mailboxId: string) {
	return ["optimistic-message-ids", mailboxId] as const;
}

export function createOptimisticMessageClaims(
	messageIds: readonly string[],
): OptimisticMessageClaim[] {
	return [...new Set(messageIds)].map((messageId) => ({
		messageId,
		claimId: `local:${crypto.randomUUID()}`,
	}));
}

export function optimisticMessageIds(
	claims: OptimisticMessageClaims | undefined,
): string[] {
	return Object.entries(claims ?? {})
		.filter(([, claimIds]) => claimIds.length > 0)
		.map(([messageId]) => messageId);
}

/** Durable mutation ids whose source-view projection must survive ordinary query invalidation. */
export function optimisticMutationIds(queryClient: QueryClient): string[] {
	const ids = new Set<string>();
	for (const [, claims] of queryClient.getQueriesData<OptimisticMessageClaims>({
		queryKey: ["optimistic-message-ids"],
	})) {
		for (const claimIds of Object.values(claims ?? {})) {
			for (const claimId of claimIds) {
				if (!claimId.startsWith("local:")) ids.add(claimId);
			}
		}
	}
	return [...ids];
}

/** Releases durable claims a reconnect query proves have already reached a terminal state. */
export function settleOptimisticMutations(
	queryClient: QueryClient,
	mutationItemIds: readonly string[],
): void {
	if (mutationItemIds.length === 0) return;
	const ids = new Set(mutationItemIds);
	queryClient.setQueriesData<OptimisticMessageClaims>(
		{ queryKey: ["optimistic-message-ids"] },
		(current) => removeClaims(current, ids),
	);
}

export function hideOptimisticMessages(
	queryClient: QueryClient,
	mailboxId: string,
	claims: readonly OptimisticMessageClaim[],
): void {
	queryClient.setQueryData<OptimisticMessageClaims>(
		optimisticMessageIdsKey(mailboxId),
		(current) => {
			const next = { ...current };
			for (const claim of claims)
				next[claim.messageId] = [
					...(next[claim.messageId] ?? []),
					claim.claimId,
				];
			return next;
		},
	);
}

export function restoreOptimisticMessages(
	queryClient: QueryClient,
	mailboxId: string,
	claims: readonly OptimisticMessageClaim[],
): void {
	const claimIds = new Set(claims.map((claim) => claim.claimId));
	queryClient.setQueryData<OptimisticMessageClaims>(
		optimisticMessageIdsKey(mailboxId),
		(current) => removeClaims(current, claimIds),
	);
}

/**
 * Replaces request-local claims with durable mutation identities. A provider can finish before
 * the enqueue response crosses SignalR; a remembered early settlement consumes the local claim
 * instead of resurrecting it under an already-terminal id.
 */
export function acceptOptimisticMessages(
	queryClient: QueryClient,
	mailboxId: string,
	claims: readonly OptimisticMessageClaim[],
	accepted: readonly MutationAcceptance[],
): void {
	const acceptedByMessage = new Map(
		accepted.map((item) => [item.messageId, item.mutationItemId]),
	);
	const settled = new Set(
		queryClient.getQueryData<string[]>(settledMutationIdsKey) ?? [],
	);
	queryClient.setQueryData<OptimisticMessageClaims>(
		optimisticMessageIdsKey(mailboxId),
		(current) => {
			let next = { ...current };
			next = removeClaims(next, new Set(claims.map((claim) => claim.claimId)));
			for (const claim of claims) {
				const mutationItemId = acceptedByMessage.get(claim.messageId);
				if (!mutationItemId) continue;
				if (settled.delete(mutationItemId)) continue;
				next[claim.messageId] = [
					...(next[claim.messageId] ?? []),
					mutationItemId,
				];
			}
			return next;
		},
	);
	queryClient.setQueryData(settledMutationIdsKey, [...settled]);
}

/** Releases exactly the source-view claim owned by one terminal durable mutation item. */
export function settleOptimisticMutation(
	queryClient: QueryClient,
	settlement: { mutationItemId: string; messageId: string },
): void {
	let matched = false;
	queryClient.setQueriesData<OptimisticMessageClaims>(
		{ queryKey: ["optimistic-message-ids"] },
		(current) => {
			if (
				!Object.values(current ?? {}).some((claimIds) =>
					claimIds.includes(settlement.mutationItemId),
				)
			)
				return current;
			matched = true;
			return removeClaims(current, new Set([settlement.mutationItemId]));
		},
	);
	if (matched || !hasLocalClaim(queryClient, settlement.messageId)) return;
	const settled = new Set(
		queryClient.getQueryData<string[]>(settledMutationIdsKey) ?? [],
	);
	settled.add(settlement.mutationItemId);
	queryClient.setQueryData(settledMutationIdsKey, [...settled]);
}

function hasLocalClaim(queryClient: QueryClient, messageId: string): boolean {
	return queryClient
		.getQueriesData<OptimisticMessageClaims>({
			queryKey: ["optimistic-message-ids"],
		})
		.some(([, claims]) =>
			(claims?.[messageId] ?? []).some((claimId) =>
				claimId.startsWith("local:"),
			),
		);
}

function removeClaims(
	current: OptimisticMessageClaims | undefined,
	claimIds: ReadonlySet<string>,
): OptimisticMessageClaims {
	return Object.fromEntries(
		Object.entries(current ?? {})
			.map(([messageId, currentClaimIds]) => [
				messageId,
				currentClaimIds.filter((claimId) => !claimIds.has(claimId)),
			])
			.filter(([, currentClaimIds]) => currentClaimIds.length > 0),
	);
}
