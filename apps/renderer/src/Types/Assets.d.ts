/** Bundled raster assets resolve to their emitted URL. */
declare module "*.png" {
	const source: string;
	export default source;
}

/** Bundled vector assets resolve to their emitted URL. */
declare module "*.svg" {
	const source: string;
	export default source;
}
