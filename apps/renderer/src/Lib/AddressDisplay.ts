/** What to call a correspondent: their name when they have one, otherwise their address. */
export function addressLabel(address: {
	name?: string | null;
	email: string;
}): string {
	const name = address.name?.trim();
	return name ? name : address.email;
}
