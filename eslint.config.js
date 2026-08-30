import { basename, dirname } from "node:path";

import eslint from "@eslint/js";
import checkFile from "eslint-plugin-check-file";
import jsxA11y from "eslint-plugin-jsx-a11y";
import react from "eslint-plugin-react";
import reactHooks from "eslint-plugin-react-hooks";
import globals from "globals";
import tseslint from "typescript-eslint";

/**
 * A `.tsx` file must be named after the PascalCase folder it sits in.
 *
 * Expressed as a local rule because no off-the-shelf rule relates a file's name to its
 * folder's, and the convention is only meaningful as that relationship: a PascalCase folder
 * and a PascalCase file, checked independently, would happily accept
 * `MessageList/ReadingPane.tsx`.
 */
const componentFolder = {
	meta: {
		type: "problem",
		docs: {
			description:
				"A component .tsx must be named after its PascalCase folder.",
		},
		schema: [],
	},
	create(context) {
		return {
			Program(node) {
				// Strip every extension, so `MessageList.test.tsx` still checks `MessageList`.
				const base = basename(context.filename).split(".")[0];
				const folder = basename(dirname(context.filename));

				if (!/^[A-Z][A-Za-z0-9]*$/.test(folder)) {
					context.report({
						node,
						message: `Component folder "${folder}" must be PascalCase.`,
					});
					return;
				}

				if (base !== folder) {
					context.report({
						node,
						message: `"${base}.tsx" must sit in a folder named "${base}", not "${folder}". A component owns its directory, so its styles, store and child components can sit beside it.`,
					});
				}
			},
		};
	},
};

const localPlugin = { rules: { "component-folder": componentFolder } };

export default tseslint.config(
	{
		ignores: [
			"**/bin/**",
			"**/obj/**",
			"**/dist/**",
			"packages/shared-types/src/**",
		],
	},
	eslint.configs.recommended,
	...tseslint.configs.recommended,
	{
		files: ["**/*.{js,ts,tsx}"],
		languageOptions: { globals: { ...globals.node, ...globals.browser } },
	},

	// PascalCase files and PascalCase folders, everywhere, including generated output. One
	// rule for the whole tree — no layer-versus-component distinction to adjudicate, and
	// nothing that depends on what a folder happens to contain.
	//
	// `src` itself is excluded: it is a package-layout convention, not part of an import
	// path (see the `paths` mapping in tsconfig.json).
	{
		files: [
			"apps/**/*.{ts,tsx}",
			"packages/ui/**/*.{ts,tsx}",
			"tests/**/*.{ts,tsx}",
		],
		plugins: { "check-file": checkFile },
		rules: {
			"check-file/filename-naming-convention": [
				"error",
				{ "**/!(index|Index).{ts,tsx}": "PASCAL_CASE" },
				{ ignoreMiddleExtensions: true },
			],
			"check-file/folder-naming-convention": [
				"error",
				{
					"apps/renderer/src/**/": "PASCAL_CASE",
					"apps/electron-shell/src/**/": "PASCAL_CASE",
					"packages/ui/src/**/": "PASCAL_CASE",
				},
			],
			// No barrel files. Imports name the module they come from in full, so a symbol
			// is greppable to exactly one path and a re-export cannot quietly rename it.
			"check-file/filename-blocklist": [
				"error",
				{
					// Exempted from the casing rule above so this speaks once, and clearly.
					"**/{index,Index}.{ts,tsx}":
						"*.{ts,tsx} — barrel files are not used; import the module by its full path.",
				},
			],
		},
	},

	// A component owns a directory: `MessageList/MessageList.tsx`, beside
	// `MessageList.module.css`, its store, and any child component it alone uses.
	// Colocation only works if the component owns a folder, and the folder is only
	// navigable if it carries the component's name.
	//
	// Entry points sitting directly in `src/` are exempt — they belong to no component.
	{
		files: ["apps/renderer/src/*/**/*.tsx", "packages/ui/src/*/**/*.tsx"],
		plugins: { local: localPlugin },
		rules: { "local/component-folder": "error" },
	},

	// React. Scoped to the renderer and the shared component library — the Electron main
	// process and the tooling scripts are Node, and these rules do not apply there.
	{
		files: ["apps/renderer/**/*.{ts,tsx}", "packages/ui/**/*.{ts,tsx}"],
		plugins: { react, "react-hooks": reactHooks, "jsx-a11y": jsxA11y },
		languageOptions: {
			globals: globals.browser,
			parserOptions: { ecmaFeatures: { jsx: true } },
		},
		settings: {
			// Pinned rather than "detect": React is not a dependency yet, and detection
			// throws when it is absent. Update this when §12's React 19 lands.
			react: { version: "19.0" },
		},
		rules: {
			...react.configs.flat.recommended.rules,
			...react.configs.flat["jsx-runtime"].rules,
			...reactHooks.configs["recommended-latest"].rules,
			...jsxA11y.flatConfigs.recommended.rules,

			// Accessibility is a continuous requirement (§15), not a late pass. Carbon
			// supplies accessible primitives, but TanStack Table and Virtual are headless,
			// so the markup around them is ours to get right.
			"jsx-a11y/no-autofocus": "error",

			// Generated types are consumed through the package entry point. Reaching into
			// its source couples callers to a layout the generator owns and rewrites.
			"no-restricted-imports": [
				"error",
				{
					patterns: [
						{
							group: ["**/shared-types/src/**"],
							message:
								"Import from @mylomail/shared-types, not by path into its source.",
						},
					],
				},
			],
		},
	},

	// Components are function declarations, not arrow constants, so stack traces and React
	// DevTools carry a name.
	{
		files: ["apps/renderer/**/*.tsx", "packages/ui/**/*.tsx"],
		rules: {
			"react/function-component-definition": [
				"error",
				{ namedComponents: "function-declaration" },
			],
			"react/jsx-no-useless-fragment": "error",
			"react/self-closing-comp": "error",
		},
	},

	// Named exports only. A default export lets the same module be imported under different
	// names, so the same component ends up with several names across the codebase.
	{
		files: ["apps/**/*.{ts,tsx}", "packages/ui/**/*.{ts,tsx}"],
		ignores: ["**/*.config.{ts,js}", "**/index.ts"],
		rules: {
			"no-restricted-syntax": [
				"error",
				{
					selector: "ExportDefaultDeclaration",
					message: "Use a named export.",
				},
			],
		},
	},

	// Tooling scripts are Node and are not subject to the frontend naming rule.
	{
		files: ["scripts/**/*.ts", "*.js", "*.ts"],
		languageOptions: { globals: globals.node },
	},
);
