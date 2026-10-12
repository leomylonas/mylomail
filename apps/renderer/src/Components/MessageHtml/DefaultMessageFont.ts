import regular from "../../../node_modules/@ibm/plex/IBM-Plex-Sans/fonts/complete/woff2/IBMPlexSans-Regular.woff2?inline";
import italic from "../../../node_modules/@ibm/plex/IBM-Plex-Sans/fonts/complete/woff2/IBMPlexSans-Italic.woff2?inline";
import bold from "../../../node_modules/@ibm/plex/IBM-Plex-Sans/fonts/complete/woff2/IBMPlexSans-Bold.woff2?inline";

/**
 * What a message with no font of its own is shown in: the app's own typeface, so it reads as part
 * of the interface rather than the browser's default serif.
 *
 * Embedded as data URLs because the frame's policy lets fonts load from `data:` only, which keeps
 * a message from reaching for any other font source. Written with no specificity, so anything the
 * message declares — in a `<style>` block or inline — overrides it; only text with no font of its
 * own inherits this.
 */
export const defaultMessageFontCss = `
@font-face { font-family: "IBM Plex Sans"; font-style: normal; font-weight: 400; src: url("${regular}") format("woff2"); }
@font-face { font-family: "IBM Plex Sans"; font-style: italic; font-weight: 400; src: url("${italic}") format("woff2"); }
@font-face { font-family: "IBM Plex Sans"; font-style: normal; font-weight: 700; src: url("${bold}") format("woff2"); }
/* :where() has no specificity, so any rule the message writes for body wins whatever its order. */
:where(body) { font-family: "IBM Plex Sans", system-ui, "Segoe UI", Roboto, "Helvetica Neue", Arial, sans-serif; }
`;
