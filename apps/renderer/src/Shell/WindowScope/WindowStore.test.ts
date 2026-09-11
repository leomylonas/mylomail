import { describe, expect, it } from "vitest";
import {
	createWindowStore,
	type ThreadModePersistence,
} from "@mylomail/renderer/Shell/WindowScope/WindowStore";

class MemoryPersistence implements ThreadModePersistence {
	value: "flat" | "collapsed" | null = null;

	read(): "flat" | "collapsed" | null {
		return this.value;
	}

	write(value: "flat" | "collapsed"): void {
		this.value = value;
	}
}

describe("createWindowStore", () => {
	it("keeps the message-list thread mode isolated to the window that changes it", () => {
		const firstPersistence = new MemoryPersistence();
		const secondPersistence = new MemoryPersistence();
		const firstWindow = createWindowStore(firstPersistence);
		const secondWindow = createWindowStore(secondPersistence);

		firstWindow.setState("messageListThreadMode", "collapsed");

		expect(firstWindow.getState("messageListThreadMode")).toBe("collapsed");
		expect(secondWindow.getState("messageListThreadMode")).toBe("flat");
	});

	it("restores the message-list thread mode for the same window slot", () => {
		const persistence = new MemoryPersistence();
		const firstLoad = createWindowStore(persistence);
		firstLoad.setState("messageListThreadMode", "collapsed");

		const reloaded = createWindowStore(persistence);

		expect(reloaded.getState("messageListThreadMode")).toBe("collapsed");
	});
});
