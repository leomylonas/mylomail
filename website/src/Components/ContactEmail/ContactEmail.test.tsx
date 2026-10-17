// @vitest-environment jsdom

import { act } from "react";
import { hydrateRoot, type Root } from "react-dom/client";
import { renderToString } from "react-dom/server";
import { afterEach, expect, test, vi } from "vitest";
import { ContactEmail } from "@mylomail/website/Components/ContactEmail/ContactEmail";

let root: Root | undefined;
// Avoid publishing a regex-ready address in the public test source as well.
const expectedEmail = "info\x40mylo\x2esoftware";

afterEach(async () => {
	if (root) await act(async () => root?.unmount());
	root = undefined;
	document.body.replaceChildren();
	vi.unstubAllGlobals();
});

test("keeps the email out of prerendered HTML and reveals a usable address only after activation", async () => {
	vi.stubGlobal("IS_REACT_ACT_ENVIRONMENT", true);
	const container = document.createElement("div");
	container.innerHTML = renderToString(<ContactEmail />);
	document.body.append(container);

	expect(container.innerHTML).not.toContain(expectedEmail);
	expect(container.querySelector('a[href^="mailto:"]')).toBeNull();

	await act(async () => {
		root = hydrateRoot(container, <ContactEmail />);
	});
	expect(container.querySelector('a[href^="mailto:"]')).toBeNull();

	const button = container.querySelector("button")!;
	button.focus();
	await act(async () => button.click());

	const address = container.querySelector('a[href^="mailto:"]');
	expect(address?.textContent).toBe(expectedEmail);
	expect(address?.getAttribute("href")).toBe(`mailto:${expectedEmail}`);
	expect(button.getAttribute("aria-expanded")).toBe("true");
	expect(document.activeElement).toBe(button);
});
