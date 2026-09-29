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

const ROOT = new URL(".", import.meta.url).pathname;

/** Cache headers by asset kind: the page itself must never go stale, its screenshots are immutable
 * enough to cache hard — they only change when a Render* test is re-harvested and redeployed. */
function cacheControl(pathname: string): string {
  if (pathname.startsWith("/shots/")) return "public, max-age=86400, stale-while-revalidate=604800";
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
      // The page is entirely self-contained — fonts and favicon are inline data URIs, and there is no
      // script at all — so it can afford a strict policy rather than the usual permissive default.
      response.headers.set(
        "content-security-policy",
        "default-src 'none'; img-src 'self' data:; style-src 'unsafe-inline'; font-src data:; base-uri 'none'; form-action 'none'; frame-ancestors 'none'",
      );
      response.headers.set("x-content-type-options", "nosniff");
      response.headers.set("referrer-policy", "strict-origin-when-cross-origin");
      response.headers.set("strict-transport-security", "max-age=31536000; includeSubDomains");
    }

    return response;
  },
} satisfies Deno.ServeDefaultExport;
