import { Resvg } from "@resvg/resvg-js";
import { mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { resolve } from "node:path";
import { fileURLToPath } from "node:url";

const root = resolve(import.meta.dirname, "..");
const markLightPath = resolve(root, "docs/branding/logos/mark-light.svg");
const markDarkPath = resolve(root, "docs/branding/logos/mark-dark.svg");
const iconDirectory = resolve(root, "assets/icons");
const linuxIconDirectory = resolve(iconDirectory, "linux");
const rendererBrandDirectory = resolve(root, "apps/renderer/src/Assets/Brand");

const iconSizes = [16, 24, 32, 48, 64, 128, 256] as const;
const linuxIconSizes = [16, 24, 32, 48, 64, 128, 256, 512] as const;
const icnsEntries = [
	{ size: 16, type: "icp4" },
	{ size: 32, type: "icp5" },
	{ size: 64, type: "icp6" },
	{ size: 128, type: "ic07" },
	{ size: 256, type: "ic08" },
	{ size: 512, type: "ic09" },
	{ size: 1024, type: "ic10" },
] as const;

export function generateIcons(): void {
	const lightMark = readFileSync(markLightPath, "utf8");
	const darkMark = readFileSync(markDarkPath, "utf8");
	const markGroup = extractMarkGroup(lightMark, markLightPath);
	const iconSvg = createIconSvg(markGroup);
	const traySvg = createTraySvg(markGroup);
	const renderedSizes = new Map<number, Buffer>();
	for (const size of new Set([
		...iconSizes,
		...linuxIconSizes,
		...icnsEntries.map((entry) => entry.size),
	])) {
		renderedSizes.set(size, renderPng(iconSvg, size));
	}

	mkdirSync(iconDirectory, { recursive: true });
	mkdirSync(linuxIconDirectory, { recursive: true });
	mkdirSync(rendererBrandDirectory, { recursive: true });
	writeFileSync(
		resolve(rendererBrandDirectory, "MarkLight.svg"),
		createHeaderMarkSvg(markGroup),
	);
	writeFileSync(
		resolve(rendererBrandDirectory, "MarkDark.svg"),
		createHeaderMarkSvg(extractMarkGroup(darkMark, markDarkPath)),
	);

	writeFileSync(resolve(iconDirectory, "app-icon.svg"), iconSvg);

	const iconPng = renderedSizes.get(1024)!;
	writeFileSync(resolve(iconDirectory, "app-icon.png"), iconPng);
	writeFileSync(resolve(rendererBrandDirectory, "Icon.png"), iconPng);
	writeFileSync(
		resolve(iconDirectory, "app-icon.ico"),
		createIco(renderedSizes),
	);
	writeFileSync(
		resolve(iconDirectory, "app-icon.icns"),
		createIcns(renderedSizes),
	);
	for (const size of linuxIconSizes) {
		writeFileSync(
			resolve(linuxIconDirectory, `${size}x${size}.png`),
			renderedSizes.get(size)!,
		);
	}
	writeFileSync(
		resolve(iconDirectory, "tray-template.png"),
		renderPng(traySvg, 44),
	);
}

function extractMarkGroup(svg: string, sourcePath: string): string {
	const markGroup = svg.match(/<g id="mark"[\s\S]*?<\/g>/)?.[0];
	if (!markGroup)
		throw new Error(`Approved mark does not contain #mark: ${sourcePath}`);
	return markGroup;
}

function createHeaderMarkSvg(markGroup: string): string {
	return `<svg xmlns="http://www.w3.org/2000/svg" viewBox="30 30 100 100">${markGroup}</svg>`;
}

function createIconSvg(markGroup: string): string {
	return `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 512 512"><rect width="512" height="512" rx="112" fill="#ffffff"/><svg x="40" y="40" width="432" height="432" viewBox="30 30 100 100">${markGroup}</svg></svg>`;
}

function createTraySvg(markGroup: string): string {
	const monochromeMark = markGroup.replaceAll(
		/fill="[^"]*"/g,
		'fill="#000000"',
	);
	return `<svg xmlns="http://www.w3.org/2000/svg" viewBox="30 30 100 100">${monochromeMark}</svg>`;
}

function renderPng(svg: string, size: number): Buffer {
	return new Resvg(svg, {
		fitTo: { mode: "width", value: size },
		font: { loadSystemFonts: false },
	})
		.render()
		.asPng();
}

function createIco(renderedSizes: ReadonlyMap<number, Buffer>): Buffer {
	const images = iconSizes.map((size) => ({
		size,
		png: renderedSizes.get(size)!,
	}));
	const directorySize = 6 + images.length * 16;
	const icon = Buffer.alloc(
		directorySize +
			images.reduce((total, image) => total + image.png.length, 0),
	);
	icon.writeUInt16LE(0, 0);
	icon.writeUInt16LE(1, 2);
	icon.writeUInt16LE(images.length, 4);

	let offset = directorySize;
	for (const [index, image] of images.entries()) {
		const entry = 6 + index * 16;
		icon[entry] = image.size === 256 ? 0 : image.size;
		icon[entry + 1] = image.size === 256 ? 0 : image.size;
		icon[entry + 2] = 0;
		icon[entry + 3] = 0;
		icon.writeUInt16LE(1, entry + 4);
		icon.writeUInt16LE(32, entry + 6);
		icon.writeUInt32LE(image.png.length, entry + 8);
		icon.writeUInt32LE(offset, entry + 12);
		image.png.copy(icon, offset);
		offset += image.png.length;
	}
	return icon;
}

function createIcns(renderedSizes: ReadonlyMap<number, Buffer>): Buffer {
	const chunks = icnsEntries.map(({ size, type }) => {
		const png = renderedSizes.get(size)!;
		const chunk = Buffer.alloc(8 + png.length);
		chunk.write(type, 0, 4, "ascii");
		chunk.writeUInt32BE(chunk.length, 4);
		png.copy(chunk, 8);
		return chunk;
	});
	const icns = Buffer.alloc(
		8 + chunks.reduce((total, chunk) => total + chunk.length, 0),
	);
	icns.write("icns", 0, 4, "ascii");
	icns.writeUInt32BE(icns.length, 4);
	let offset = 8;
	for (const chunk of chunks) {
		chunk.copy(icns, offset);
		offset += chunk.length;
	}
	return icns;
}

if (
	process.argv[1] &&
	resolve(process.argv[1]) === fileURLToPath(import.meta.url)
) {
	generateIcons();
}
