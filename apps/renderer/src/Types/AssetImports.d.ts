/** Vite's `?inline` suffix imports an asset as a base64 data URL. */
declare module "*.woff2?inline" {
	const dataUrl: string;
	export default dataUrl;
}
