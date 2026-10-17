import type { Metadata } from "next";
import { PrivacyPage } from "@mylomail/website/Components/PrivacyPage/PrivacyPage";

export const metadata: Metadata = {
	title: "Privacy policy",
	description:
		"How the Mylo Mail website and desktop application handle local data, account providers, diagnostics, and privacy questions.",
	alternates: { canonical: "/privacy/" },
};

export default function Page() {
	return <PrivacyPage />;
}
