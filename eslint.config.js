import eslint from "@eslint/js";
import checkFile from "eslint-plugin-check-file";
import jsxA11y from "eslint-plugin-jsx-a11y";
import react from "eslint-plugin-react";
import reactHooks from "eslint-plugin-react-hooks";
import globals from "globals";
import tseslint from "typescript-eslint";

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

	// File naming. PascalCase throughout, including the generated output, so there is one
	// rule rather than a per-file-type exception nobody remembers.
	//
	// `index.ts` is the single exception, and it is not a style choice: module resolution
	// looks for that exact name when importing a directory, and the CI runner's filesystem
	// is case-sensitive even though macOS is not. `Index.ts` would resolve locally and fail
	// in CI.
	{
		files: [
			"apps/**/*.{ts,tsx}",
			"packages/ui/**/*.{ts,tsx}",
			"tests/**/*.{ts,tsx}",
		],
		ignores: ["**/index.ts", "**/index.tsx"],
		plugins: { "check-file": checkFile },
		rules: {
			"check-file/filename-naming-convention": [
				"error",
				{ "**/*.{ts,tsx}": "PASCAL_CASE" },
				{ ignoreMiddleExtensions: true },
			],
		},
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
