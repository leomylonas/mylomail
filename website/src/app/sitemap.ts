import type { MetadataRoute } from "next";
import { siteOrigin } from "@mylomail/website/Lib/Site";

export const dynamic = "force-static";

export default function sitemap(): MetadataRoute.Sitemap {
	return [
		{ url: `${siteOrigin}/`, changeFrequency: "monthly" },
		{ url: `${siteOrigin}/privacy/`, changeFrequency: "yearly" },
	];
}
