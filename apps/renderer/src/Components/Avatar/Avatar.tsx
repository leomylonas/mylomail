import type { CSSProperties } from "react";
import styles from "@mylomail/renderer/Components/Avatar/Avatar.module.css";

/**
 * A sender's initials on a colour derived from the name, so the same person keeps the same
 * colour in the list and the reading pane. Decorative: the name is always rendered next to it.
 */
export function Avatar({
	name,
	size = "md",
}: {
	name: string;
	size?: "sm" | "md" | "lg";
}) {
	return (
		<span
			className={`${styles.avatar} ${styles[size]}`}
			style={{ "--avatar-hue": hueFor(name) } as CSSProperties}
			aria-hidden="true"
		>
			{initialsFor(name)}
		</span>
	);
}

export function initialsFor(name: string): string {
	const words = name
		.replace(/<.*>/, "")
		.split(/[\s@._-]+/)
		.filter((word) => /\p{L}|\p{N}/u.test(word));
	if (words.length === 0) return "?";
	const first = [...words[0]!][0]!;
	const last = words.length > 1 ? [...words[words.length - 1]!][0]! : "";
	return (first + last).toLocaleUpperCase();
}

export function hueFor(name: string): number {
	let hash = 0;
	for (const character of name.toLocaleLowerCase())
		hash = (hash * 31 + character.codePointAt(0)!) % 360;
	return hash;
}
