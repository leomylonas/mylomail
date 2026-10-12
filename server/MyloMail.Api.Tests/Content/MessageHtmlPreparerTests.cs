using MyloMail.Api.Content;
using Xunit;

namespace MyloMail.Api.Tests.Content;

/// <summary>
/// Message HTML is the most security-sensitive surface in the app: it is authored by whoever sent
/// the mail, and the renderer displaying it also holds the capability to mutate mail (§13). These
/// are the attacks, not the happy path.
/// </summary>
public sealed class MessageHtmlPreparerTests
{
	private static PreparedHtml Prepare(string html, bool allowRemote = false, Func<string, string?>? inline = null) =>
		MessageHtmlPreparer.Prepare(html, allowRemote, inline);

	[Fact]
	public void Ordinary_formatting_is_kept()
	{
		var html = Prepare("<p>Hello <b>there</b></p>").Html;

		Assert.Contains("<b>there</b>", html);
	}

	[Fact]
	public void Layout_attributes_classes_and_font_faces_a_newsletter_relies_on_survive()
	{
		var html = Prepare("<table width=\"600\" bgcolor=\"#fff\" cellpadding=\"0\"><tr><td class=\"hero\" id=\"top\" align=\"center\"><font face=\"Arial\" size=\"2\" color=\"red\">x</font></td></tr></table>").Html;

		Assert.Contains("class=\"hero\"", html);
		Assert.Contains("id=\"top\"", html);
		Assert.Contains("face=\"Arial\"", html);
		Assert.Contains("bgcolor=\"#fff\"", html);
	}

	[Fact]
	public void Scripts_are_removed()
	{
		var html = Prepare("<p>hi</p><script>alert(\"x\")</script>").Html;

		Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("alert", html);
	}

	[Fact]
	public void Event_handlers_are_removed()
	{
		var html = Prepare("<img onerror=\"alert(1)\" src=\"cid:x\">").Html;

		Assert.DoesNotContain("onerror", html, StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>A form in a message body exists to collect credentials. There is no benign use.</summary>
	[Theory]
	[InlineData("<form action=\"https://evil.example\"><input name=\"password\"></form>")]
	[InlineData("<input name=\"password\" type=\"password\">")]
	[InlineData("<button formaction=\"https://evil.example\">go</button>")]
	[InlineData("<select><option>a</option></select><textarea>t</textarea>")]
	public void Forms_and_their_controls_are_removed(string html)
	{
		var result = Prepare(html).Html.ToLowerInvariant();

		foreach (var tag in new[] { "<form", "<input", "<button", "<select", "<textarea", "<option" })
		{
			Assert.DoesNotContain(tag, result);
		}

		Assert.DoesNotContain("evil.example", result);
	}

	[Theory]
	[InlineData("<iframe src=\"https://evil.example\"></iframe>")]
	[InlineData("<object data=\"https://evil.example/x\"></object>")]
	[InlineData("<embed src=\"https://evil.example/x\">")]
	[InlineData("<base href=\"https://evil.example/\">")]
	[InlineData("<meta http-equiv=\"refresh\" content=\"0;url=https://evil.example\">")]
	public void Frames_objects_embeds_base_and_meta_are_removed(string html)
	{
		var result = Prepare(html).Html.ToLowerInvariant();

		Assert.DoesNotContain("evil.example", result);
		foreach (var tag in new[] { "<iframe", "<object", "<embed", "<base", "<meta" })
		{
			Assert.DoesNotContain(tag, result);
		}
	}

	[Theory]
	[InlineData("<a href=\"javascript:alert(1)\">click</a>")]
	[InlineData("<a href=\"JaVaScRiPt:alert(1)\">click</a>")]
	[InlineData("<a href=\"vbscript:msgbox(1)\">click</a>")]
	public void Script_links_are_removed(string html)
	{
		var result = Prepare(html).Html.ToLowerInvariant();

		Assert.DoesNotContain("javascript:", result);
		Assert.DoesNotContain("vbscript:", result);
	}

	[Fact]
	public void A_link_cannot_name_a_target_or_carry_data_attributes()
	{
		var html = Prepare("<a href=\"https://example.org\" target=\"_blank\" data-track=\"1\" ping=\"https://evil.example/p\">x</a>").Html;

		Assert.Contains("href=\"https://example.org\"", html);
		Assert.DoesNotContain("target", html);
		Assert.DoesNotContain("data-track", html);
		Assert.DoesNotContain("ping", html);
		Assert.DoesNotContain("evil.example", html);
	}

	[Theory]
	[InlineData("<a href=\"/accounts\">x</a>")]
	[InlineData("<img src=\"/accounts\">")]
	[InlineData("<a href=\"messages/abc\">x</a>")]
	public void Relative_urls_are_removed_because_they_would_resolve_against_the_backend(string html)
	{
		var result = Prepare(html, allowRemote: true).Html;

		Assert.DoesNotContain("/accounts", result);
		Assert.DoesNotContain("messages/abc", result);
	}

	/// <summary>Classic vectors, each of which has beaten some sanitiser: none may leave script or a script URL.</summary>
	[Theory]
	[InlineData("<img src=x onerror=alert(1)>")]
	[InlineData("<svg onload=alert(1)>")]
	[InlineData("<body onload=alert(1)>")]
	[InlineData("<a href=\"java&#09;script:alert(1)\">x</a>")]
	[InlineData("<a href=\"&#106;avascript:alert(1)\">x</a>")]
	[InlineData("<a href=\" \tjavascript:alert(1)\">x</a>")]
	[InlineData("<iframe srcdoc=\"<script>alert(1)</script>\"></iframe>")]
	[InlineData("<math><mtext><table><mglyph><style><!--</style><img title=\"--&gt;&lt;img src=1 onerror=alert(1)&gt;\">")]
	[InlineData("<noscript><p title=\"</noscript><img src=x onerror=alert(1)>\">")]
	[InlineData("<style></style><script>alert(1)</script>")]
	[InlineData("<link rel=stylesheet href=https://evil.example/x.css>")]
	[InlineData("<div style=\"width:expression(alert(1))\">x</div>")]
	[InlineData("<div style=\"behavior:url(https://evil.example/x.htc)\">x</div>")]
	[InlineData("<div style=\"-moz-binding:url(https://evil.example/x.xml#a)\">x</div>")]
	[InlineData("<template><script>alert(1)</script></template>")]
	[InlineData("<details open ontoggle=alert(1)>")]
	[InlineData("<a href=\"data:text/html,<script>alert(1)</script>\">x</a>")]
	public void Known_script_vectors_leave_nothing_executable(string html)
	{
		foreach (var allowRemote in new[] { false, true })
		{
			var result = Prepare(html, allowRemote).Html.ToLowerInvariant();

			Assert.DoesNotContain("<script", result);
			Assert.DoesNotContain("javascript:", result);
			Assert.DoesNotContain("onerror", result);
			Assert.DoesNotContain("onload", result);
			Assert.DoesNotContain("ontoggle", result);
			Assert.DoesNotContain("expression(", result);
			Assert.DoesNotContain("behavior", result);
			Assert.DoesNotContain("binding", result);
			Assert.DoesNotContain("evil.example", result);
			Assert.DoesNotContain("<iframe", result);
		}
	}

	public sealed class RemoteContent
	{
		[Fact]
		public void Remote_images_are_withheld_and_counted()
		{
			var result = Prepare("<img src=\"https://tracker.example/pixel.gif\">");

			Assert.DoesNotContain("tracker.example", result.Html);
			Assert.Equal(1, result.BlockedRemoteCount);
		}

		[Fact]
		public void A_protocol_relative_url_is_remote()
		{
			var result = Prepare("<img src=\"//tracker.example/p.gif\">");

			Assert.DoesNotContain("tracker.example", result.Html);
			Assert.Equal(1, result.BlockedRemoteCount);
		}

		/// <summary>
		/// §13 is explicit that removing <c>src</c> is not enough. A tracking pixel in a background
		/// image works exactly as well as one in an img tag.
		/// </summary>
		[Fact]
		public void Remote_content_hidden_in_inline_styles_is_withheld_but_the_rest_of_the_style_stays()
		{
			var result = Prepare("<div style=\"color: blue; background: url(https://tracker.example/p.gif)\">hi</div>");

			Assert.DoesNotContain("tracker.example", result.Html);
			Assert.Contains("blue", result.Html.Replace("rgba(0, 0, 255, 1)", "blue"));
			Assert.Equal(1, result.BlockedRemoteCount);
		}

		[Fact]
		public void The_background_attribute_is_withheld()
		{
			var result = Prepare("<table background=\"https://tracker.example/bg.png\"><tr><td>x</td></tr></table>");

			Assert.DoesNotContain("tracker.example", result.Html);
			Assert.Equal(1, result.BlockedRemoteCount);
		}

		[Theory]
		[InlineData("<img srcset=\"https://tracker.example/p.gif 1x\" alt=\"\">")]
		[InlineData("<img poster=\"https://tracker.example/p.gif\" alt=\"\">")]
		public void Srcset_and_poster_load_independently_of_src_and_are_never_allowed(string html)
		{
			var result = Prepare(html);

			Assert.DoesNotContain("tracker.example", result.Html);
			Assert.Equal(1, result.BlockedRemoteCount);
		}

		[Fact]
		public void A_style_element_loses_its_remote_references_but_keeps_its_layout()
		{
			var result = Prepare(
				"<style>@import url(https://tracker.example/s.css); .hero { color: red; background-image: url(https://tracker.example/b.png) } @media (max-width: 600px) { .hero { width: 100% } }</style><p class=\"hero\">hi</p>"
			);

			Assert.DoesNotContain("tracker.example", result.Html);
			Assert.Contains(".hero", result.Html);
			Assert.Contains("@media", result.Html);
			Assert.Equal(1, result.BlockedRemoteCount);
		}

		[Fact]
		public void A_style_element_before_the_content_is_kept()
		{
			var result = Prepare("<html><head><style>.h{color:blue}</style></head><body><p class=\"h\">hi</p></body></html>");

			Assert.Contains("<style>", result.Html);
			Assert.Contains("class=\"h\"", result.Html);
		}

		[Fact]
		public void Remote_content_loads_once_the_user_asks()
		{
			var result = Prepare("<img src=\"https://example.org/p.gif\"><div style=\"background:url(https://example.org/b.png)\">x</div>", allowRemote: true);

			Assert.Contains("https://example.org/p.gif", result.Html);
			Assert.Contains("https://example.org/b.png", result.Html);
			Assert.Equal(0, result.BlockedRemoteCount);
		}

		/// <summary>Consent to images is never consent to scripts.</summary>
		[Fact]
		public void Scripts_are_still_removed_when_remote_content_is_allowed()
		{
			var html = Prepare("<script>alert(1)</script><img src=\"https://a.example/p\">", allowRemote: true).Html;

			Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
		}

		/// <summary>
		/// An ordinary hyperlink's <c>href</c> never fetches on its own — only navigates on click.
		/// Blocking every <c>href</c> would strip every http link in the body, which is not what this
		/// defence is for.
		/// </summary>
		[Fact]
		public void An_ordinary_hyperlink_is_kept_and_not_counted()
		{
			var result = Prepare("<a href=\"https://example.org/page\">a link</a><a href=\"mailto:a@example.org\">m</a>");

			Assert.Contains("href=\"https://example.org/page\"", result.Html);
			Assert.Contains("href=\"mailto:a@example.org\"", result.Html);
			Assert.Equal(0, result.BlockedRemoteCount);
		}

		/// <summary>
		/// SVG is not allowed at all: <c>&lt;image href&gt;</c>, <c>&lt;use&gt;</c> and
		/// <c>&lt;feImage&gt;</c> fetch exactly like <c>&lt;img src&gt;</c>, in more spellings than a
		/// filter can be trusted to enumerate (§13), and mail has no need for inline SVG.
		/// </summary>
		[Theory]
		[InlineData("<svg><image href=\"https://evil.example/track.png\"/></svg>")]
		[InlineData("<svg><image xlink:href=\"https://evil.example/track.png\"/></svg>")]
		[InlineData("<svg><filter><feImage href=\"https://evil.example/track.png\"/></filter></svg>")]
		[InlineData("<svg><use href=\"https://evil.example/s.svg#a\"/></svg>")]
		public void Inline_svg_is_removed_with_every_reference_in_it(string html)
		{
			var result = Prepare(html, allowRemote: true).Html;

			Assert.DoesNotContain("evil.example", result);
			Assert.DoesNotContain("<svg", result, StringComparison.OrdinalIgnoreCase);
		}
	}

	public sealed class InlineParts
	{
		/// <summary>
		/// A cid: reference names a part of this message, which is already downloaded: it is replaced
		/// by the part itself, and is not remote. Stripping it left every inline image broken while
		/// every unit test passed.
		/// </summary>
		[Fact]
		public void A_cid_reference_becomes_the_part_it_names_and_is_not_counted_as_remote()
		{
			var result = Prepare("<img src=\"cid:logo@example.org\">", inline: id => id == "logo@example.org" ? "data:image/png;base64,AAAA" : null);

			Assert.Contains("src=\"data:image/png;base64,AAAA\"", result.Html);
			Assert.DoesNotContain("cid:", result.Html);
			Assert.Equal(0, result.BlockedRemoteCount);
		}

		[Fact]
		public void A_cid_reference_in_a_style_is_resolved_too()
		{
			var result = Prepare("<div style=\"background-image: url(cid:bg@example.org)\">x</div>", inline: _ => "data:image/png;base64,AAAA");

			Assert.Contains("data:image/png;base64,AAAA", result.Html);
			Assert.DoesNotContain("cid:", result.Html);
		}

		[Fact]
		public void A_cid_reference_with_no_such_part_is_removed_rather_than_left_to_dangle()
		{
			var result = Prepare("<img src=\"cid:missing@example.org\">", inline: _ => null);

			Assert.DoesNotContain("cid:", result.Html);
			Assert.DoesNotContain("missing", result.Html);
		}

		[Fact]
		public void A_quoted_message_keeps_its_cid_references_for_the_new_draft_to_bind()
		{
			var result = MessageHtmlPreparer.Prepare("<img src=\"cid:logo@example.org\"><a href=\"cid:logo@example.org\">x</a>", false, null, preserveInline: true);

			Assert.Contains("src=\"cid:logo@example.org\"", result.Html);
			Assert.DoesNotContain("href=\"cid:", result.Html);
		}

		[Fact]
		public void A_link_to_a_cid_part_is_removed()
		{
			var result = Prepare("<a href=\"cid:logo@example.org\">x</a>", inline: _ => "data:image/png;base64,AAAA");

			Assert.DoesNotContain("data:", result.Html);
			Assert.DoesNotContain("cid:", result.Html);
		}

		[Fact]
		public void An_embedded_raster_image_is_kept_and_not_counted()
		{
			var result = Prepare("<img src=\"data:image/png;base64,AAAA\">");

			Assert.Contains("data:image/png;base64,AAAA", result.Html);
			Assert.Equal(0, result.BlockedRemoteCount);
		}

		[Theory]
		[InlineData("<img src=\"data:image/svg+xml;base64,AAAA\">")]
		[InlineData("<img src=\"data:text/html;base64,AAAA\">")]
		[InlineData("<a href=\"data:text/html;base64,AAAA\">x</a>")]
		public void Any_other_data_url_is_removed(string html)
		{
			Assert.DoesNotContain("data:", Prepare(html).Html);
		}

		private static PreparedHtml Prepare(string html, Func<string, string?>? inline = null) =>
			MessageHtmlPreparerTests.Prepare(html, false, inline);
	}
}
