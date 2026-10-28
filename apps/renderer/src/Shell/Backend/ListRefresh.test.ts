import { QueryClient, QueryObserver } from "@tanstack/react-query";
import { describe, expect, it } from "vitest";
import { releaseWhenListsAreFresh } from "@mylomail/renderer/Shell/Backend/ListRefresh";

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
});
