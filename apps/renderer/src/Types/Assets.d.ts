/** Bundled raster assets resolve to their emitted URL. */
declare module "*.png" {
	const source: string;
	export default source;
}
