import { describe, expect, it } from "vitest";
import { isDangerousAttachment } from "@mylomail/electron-shell/DangerousAttachment";

describe("isDangerousAttachment", () => {
	it("flags well-known executable extensions the original list already covered", () => {
		expect(isDangerousAttachment("/tmp/attachments/x/invoice.exe")).toBe(true);
		expect(isDangerousAttachment("/tmp/attachments/x/setup.msi")).toBe(true);
	});

	it("flags script/shortcut extensions that were previously missing", () => {
		expect(isDangerousAttachment("/tmp/attachments/x/photo.scr")).toBe(true);
		expect(isDangerousAttachment("/tmp/attachments/x/invoice.js")).toBe(true);
		expect(isDangerousAttachment("/tmp/attachments/x/reminder.vbs")).toBe(true);
		expect(isDangerousAttachment("/tmp/attachments/x/shortcut.lnk")).toBe(true);
		expect(isDangerousAttachment("/tmp/attachments/x/helper.jar")).toBe(true);
		expect(isDangerousAttachment("/tmp/attachments/x/install.hta")).toBe(true);
	});

	it("does not flag ordinary document extensions", () => {
		expect(isDangerousAttachment("/tmp/attachments/x/report.pdf")).toBe(false);
		expect(isDangerousAttachment("/tmp/attachments/x/photo.png")).toBe(false);
	});

	it("matches only the final extension of a double-extension filename", () => {
		expect(isDangerousAttachment("/tmp/attachments/x/invoice.pdf.exe")).toBe(
			true,
		);
		expect(isDangerousAttachment("/tmp/attachments/x/invoice.exe.pdf")).toBe(
			false,
		);
	});
});
