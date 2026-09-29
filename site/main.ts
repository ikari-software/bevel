/**
 * Static file server for the Bevel landing page (bevel-rkxb / site hosting).
 *
 * The page is a single self-contained HTML file plus its screenshots — no build step, no framework.
 * ikari.software next door is a Fresh app; this deliberately is not, because there is nothing here that
 * needs one, and a build step would be one more thing between a harvested screenshot and the live page.
 *
 * Serves from this directory: `/` and `/index.html` both return the page, `/shots/*` the real renders.
 */
import { serveDir } from "jsr:@std/http@1/file-server";
import { encodeBase64 } from "jsr:@std/encoding@1/base64";

const ROOT = new URL(".", import.meta.url).pathname;

/**
 * CSP script hashes, derived at boot from the page we actually serve.
 *
 * index.html carries one small inline script (the theme toggle). A hand-written hash would rot the
 * moment that script changed, and the failure is silent -- the toggle simply stops working. Hashing the
 * served file instead means the policy can never disagree with the page. Throws rather than degrading:
 * a page whose script is blocked should fail loudly at boot, not quietly in the browser.
 */
async function inlineScriptHashes(): Promise<string> {
  const html = await Deno.readTextFile(`${ROOT}index.html`);
  const bodies = [...html.matchAll(/<script(?![^>]*\bsrc=)[^>]*>([\s\S]*?)<\/script>/g)].map((m) => m[1]);
  if (bodies.length === 0) return "'none'";

  const hashes: string[] = [];
  for (const body of bodies) {
    const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(body));
    hashes.push(`'sha256-${encodeBase64(digest)}'`);
  }
  return hashes.join(" ");
}

const SCRIPT_SRC = await inlineScriptHashes();

/** Cache headers by asset kind.
 *
 * The screenshots were previously treated as immutable (a day of max-age plus a week of
 * stale-while-revalidate) on the theory that they only change when a Render* test is re-harvested.
 * But a re-harvest REWRITES THEM AT THE SAME URL, so that policy meant a corrected screenshot could
 * not reach anyone who had already loaded the page for a day, and would keep being served stale for
 * a week after. Exactly that happened: the fixed win9x info pane stayed invisible behind the cache.
 *
 * Immutable caching needs immutable URLs, and these URLs are deliberately stable (the page is
 * buildless — nothing rewrites its references to add a content hash). So the shots revalidate
 * instead. serveDir already emits an ETag, so an unchanged shot costs a 304, not a re-download. */
function cacheControl(pathname: string): string {
  if (pathname.startsWith("/shots/")) return "public, no-cache";
  return "public, max-age=0, must-revalidate";
}

export default {
  async fetch(request: Request): Promise<Response> {
    const { pathname } = new URL(request.url);

    // One canonical URL for the page. Without this, "/index.html" and "/" are two addresses for one
    // document, which splits analytics and is the kind of thing that quietly hurts search.
    if (pathname === "/index.html") {
      return Response.redirect(new URL("/", request.url), 308);
    }

    const response = await serveDir(request, {
      fsRoot: ROOT,
      quiet: true,
      showDirListing: false,
      showDotfiles: false,
    });

    if (response.ok || response.status === 304) {
      response.headers.set("cache-control", cacheControl(pathname));
      // The page is self-contained — fonts and favicon are inline data URIs, and its only script is the
      // theme toggle — so it can afford a strict policy rather than the usual permissive default. The
      // script is allowed by HASH, not by 'unsafe-inline', so an injected script still cannot run.
      response.headers.set(
        "content-security-policy",
        `default-src 'none'; img-src 'self' data:; style-src 'unsafe-inline'; font-src data:; ` +
          `script-src ${SCRIPT_SRC}; base-uri 'none'; form-action 'none'; frame-ancestors 'none'`,
      );
      response.headers.set("x-content-type-options", "nosniff");
      response.headers.set("referrer-policy", "strict-origin-when-cross-origin");
      response.headers.set("strict-transport-security", "max-age=31536000; includeSubDomains");
    }

    return response;
  },
} satisfies Deno.ServeDefaultExport;
