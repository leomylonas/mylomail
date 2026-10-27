import { Search } from "@carbon/react";
import styles from "@mylomail/renderer/Components/SearchBox/SearchBox.module.css";

/**
 * Search input for the current account.
 *
 * Carbon's own component rather than a bare input, because it carries the label association,
 * clear-button semantics and keyboard behaviour that accessibility requires and that a
 * hand-rolled one quietly omits (§12).
 */
export function SearchBox({
	query,
	onChange,
	label = "Search mail",
	placeholder = "Search mail — try subject:invoice",
}: {
	query: string;
	onChange: (query: string) => void;
	label?: string;
	placeholder?: string;
}) {
	return (
		<div className={styles.box}>
			<Search
				className={styles.search}
				size="sm"
				labelText={label}
				placeholder={placeholder}
				value={query}
				onChange={(event) => onChange(event.target.value)}
				onClear={() => onChange("")}
			/>
		</div>
	);
}
