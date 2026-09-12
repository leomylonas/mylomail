import { EventEmitter } from "node:events";
import type { ChildProcess } from "node:child_process";
import { describe, expect, it, vi } from "vitest";
import {
	waitForBackendHealth,
	waitForBackendOriginHealth,
} from "@mylomail/electron-shell/BackendHealthProbe";

class FakeStdout extends EventEmitter {
	public write(text: string): void {
		this.emit("data", Buffer.from(text, "utf8"));
	}
}

class FakeChildProcess extends EventEmitter {
	public stdout = new FakeStdout();

	public exit(code: number): void {
		this.emit("exit", code, null);
	}
}

const launch = (child: FakeChildProcess) => ({
	child: child as unknown as ChildProcess,
	launchToken: "token-abc",
});

const immediately = async () => undefined;

describe("waitForBackendHealth", () => {
	it("returns the announced port and authenticates with the launch token", async () => {
		const child = new FakeChildProcess();
		const fetchHealth = vi.fn(async () => ({ ok: true }));

		const pending = waitForBackendHealth(launch(child), {
			fetchHealth,
			delay: immediately,
		});
		child.stdout.write("MYLOMAIL_PORT=51423\n");

		// The OS assigns the port, so returning it is the only way anything else can address
		// the backend.
		await expect(pending).resolves.toBe(51423);
		expect(fetchHealth).toHaveBeenCalledWith(
			"http://127.0.0.1:51423/health",
			"token-abc",
		);
	});

	it("tolerates the announcement arriving split across chunks", async () => {
		const child = new FakeChildProcess();
		const pending = waitForBackendHealth(launch(child), {
			fetchHealth: async () => ({ ok: true }),
			delay: immediately,
		});

		child.stdout.write("MYLOMAIL_PO");
		child.stdout.write("RT=42\n");

		await expect(pending).resolves.toBe(42);
	});

	it("keeps polling until health answers", async () => {
		const child = new FakeChildProcess();
		let attempts = 0;
		const fetchHealth = vi.fn(async () => {
			attempts += 1;
			// Kestrel announces its port when it binds, which is not when the pipeline is
			// ready to answer.
			if (attempts < 3) throw new Error("ECONNREFUSED");
			return { ok: true };
		});

		const pending = waitForBackendHealth(launch(child), {
			fetchHealth,
			delay: immediately,
		});
		child.stdout.write("MYLOMAIL_PORT=8080\n");

		await expect(pending).resolves.toBe(8080);
		expect(attempts).toBe(3);
	});

	it("rejects when the backend exits before announcing a port", async () => {
		const child = new FakeChildProcess();
		const pending = waitForBackendHealth(launch(child), {
			fetchHealth: async () => ({ ok: true }),
			delay: immediately,
		});

		// The supervisor tells a credential-unlock exit from a failure by this rejection, so
		// hanging here would turn a recoverable exit into a stuck launch.
		child.exit(78);

		await expect(pending).rejects.toThrow(/exited before reporting its port/);
	});

	it("gives up rather than waiting forever for a silent backend", async () => {
		const child = new FakeChildProcess();

		await expect(
			waitForBackendHealth(launch(child), {
				timeoutMs: 0,
				fetchHealth: async () => ({ ok: true }),
				delay: immediately,
			}),
		).rejects.toThrow(/did not report a port in time/);
	});

	it("gives up when health never becomes ready", async () => {
		const child = new FakeChildProcess();
		const pending = waitForBackendHealth(launch(child), {
			timeoutMs: 20,
			fetchHealth: async () => ({ ok: false }),
			delay: immediately,
		});
		child.stdout.write("MYLOMAIL_PORT=9000\n");

		await expect(pending).rejects.toThrow(/did not become healthy/);
	});

	it("stops listening once it has the port", async () => {
		const child = new FakeChildProcess();
		const pending = waitForBackendHealth(launch(child), {
			fetchHealth: async () => ({ ok: true }),
			delay: immediately,
		});
		child.stdout.write("MYLOMAIL_PORT=7000\n");
		await pending;

		// Left attached, every later line of backend logging would be buffered forever.
		expect(child.stdout.listenerCount("data")).toBe(0);
		expect(child.listenerCount("exit")).toBe(0);
	});
});

describe("waitForBackendOriginHealth", () => {
	it("polls the supplied origin with its shared launch token", async () => {
		const fetchHealth = vi
			.fn<() => Promise<{ ok: boolean }>>()
			.mockResolvedValueOnce({ ok: false })
			.mockResolvedValueOnce({ ok: true });

		await waitForBackendOriginHealth(
			"http://127.0.0.1:6123",
			"attached-token",
			{ fetchHealth, delay: immediately },
		);

		expect(fetchHealth).toHaveBeenCalledTimes(2);
		expect(fetchHealth).toHaveBeenLastCalledWith(
			"http://127.0.0.1:6123/health",
			"attached-token",
		);
	});
});
