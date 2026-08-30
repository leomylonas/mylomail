import eslint from "@eslint/js";
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
);
