import { randomBytes, randomUUID } from "node:crypto";
import { once } from "node:events";
import {
	existsSync,
	mkdirSync,
	mkdtempSync,
	readFileSync,
	realpathSync,
	rmSync,
	writeFileSync,
} from "node:fs";
import { createServer, type Server } from "node:http";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { expect, test } from "@playwright/test";
import { launchPackagedAttachedApp } from "@mylomail/renderer-e2e/AppFixture";
import { registerNativeAttachmentHandler } from "@mylomail/renderer-e2e/NativeAttachmentHandler";

interface AttachmentProof {
	path: string;
	content: string;
}

test("packaged Electron opens an attachment through the native desktop", async () => {
	test.skip(
		process.platform === "linux",
		"The full Linux attachment lifecycle test already exercises xdg-open.",
	);

	const root = mkdtempSync(join(tmpdir(), "mylomail-native-open-"));
	const marker = join(root, "opened.json");
	const associationToken = randomBytes(6).toString("hex");
	const handler = registerNativeAttachmentHandler(
		root,
		marker,
		associationToken,
	);
	const launchToken = randomBytes(32).toString("base64url");
	const backend = await startAttachmentBackend(launchToken);
	let launched:
		Awaited<ReturnType<typeof launchPackagedAttachedApp>> | undefined;

	try {
		launched = await launchPackagedAttachedApp(backend.origin, launchToken);
		await expect(
			launched.window.getByText("Native attachment smoke"),
		).toBeVisible({
			timeout: 30_000,
		});

		const attachmentDirectory = join(
			launched.dataDirectory,
			"tmp",
			"attachments",
			randomUUID().replaceAll("-", ""),
		);
		mkdirSync(attachmentDirectory, { recursive: true });
		const attachmentPath = join(
			attachmentDirectory,
			`proof${handler.extension}`,
		);
		const payload = Buffer.from("native attachment open proof\n", "utf8");
		writeFileSync(attachmentPath, payload, { mode: 0o600 });
		backend.setAttachmentPath(attachmentPath);

		const error = await launched.window.evaluate(
			async ([messageId, attachmentId]) => {
				if (!window.backend)
					throw new Error("The packaged preload bridge is unavailable.");
				return window.backend.openAttachment(messageId, attachmentId);
			},
			[randomUUID(), randomUUID()] as const,
		);
		expect(error).toBe("");
		if (handler.observesRead) {
			await expect
				.poll(
					() =>
						existsSync(marker)
							? (JSON.parse(readFileSync(marker, "utf8")) as AttachmentProof)
							: null,
					{ timeout: 15_000 },
				)
				.not.toBeNull();

			const proof = JSON.parse(readFileSync(marker, "utf8")) as AttachmentProof;
			const expectedPath = realpathSync.native(attachmentPath);
			const actualPath = realpathSync.native(proof.path);
			expect(actualPath.toLowerCase()).toBe(expectedPath.toLowerCase());
			expect(proof.content).toBe(payload.toString("base64"));
		}
	} finally {
		if (launched) {
			await launched.app.close();
			rmSync(dirname(launched.dataDirectory), { recursive: true, force: true });
		}
		await backend.close();
		handler.cleanup();
		rmSync(root, { recursive: true, force: true });
	}
});

async function startAttachmentBackend(launchToken: string): Promise<{
	origin: string;
	setAttachmentPath(path: string): void;
	close(): Promise<void>;
}> {
	let attachmentPath: string | undefined;
	const server = createServer((request, response) => {
		if (request.url === "/health") {
			response.statusCode =
				request.headers.authorization === `Bearer ${launchToken}` ? 200 : 401;
			response.end();
			return;
		}
		if (
			request.method === "GET" &&
			new URL(request.url ?? "/", "http://127.0.0.1").pathname === "/"
		) {
			response.setHeader("Content-Type", "text/html; charset=utf-8");
			response.end(
				"<!doctype html><title>Native attachment smoke</title><p>Native attachment smoke</p>",
			);
			return;
		}
		if (
			request.method === "POST" &&
			/^\/messages\/[0-9a-f-]+\/attachments\/[0-9a-f-]+\/open$/u.test(
				request.url ?? "",
			) &&
			attachmentPath
		) {
			response.setHeader("Content-Type", "application/json");
			response.end(JSON.stringify({ path: attachmentPath }));
			return;
		}
		response.statusCode = 404;
		response.end();
	});
	await listen(server);
	const address = server.address();
	if (typeof address !== "object" || address === null) {
		throw new Error("Native attachment backend did not bind a TCP port.");
	}

	return {
		origin: `http://127.0.0.1:${address.port}`,
		setAttachmentPath: (path) => {
			attachmentPath = path;
		},
		close: () => close(server),
	};
}

async function listen(server: Server): Promise<void> {
	server.listen(0, "127.0.0.1");
	await once(server, "listening");
}

async function close(server: Server): Promise<void> {
	server.close();
	await once(server, "close");
}
