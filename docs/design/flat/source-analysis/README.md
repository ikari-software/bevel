# Flat / Whistler Watercolor Source Analysis

This directory is the source-analysis phase for the Flat theme. The supplied
reference screenshots and Classic Shell skin are evidence, not runtime assets.
Bevel's implementation must remain vector and HiDPI-safe.

## Evidence

- `reference-whistler.jpg`: supplied Whistler reference, 500x554.
- `reference-classic-shell.jpg`: supplied Classic Shell Watercolor reference, 900x600.
- `original-start-button.png`: supplied source Start button, 58x66, retained only for visual study.
- `Watercolor.skin`: supplied Classic Shell skin configuration and source variation metadata.

## Source Facts

- The source family is a compact Classic Shell / early-XP composition, not a modern airy dashboard.
- Main menu content is solid white: `Main_background=#FFFFFF`.
- Primary selection is saturated blue: `#316AC5`; secondary selection is `#5096F8`.
- New-item selection is pale warm yellow: `#FEEAB6`.
- Main and submenu text are dark/white by state; disabled text is gray.
- The source uses thin bitmap-slice borders and compact Tahoma metrics. The vector port must preserve the edge transitions as geometry, not paint a soft card.
- Main slices are `X=10,144,2,2,168,10`, `Y=15,1,48`; submenu slices are `X=2,174,2`, `Y=2,18,2`.
- Main padding is compact (`10,16,5,45` / `13,12,7,46` for the primary two-column states); submenu padding is `2,2,2,2`.
- Variations in the supplied skin are Blue, Ergonomic, Olive, Silver, Violet. Bevel's requested product set is Blue, Ergonomic, Silver, Amber: Olive is removed and Violet is replaced by Amber.
- The original skin configuration describes the Start/menu surface. It is not evidence for Bevel's internal window frame. Flat's Bevel window frame must be designed separately and labeled as such in the target.

## Material Separation

Flat's source-led material system has four layers:

1. Warm neutral shell faces: sampled warm gray/gray-green surfaces around `#F1F1EF`, `#EDEDEA`, `#C2C5BC`, `#A1A3A2`.
2. Watercolor chrome bands: blue-violet/lilac transitions around `#BBC8F0`, `#A2A8E1`, `#688ADE`, with variation-specific transforms.
3. Source semantic accents: `#316AC5`, `#5096F8`, `#FEEAB6`, plus the variation accents.
4. Content surfaces: solid white main menu/list areas and pale blue-lilac secondary columns around `#D9E3F3` / `#B9CCEF`.

The current Flat runtime is not accepted merely because it contains these hexes. Geometry, proportions, state mapping, and region ownership must also agree with the source-led target.
