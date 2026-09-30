# Parked legal / licensing questions (2026-09-30)

> Not blocking engineering. Owner decisions below; open asks kept for a later counsel pass.
> Source context: `docs/legal/2026-09-29-branding-research-brief.md`, IP risk report,
> bead `bevel-legal-branding`.

## Decisions (owner, 2026-09-30)

| Topic | Decision |
|---|---|
| Product name | **Bevel Desktop** (compound branding). Ids stay `pl.ikari.bevel` / `bevel://`. |
| Trade dress / slavish imitation | **Accepted product posture:** Bevel Desktop is a clean redesign and will become highly customisable (cross-era / cross-OS mix), not a slavish imitation. Not waiting on counsel to ship on that basis. Mitigations already shipped (own mark, theme names, disclaimers, no vendor logos) remain. |
| Theme distribution | **No hosted theme gallery.** Third-party themes may be accepted as **git repositories** (clone/install from a repo URL). No Bevel-operated package host. |

## Parked questions — trade dress (item 2)

Keep for a future counsel packet if we revisit risk appetite or enter a jurisdiction that forces it:

1. Under Polish UZNK arts. 10/13 (slavish imitation), does a desktop shell that recreates historical Win2000/XP *layout language* with original assets, own branding, and deep user customisation still present material civil exposure?
2. Under US Lanham Act § 43(a) trade dress, with the mitigations already shipped, is the *totality* of Bevel Desktop’s default chrome likely to support a confusion claim by Microsoft (or others)?
3. Does the public landing page / screenshots need further distance (copy or visuals) beyond the current multi-vendor notice and Bevel theme names?
4. Any clearance concern for the spectrum-edged glass cube mark vs existing cube / rainbow-outline marks (incl. legacy Apple rainbow palette analogies)?

## Parked questions — `.beveltheme` trust (item 3)

Distribution policy is set (git repos only; no gallery). Licensing / liability detail stays TBD:

1. For **git-sourced** themes (user pastes a repo URL; Bevel clones locally), what warranties/disclaimers should the install UI carry so Bevel is not treated as publisher of third-party assets?
2. Is a mandatory `theme.json` provenance block (`author`, `license`, `attribution_notice`, optional asset hashes) enough, or must install fail closed without it?
3. Declarative-only packages (no assemblies / no `x:Class`) — confirm this is the right hard line; any exception for sandboxed script hooks?
4. If a git theme bundles infringing fonts/icons/sounds, where does liability sit (author vs user vs Bevel as the tool that fetched the repo)?
5. DSA / hosting: confirm that **not** operating a gallery avoids hosting-provider duties; any residual risk if we later index public git URLs on the marketing site without hosting blobs?

## TBD — blurry on purpose

Until those asks are answered, docs that touch theme licensing should say **TBD** rather than inventing a trust framework. Engineering may still build: declarative loader, git-clone install path, provenance fields in the manifest schema — without claiming counsel sign-off.
