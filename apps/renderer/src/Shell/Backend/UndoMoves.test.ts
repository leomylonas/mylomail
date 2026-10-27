import { QueryClient } from "@tanstack/react-query";
import { describe, expect, it } from "vitest";
import {
	recordMove,
	takeLastMove,
} from "@mylomail/renderer/Shell/Backend/UndoMoves";

const move = (id: string, ids = ["m1"]) => ({
	description: `Moved ${id}`,
	accountId: "a",
	messageIds: ids,
	fromMailboxId: "inbox",
});

describe("undo history", () => {
	it("hands moves back newest first, once each", () => {
		const client = new QueryClient();
		recordMove(client, move("one"));
		recordMove(client, move("two"));

		expect(takeLastMove(client)?.description).toBe("Moved two");
		expect(takeLastMove(client)?.description).toBe("Moved one");
		expect(takeLastMove(client)).toBeUndefined();
	});

	it("does not record a move that moved nothing, and forgets the oldest past twenty", () => {
		const client = new QueryClient();
		recordMove(client, move("empty", []));
		expect(takeLastMove(client)).toBeUndefined();

		for (let i = 0; i < 25; i++) recordMove(client, move(String(i)));
		let count = 0;
		while (takeLastMove(client)) count++;
		expect(count).toBe(20);
	});
});
