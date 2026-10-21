import type { Metadata } from "next";
import type { ReactNode } from "react";
import { SiteShell } from "@mylomail/website/Components/SiteShell/SiteShell";
import { siteDescription, siteOrigin } from "@mylomail/website/Lib/Site";
import "@mylomail/website/Styles/Globals.css";

export const metadata: Metadata = {
	metadataBase: new URL(siteOrigin),
	title: {
		default: "Mylo Mail — Free, open-source email and calendar",
		template: "%s | Mylo Mail",
	},
	description: siteDescription,
	openGraph: {
		type: "website",
		siteName: "Mylo Mail",
		title: "Mylo Mail",
		description: siteDescription,
		images: [
			{ url: "/Brand/Icon.png", width: 1024, height: 1024, alt: "Mylo Mail" },
		],
	},
	icons: {
		icon: [
			{ url: "/Brand/Icon.svg", type: "image/svg+xml" },
			{ url: "/favicon.ico" },
		],
		apple: "/Brand/Icon.png",
	},
};

export default function RootLayout({ children }: { children: ReactNode }) {
	return (
		<html lang="en">
			<head>
				<link
					rel="preload"
					href="/Fonts/IBMPlexSans-Regular.woff2"
					as="font"
					type="font/woff2"
					crossOrigin="anonymous"
				/>
				<link
					rel="preload"
					href="/Fonts/IBMPlexSans-SemiBold.woff2"
					as="font"
					type="font/woff2"
					crossOrigin="anonymous"
				/>
			</head>
			<body>
				<SiteShell>{children}</SiteShell>
			</body>
		</html>
	);
}
