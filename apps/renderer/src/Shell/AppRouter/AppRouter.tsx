import { useState } from "react";
import {
	createMemoryHistory,
	createRootRoute,
	createRoute,
	createRouter,
	RouterProvider,
} from "@tanstack/react-router";
import { z } from "zod";
import { AppShell } from "@mylomail/renderer/Shell/AppShell/AppShell";
import { ComposeWindow } from "@mylomail/renderer/Shell/Windows/ComposeWindow/ComposeWindow";
import { MessageWindow } from "@mylomail/renderer/Shell/Windows/MessageWindow/MessageWindow";
import { windowRouteFromSearch } from "@mylomail/renderer/Shell/WindowRoute";

const windowSearchSchema = z.object({
	message: z.string().optional(),
	subject: z.string().optional(),
	sender: z.string().optional(),
	compose: z.string().optional(),
	account: z.string().optional(),
	notification: z.string().optional(),
	mailto: z.string().optional(),
});

const rootRoute = createRootRoute();
const windowRoute = createRoute({
	getParentRoute: () => rootRoute,
	path: "/",
	validateSearch: windowSearchSchema,
	component: WindowRoot,
});
const routeTree = rootRoute.addChildren([windowRoute]);

function WindowRoot() {
	const search = windowRoute.useSearch();
	const route = windowRouteFromSearch(search);
	switch (route.kind) {
		case "message":
			return (
				<MessageWindow
					messageId={route.messageId}
					subject={route.subject}
					senderAddress={route.senderAddress}
				/>
			);
		case "compose":
			return (
				<ComposeWindow draftId={route.draftId} accountId={route.accountId} />
			);
		case "shell":
			return (
				<AppShell
					initialNotification={route.initialNotification}
					initialMailto={route.initialMailto}
				/>
			);
	}
}

/** Each renderer window owns its own in-memory router and typed search state. */
function createWindowRouter() {
	return createRouter({
		routeTree,
		history: createMemoryHistory({
			initialEntries: [`/${window.location.search}`],
		}),
	});
}

export function AppRouter() {
	const [router] = useState(createWindowRouter);
	return <RouterProvider router={router} />;
}

declare module "@tanstack/react-router" {
	interface Register {
		router: ReturnType<typeof createWindowRouter>;
	}
}
