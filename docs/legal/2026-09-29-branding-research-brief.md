# Research brief: Bevel branding, trade dress and third-party marks

> Paste the section below the line into a deep-research tool. It is written to be self-contained —
> it does not assume the reader can see this repository.
>
> Filed against bead `bevel-legal-branding`. This is a brief for research, not advice, and not a
> substitute for counsel in PL/EU and the US.

---

You are researching trademark, trade dress and copyright exposure for a specific software product
before its public launch. I need a risk-ranked, source-backed analysis I can act on, not general
commentary. Where the answer differs by jurisdiction, say so explicitly rather than generalising.

## The product

**Bevel** is a desktop shell replacement — it provides a taskbar, a Start menu, a file manager and a
desktop surface, replacing or sitting alongside the host operating system's own shell. It runs on
macOS, Windows and Linux, written in C#/.NET with the Avalonia UI toolkit. It is **not** an emulator
and contains **no** Microsoft code, binaries, fonts or extracted resources.

Its design premise is to recreate the *visual language* of late-1990s/early-2000s Windows desktop UI
— the grey 3D bevelled chrome of Windows 2000, and the glossy blue "Luna" style of Windows XP — but
redrawn from scratch as resolution-independent vector geometry at modern fidelity. The project's own
internal north star is "the classic desktop as if it were designed today", explicitly **not** a
pixel-accurate clone: colours, proportions and behaviour are evocative, the rendering is modern
(antialiased, HiDPI, vector-only, no extracted bitmaps).

**Author and distribution.** A single individual developer resident in **Poland (EU)**. Distribution
is planned worldwide from a public website and a public source repository, as a macOS disk image
signed with an individual Apple Developer ID, a Windows build, and possibly a Homebrew cask. The
project intends to be open source; the licence has not yet been chosen.

## The specific artefacts at issue

Analyse each of these as a **separate** item. They differ in kind and I need them ranked, not merged.

**1. Reproduction of third-party logos inside the product.** The Start button renders an OS badge
chosen at runtime by host platform: on Windows, the **Microsoft Windows flag logo**, loaded from a
vector asset that reproduces the actual mark; on macOS, the **Apple logo**, reproduced as hand-drawn
vector geometry; on Linux, **Tux** the Linux mascot. These are deliberately brand-accurate — the
intent was that the button shows "the logo of the system you are on", mirroring how the historical
Start button worked. This is the item I expect to be most exposed, and I want to know precisely how
exposed, including whether Apple's and Microsoft's published trademark/logo guidelines permit *any*
in-product use of this shape, and whether Tux's licence (the original Larry Ewing terms) differs.

**2. Era and product names in marketing copy.** The product's *in-app* theme names are already
neutralised — the user sees "Bevel Classic", "Bevel Luna (XP)", "Bevel Flat (preview)". But the
public landing page currently uses, as descriptive copy: "Windows 2000" (5 occurrences), "Windows
XP", "Windows 9x", "Windows Vista", "Whistler", and "Luna". "Luna" and "Whistler" are Microsoft's
own names/codenames for the Windows XP visual style and for XP itself. The page also describes a
file manager as an "Explorer" and its parts as a "Start menu". Questions: which of these are
legitimate **referential/nominative** use (naming the thing one is inspired by, or describing
compatibility), which are impermissible **trademark use in the course of trade**, and does the
answer change between describing a *visual style* ("the Windows 2000 look") and naming a *product
feature* ("the Luna theme")?

**3. Trade dress of the interface itself.** The entire UI is deliberately evocative of Windows 2000
and XP chrome: button-face grey with raised/sunken 3D bevels, navy gradient title bars, a
bottom-anchored taskbar with a Start button at the left and a clock/tray at the right, a two-column
Start menu with a user header. It is redrawn, not copied, and differs in many details. Questions:
what is the actual legal test for UI trade dress and "look and feel" here, what does the case law
say about *functional* vs *ornamental* interface elements, and does the "redrawn at modern fidelity,
not pixel-copied" framing materially help or is it irrelevant? Please engage with the historical
authorities directly — including *Apple Computer v. Microsoft* (GUI look and feel), *Lotus v.
Borland* (menu command hierarchy), and how *Oracle v. Google* did or did not shift the analysis for
interface elements — and say which remain good law.

**4. Third-party application icons in marketing screenshots.** The landing page's hero screenshot
shows the shell running with a populated Start menu whose entries are real applications from the
developer's own Mac — Safari, Terminal, Preview, Calculator — displayed with their **real macOS
application icons**, extracted at runtime by the shell and committed into the repository as PNG
fixtures so the screenshot is reproducible. Questions: is displaying a competitor's/platform
vendor's icons in a screenshot of your own software covered by nominative fair use (US) or
referential use (EU), does *committing the icon artwork into a public repository* change the
analysis from *displaying it in a screenshot*, and is there an established industry norm here (e.g.
how do dock/launcher/theming products handle screenshots)?

**5. Third-party theme packages — platform liability.** Bevel supports `.beveltheme` packages: data
only (markup, images, fonts, sounds), no executable code, loaded at runtime. A third party could
plainly author and distribute a package that *is* a pixel-accurate reproduction of a Microsoft
visual style, including extracted Microsoft bitmaps and fonts. Bevel would be the runtime that loads
it. Questions: what is the platform's exposure for contributory/secondary infringement (US) and
under the EU DSA / e-Commerce hosting provisions, does it matter whether Bevel *hosts* a package
gallery versus merely *loads local files*, and what concrete design mitigations are known to work
(provenance metadata, a licence/attribution manifest field, a curated-versus-open gallery split,
takedown process)?

**6. Fonts.** The original visual styles depend on Microsoft-licensed typefaces (MS Sans Serif,
Tahoma). Bevel intends to ship only open-licensed fonts. Question: which open fonts are genuinely
metric-compatible substitutes for these, and is a *metric-compatible* clone itself a risk
(typeface designs versus font software, which differs sharply between the US and EU/Poland)?

**7. The product name.** "Bevel" for desktop shell software. Basic clearance signal: existing
registered marks in the relevant Nice classes (9, 42) in the EU, Poland, US and UK, and any obvious
conflicts in software.

**8. Disclaimers.** The page currently carries one line in the footer: *"an homage, reimagined — not
affiliated with Microsoft"*. Questions: what does a disclaimer actually achieve legally (as opposed
to reputationally), where must it appear to have any effect, and what wording is materially
stronger? Note the page does not currently disclaim Apple at all, despite showing Apple's logo and
icons.

## What I want back

1. **A risk table**, one row per numbered item above, with: the specific right at issue (trademark /
   trade dress / copyright / design right), a likelihood-and-severity rating, the jurisdiction(s)
   where it is worst, and a plain go / change / stop recommendation.
2. **The single highest-risk item** identified unambiguously, with reasoning.
3. **Concrete safer alternatives** for anything you rate change-or-stop — specific wording, specific
   design changes — not "consult a lawyer" as the whole answer.
4. **Real-world precedent**: how comparable shipping projects handle exactly these questions. Look at
   retro-shell and theming software specifically (for example Open-Shell / Classic Shell, RetroBar,
   Stardock WindowBlinds and Curtains, Chicago95, various Linux desktop themes that imitate Windows
   or macOS), and note which of them reproduce vendor logos, which use vendor names in marketing, and
   whether any have faced public enforcement.
5. **Published vendor policy**, quoted and linked: Microsoft's Trademark and Brand Guidelines and any
   position on Windows UI imitation; Apple's Trademark and Copyright guidelines and App Store/logo
   rules; the licence terms attached to Tux.
6. **Where the honest answer is "this needs a lawyer"**, say so for that specific item and say what
   question to put to them — but do not use it as the answer for every item.

Cite primary sources — statutes, decided cases, and the vendors' own published policies — with links.
Distinguish clearly between **US** law, **EU** law (including the EU Trade Mark Regulation's
referential-use provision and the Design Directive), and anything **Poland**-specific, since that is
where the author is established. Flag explicitly where US and EU outcomes diverge, because the
product ships in both.
