import {
	closeSync,
	existsSync,
	openSync,
	readSync,
	readdirSync,
	statSync,
} from "node:fs";
import { join } from "node:path";
import { platform } from "node:process";

const expectedExtensions: Partial<Record<NodeJS.Platform, readonly string[]>> =
	{
		linux: [".AppImage", ".deb", ".rpm"],
		win32: [".exe", ".msi"],
		darwin: [".dmg", ".zip"],
	};

const artifactSignatures: Readonly<
	Record<string, { bytes: Buffer; offsetFromEnd?: number }>
> = {
	".appimage": { bytes: Buffer.from([0x7f, 0x45, 0x4c, 0x46]) },
	".deb": { bytes: Buffer.from("!<arch>\n", "ascii") },
	".rpm": { bytes: Buffer.from([0xed, 0xab, 0xee, 0xdb]) },
	".exe": { bytes: Buffer.from("MZ", "ascii") },
	".msi": {
		bytes: Buffer.from([0xd0, 0xcf, 0x11, 0xe0, 0xa1, 0xb1, 0x1a, 0xe1]),
	},
	".dmg": { bytes: Buffer.from("koly", "ascii"), offsetFromEnd: 512 },
	".zip": { bytes: Buffer.from("PK\u0003\u0004", "binary") },
};

const expected = expectedExtensions[platform];
if (!expected)
	throw new Error(`Package verification is not supported on ${platform}.`);

const packageDirectory = join(process.cwd(), "dist", "packages");
if (!existsSync(packageDirectory)) {
	throw new Error(`Package output does not exist: ${packageDirectory}`);
}

const files = readdirSync(packageDirectory, { withFileTypes: true })
	.filter((entry) => entry.isFile())
	.map((entry) => entry.name);

const verified: string[] = [];
for (const extension of expected) {
	const matches = files.filter((file) =>
		file.toLowerCase().endsWith(extension.toLowerCase()),
	);
	if (matches.length !== 1) {
		throw new Error(
			`Expected exactly one ${extension} package, found ${matches.length}: ${matches.join(", ") || "none"}.`,
		);
	}
	const file = matches[0];
	verifySignature(join(packageDirectory, file), extension);
	verified.push(file);
}

function verifySignature(path: string, extension: string): void {
	const signature = artifactSignatures[extension.toLowerCase()];
	const size = statSync(path).size;
	const offset = signature.offsetFromEnd ? size - signature.offsetFromEnd : 0;
	if (offset < 0 || size < signature.bytes.length) {
		throw new Error(`Package artifact is too small: ${path}`);
	}

	const actual = Buffer.alloc(signature.bytes.length);
	const descriptor = openSync(path, "r");
	try {
		const bytesRead = readSync(descriptor, actual, 0, actual.length, offset);
		if (bytesRead !== actual.length || !actual.equals(signature.bytes)) {
			throw new Error(
				`Package artifact has an invalid ${extension} signature: ${path}`,
			);
		}
	} finally {
		closeSync(descriptor);
	}
}

console.log(`Verified package artifacts: ${verified.join(", ")}`);
