/**
 * CSS Modules are resolved by the bundler, so TypeScript needs telling they exist.
 *
 * Typed as a plain record rather than generated per-file: a wrong class name shows up as a
 * missing style immediately, and generating types per module would add a build step for a
 * class of error that is already obvious when it happens.
 */
declare module "*.module.css" {
	const classes: Record<string, string>;
	export default classes;
}

/** Carbon ships its styles as Sass, imported for side effects only. */
declare module "*.scss";
