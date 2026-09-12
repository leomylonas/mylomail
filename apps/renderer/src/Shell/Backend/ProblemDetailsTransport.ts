import type { HubConnection } from "@microsoft/signalr";
import { ErrorCategory } from "@mylomail/shared-types/SignalR/MyloMail.Api.Errors";
import {
	present,
	type ErrorPresentation,
} from "@mylomail/renderer/Shell/Registries/Errors/ErrorPresentation";
import type { AppNotification } from "@mylomail/renderer/Shell/Registries/Notifications/Notifications";

export interface MutationProblem {
	type?: string;
	title?: string;
	status?: number;
	detail?: string;
	instance?: string;
	category: ErrorCategory;
	providerCode?: string;
	extensions: Record<string, unknown>;
}

export class MutationTransportError extends Error {
	readonly presentation: ErrorPresentation;

	constructor(
		readonly problem: MutationProblem,
		options?: ErrorOptions,
	) {
		const presentation = present(
			problem.category,
			problem.detail,
			problem.extensions,
		);
		super(presentation.detail, options);
		this.name = "MutationTransportError";
		this.presentation = presentation;
	}
}

/** Fetches a same-origin API resource and turns every transport/status failure into one shape. */
export async function fetchApi(
	input: RequestInfo | URL,
	init?: RequestInit,
): Promise<Response> {
	let response: Response;
	try {
		response =
			init === undefined ? await fetch(input) : await fetch(input, init);
	} catch (error) {
		throw new MutationTransportError(problem(ErrorCategory.Network), {
			cause: error,
		});
	}

	if (response.ok) return response;
	throw await errorFromResponse(response);
}

/** Installs the same deserialisation boundary around every SignalR invocation. */
export function normalizeHubErrors(hub: HubConnection): void {
	const invoke = hub.invoke.bind(hub);
	hub.invoke = (<T>(methodName: string, ...args: unknown[]): Promise<T> =>
		invoke<T>(methodName, ...args).catch((error: unknown) => {
			throw normalizeHubError(error);
		})) as HubConnection["invoke"];
}

export function normalizeHubError(error: unknown): MutationTransportError {
	if (error instanceof MutationTransportError) return error;
	const decoded = decodeProblem(error);
	return decoded
		? new MutationTransportError(decoded, { cause: error })
		: new MutationTransportError(problem(ErrorCategory.Network), {
				cause: error,
			});
}

export function notificationForError(
	error: unknown,
	fallbackTitle: string,
	actions: Partial<
		Record<NonNullable<ErrorPresentation["action"]>, () => void>
	> = {},
): Omit<AppNotification, "id"> {
	const decoded = decodeProblem(error);
	if (decoded && !(error instanceof MutationTransportError)) {
		error = new MutationTransportError(decoded, { cause: error });
	}
	if (!(error instanceof MutationTransportError)) {
		return {
			kind: "error",
			title: fallbackTitle,
			detail: error instanceof Error ? error.message : String(error),
		};
	}

	const { presentation } = error;
	const run = presentation.action ? actions[presentation.action] : undefined;
	return {
		kind: "error",
		title: presentation.title,
		detail: presentation.detail,
		persistent: !presentation.transient,
		action:
			presentation.action && run
				? { label: actionLabel(presentation.action), run }
				: undefined,
	};
}

export function decodeProblem(error: unknown): MutationProblem | null {
	if (error instanceof MutationTransportError) return error.problem;
	const message = error instanceof Error ? error.message : String(error);
	const direct = parseProblem(message);
	if (direct) return direct;

	// SignalR prefixes HubException messages with invocation context. The server-provided JSON
	// remains intact after that prefix, so try every object start rather than depending on one
	// package-version-specific sentence.
	for (
		let start = message.indexOf("{");
		start >= 0;
		start = message.indexOf("{", start + 1)
	) {
		for (
			let end = message.lastIndexOf("}");
			end > start;
			end = message.lastIndexOf("}", end - 1)
		) {
			const candidate = parseProblem(message.slice(start, end + 1));
			if (candidate) return candidate;
		}
	}
	return null;
}

async function errorFromResponse(
	response: Response,
): Promise<MutationTransportError> {
	const text = await response.text().catch(() => "");
	const decoded = parseProblem(text);
	return new MutationTransportError(
		decoded ??
			problem(
				categoryForStatus(response.status),
				`The server rejected the request (${response.status}).`,
				response.status,
			),
	);
}

function parseProblem(text: string): MutationProblem | null {
	if (!text.trim()) return null;
	try {
		const value = JSON.parse(text) as unknown;
		if (!value || typeof value !== "object") return null;
		const record = value as Record<string, unknown>;
		const category = parseCategory(record.category);
		if (category === null) return null;

		const nestedExtensions =
			record.extensions && typeof record.extensions === "object"
				? (record.extensions as Record<string, unknown>)
				: {};
		const extensions = { ...nestedExtensions };
		for (const [key, item] of Object.entries(record)) {
			if (!standardKeys.has(key)) extensions[key] = item;
		}
		return {
			type: stringValue(record.type),
			title: stringValue(record.title),
			status: numberValue(record.status),
			detail: stringValue(record.detail),
			instance: stringValue(record.instance),
			category,
			providerCode: stringValue(record.providerCode),
			extensions,
		};
	} catch {
		return null;
	}
}

function parseCategory(value: unknown): ErrorCategory | null {
	if (
		typeof value === "number" &&
		Number.isInteger(value) &&
		value >= ErrorCategory.Network &&
		value <= ErrorCategory.Unknown
	) {
		return value as ErrorCategory;
	}
	if (typeof value === "string") {
		const named = ErrorCategory[value as keyof typeof ErrorCategory];
		return typeof named === "number" ? named : null;
	}
	return null;
}

function problem(
	category: ErrorCategory,
	detail?: string,
	status?: number,
): MutationProblem {
	return { category, detail, status, extensions: {} };
}

function categoryForStatus(status: number): ErrorCategory {
	if (status === 401 || status === 403) return ErrorCategory.Auth;
	if (status === 409) return ErrorCategory.Conflict;
	if (status === 429) return ErrorCategory.RateLimit;
	if (status === 502 || status === 503 || status === 504)
		return ErrorCategory.Network;
	if (status >= 400 && status < 500) return ErrorCategory.Validation;
	return ErrorCategory.Unknown;
}

function stringValue(value: unknown): string | undefined {
	return typeof value === "string" ? value : undefined;
}

function numberValue(value: unknown): number | undefined {
	return typeof value === "number" ? value : undefined;
}

function actionLabel(action: NonNullable<ErrorPresentation["action"]>): string {
	switch (action) {
		case "reauthenticate":
			return "Sign in";
		case "resolve":
			return "Resolve";
		case "trust-certificate":
			return "Trust certificate";
		case "retry":
		default:
			return "Retry";
	}
}

const standardKeys = new Set([
	"type",
	"title",
	"status",
	"detail",
	"instance",
	"category",
	"providerCode",
	"extensions",
]);
