import { EventEmitter } from "node:events";
import type { ChildProcess } from "node:child_process";
import { describe, expect, it, vi } from "vitest";
import {
	credentialStoreUnavailableExitCode,
	startBackend,
	type BackendSupervisorOptions,
} from "@mylomail/electron-shell/BackendSupervisor";

class FakeChildProcess extends EventEmitter {
	public readonly stdin = { end: vi.fn() };
	public readonly stderr = new EventEmitter();
	public readonly kill = vi.fn();
	public killed = false;

	public exit(code: number): void {
		this.emit("exit", code, null);
	}
}

describe("startBackend", () => {
	it("restarts once with a prompted master password after the dedicated exit code", async () => {
		const children: FakeChildProcess[] = [];
		const environments: NodeJS.ProcessEnv[] = [];
		let spawnCount = 0;
		const spawnProcess: NonNullable<
			BackendSupervisorOptions["spawnProcess"]
		> = (_, __, options) => {
			environments.push(options.env);
			const child = new FakeChildProcess();
			children.push(child);
			spawnCount += 1;
			if (spawnCount === 1)
				queueMicrotask(() => child.exit(credentialStoreUnavailableExitCode));
			return child as unknown as ChildProcess;
		};
		const requestMasterPassword = vi.fn(async () => "master password");
		let readinessAttempts = 0;

		const backend = await startBackend({
			command: "backend",
			args: ["--test"],
			requestMasterPassword,
			spawnProcess,
			waitUntilReady: async () => {
				readinessAttempts += 1;
				if (readinessAttempts === 2) return 51423;
				await new Promise<void>(() => undefined);
				return 0;
			},
		});
		expect(requestMasterPassword).toHaveBeenCalledOnce();
		expect(environments[0].MYLOMAIL_LAUNCH_TOKEN).toBeUndefined();
		expect(environments[1].MYLOMAIL_LAUNCH_TOKEN).toBeUndefined();
		expect(environments[0].MYLOMAIL_MASTER_PASSWORD).toBeUndefined();
		expect(environments[1].MYLOMAIL_MASTER_PASSWORD).toBeUndefined();
		expect(children[1].stdin.end).toHaveBeenCalledWith(
			expect.stringContaining('"masterPassword":"master password"'),
		);

		// The restarted launch's port is what the renderer connects to; the first launch
		// never had one.
		expect(backend.port).toBe(51423);
	});

	it("says why a backend failed by including what it wrote to stderr", async () => {
		const child = new FakeChildProcess();
		vi.spyOn(process.stderr, "write").mockImplementation(() => true);
		const starting = startBackend({
			command: "backend",
			args: [],
			requestMasterPassword: vi.fn(),
			spawnProcess: () => child as unknown as ChildProcess,
			waitUntilReady: async () => await new Promise<number>(() => undefined),
		});
		child.stderr.emit(
			"data",
			Buffer.from("Unhandled exception. A pre-migration backup already exists"),
		);
		child.exit(134);

		await expect(starting).rejects.toThrow(
			/code 134\..*A pre-migration backup already exists/s,
		);
	});

	it("does not prompt when the backend exits for another reason", async () => {
		const child = new FakeChildProcess();
		const requestMasterPassword = vi.fn(async () => "master password");
		const starting = startBackend({
			command: "backend",
			args: [],
			requestMasterPassword,
			spawnProcess: () => child as unknown as ChildProcess,
			waitUntilReady: async () => await new Promise<number>(() => undefined),
		});
		child.exit(1);

		await expect(starting).rejects.toThrow(
			"Backend exited during startup with code 1.",
		);
		expect(child.kill).toHaveBeenCalledWith("SIGTERM");
		expect(requestMasterPassword).not.toHaveBeenCalled();
	});
});
