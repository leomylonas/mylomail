using MyloMail.Api.Domain;

namespace MyloMail.Api.Contracts;

public record RemoteContentRuleDto(
	Guid Id,
	RemoteContentRuleScope Scope,
	RemoteContentRuleDecision Decision,
	string Value
);

public record PutRemoteContentRuleRequest(
	RemoteContentRuleScope Scope,
	RemoteContentRuleDecision Decision,
	string Value
);

/// <summary>What the reading pane needs to know about a prepared message (§13). The body itself is served as a document.</summary>
/// <param name="BlockedRemoteCount">How many remote references were withheld; the pane offers to load them.</param>
/// <param name="Decision">What the persisted sender/domain rules say about this sender.</param>
/// <param name="RemoteAllowed">Whether remote content is loaded in this view: a rule decides, otherwise the reader's request for this view.</param>
public record MessageHtmlInfoDto(int BlockedRemoteCount, RemoteContentDecision Decision, bool RemoteAllowed);

/// <summary>A message body prepared for quoting in a reply or forward: remote content withheld, inline images still named by <c>cid:</c>.</summary>
public record QuotedHtmlDto(string Html);

/// <summary>
/// A message body prepared for the print layout, which lives in the app's own page. That page's policy
/// refuses <c>style</c> elements and attributes in markup, so they are taken out here and applied by the
/// renderer through the CSS object model.
/// </summary>
/// <param name="Html">The sanitised markup: no style elements, no style attributes; each element that had one carries <c>data-inline-style</c>.</param>
/// <param name="Sheets">The text of each style element, in document order.</param>
/// <param name="Inline">The declarations of each styled element, keyed by its <c>data-inline-style</c> marker.</param>
public record PrintHtmlDto(string Html, IReadOnlyList<string> Sheets, IReadOnlyDictionary<string, string> Inline);
