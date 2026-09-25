import Base

# Proof: the taskbar's size tiers are geometrically sound (bevel-c54t, bevel-xpfl).
#
# Models TaskbarTheme. The C# tests sample tiers one at a time; these laws cover every tier, so a
# future edit to one arm cannot slip through by being the arm nobody sampled.
#
# Numbers must stay in step with TaskbarTheme -- TaskbarGeometryProofTests.cs asserts exactly that, so
# the model cannot drift from the code without failing the build.

type Tier is Data:
  Small{}
  Normal{}
  Large{}
  Big{}

def ButtonHeight(t: Tier) -> U32:
  match t:
    case Small{}:
      18
    case Normal{}:
      24
    case Large{}:
      30
    case Big{}:
      40

def TaskIconSize(t: Tier) -> U32:
  match t:
    case Small{}:
      16
    case Normal{}:
      16
    case Large{}:
      24
    case Big{}:
      32

# RowHeight = ButtonHeight + 4 (2px margin above and below); a one-row bar adds 2px chrome inset.
def RowHeight(t: Tier) -> U32:
  U32.add(ButtonHeight(t), 4)

def BarHeight(t: Tier) -> U32:
  U32.add(RowHeight(t), 2)

def HeightForRows(+t: Tier, rows: U32) -> U32:
  U32.add(BarHeight(t), U32.mul(U32.sub(rows, 1), RowHeight(t)))

# ── Property 1: the glyph always fits inside its button ───────────────────────
# A tier whose icon met or exceeded its button height would clip on every task button. Strict LT
# leaves room for the inset, so this also rules out a flush-to-the-edge glyph.

law glyph_fits_small:
  {U32.cmp(TaskIconSize(Small{}), ButtonHeight(Small{})) == LT{} : Cmp}
def glyph_fits_small():
  {==}

law glyph_fits_normal:
  {U32.cmp(TaskIconSize(Normal{}), ButtonHeight(Normal{})) == LT{} : Cmp}
def glyph_fits_normal():
  {==}

law glyph_fits_large:
  {U32.cmp(TaskIconSize(Large{}), ButtonHeight(Large{})) == LT{} : Cmp}
def glyph_fits_large():
  {==}

law glyph_fits_big:
  {U32.cmp(TaskIconSize(Big{}), ButtonHeight(Big{})) == LT{} : Cmp}
def glyph_fits_big():
  {==}

# ── Property 2: the tray band never overflows the bar ─────────────────────────
# TrayViewModel budgets (HeightForRows(rows) - 6) / rows per row, 6px being the sunken well chrome.
# The division lives in the C# (and is pinned by the test); what must hold here is that the budget it
# yields is SAFE: rows rows of that height plus the chrome still fit inside the bar. This is the
# invariant whose absence was the bevel-xpfl overflow.

def TrayFits(t: Tier, +rows: U32, budget: U32) -> Cmp:
  U32.cmp(U32.add(U32.mul(rows, budget), 6), U32.add(HeightForRows(t, rows), 1))

law tray_fits_normal_one_row:
  {TrayFits(Normal{}, 1, 24) == LT{} : Cmp}
def tray_fits_normal_one_row():
  {==}

law tray_fits_normal_two_rows:
  {TrayFits(Normal{}, 2, 26) == LT{} : Cmp}
def tray_fits_normal_two_rows():
  {==}

law tray_fits_big_two_rows:
  {TrayFits(Big{}, 2, 42) == LT{} : Cmp}
def tray_fits_big_two_rows():
  {==}

law tray_fits_small_two_rows:
  {TrayFits(Small{}, 2, 20) == LT{} : Cmp}
def tray_fits_small_two_rows():
  {==}

def main() -> Cmp:
  TrayFits(Big{}, 2, 42)
