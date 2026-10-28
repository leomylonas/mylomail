import { QueryClient, QueryObserver } from "@tanstack/react-query";
import { describe, expect, it } from "vitest";
import {
	refreshMessageLists,
	releaseWhenListsAreFresh,
} from "@mylomail/renderer/Shell/Backend/ListRefresh";

describe("releaseWhenListsAreFresh", () => {
	it("holds the release until an active list has been read again, even if the refetch call returned at once", async () => {
		const client = new QueryClient();
		let reads = 0;
		const observer = new QueryObserver(client, {
			queryKey: ["messages", "inbox"],
			queryFn: async () => {
				await new Promise((resolve) => setTimeout(resolve, 80));
				return ++reads;
			},
		});
		const unsubscribe = observer.subscribe(() => undefined);
		await new Promise((resolve) => setTimeout(resolve, 150));
		const settledAt = Date.now();

		// The refetch is started, and its promise is not waited for.
		void client.invalidateQueries({ queryKey: ["messages"] });
		let released = false;
		const done = releaseWhenListsAreFresh(client, settledAt, () => {
			released = true;
		});
		await new Promise((resolve) => setTimeout(resolve, 30));
		expect(released).toBe(false);

		await done;
		expect(released).toBe(true);
		expect(reads).toBe(2);
		unsubscribe();
	});

	it("does not wait for ever on a list that is never read again", async () => {
		const client = new QueryClient();
		const observer = new QueryObserver(client, {
			queryKey: ["messages", "inbox"],
			queryFn: () => 1,
			staleTime: Infinity,
		});
		const unsubscribe = observer.subscribe(() => undefined);
		await new Promise((resolve) => setTimeout(resolve, 20));

		let released = false;
		await releaseWhenListsAreFresh(
			client,
			Date.now() + 1000,
			() => {
				released = true;
			},
			{ maxWaitMs: 100, pollMs: 10 },
		);
		expect(released).toBe(true);
		unsubscribe();
	});

	it("lets a read finish while a run of events asks for more, and reads once more after it", async () => {
		const client = new QueryClient();
		let started = 0;
		let finished = 0;
		const observer = new QueryObserver(client, {
			queryKey: ["messages", "inbox"],
			queryFn: async () => {
				started++;
				await new Promise((resolve) => setTimeout(resolve, 100));
				return ++finished;
			},
		});
		const unsubscribe = observer.subscribe(() => undefined);
		await new Promise((resolve) => setTimeout(resolve, 200));
		started = 0;
		finished = 0;

		// Ten events, 30 ms apart: each used to cancel the read under way.
		const asks: Promise<void>[] = [];
		for (let i = 0; i < 10; i++) {
			asks.push(refreshMessageLists(client));
			await new Promise((resolve) => setTimeout(resolve, 30));
		}
		await Promise.all(asks);

		expect(finished).toBeGreaterThanOrEqual(2);
		expect(finished).toBe(started);
		expect(finished).toBeLessThanOrEqual(4);
		unsubscribe();
	});
});
