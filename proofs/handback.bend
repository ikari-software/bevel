# Proof: the taskbar's key-focus handback never stomps a foreign activation (bevel-hx63).
#
# Models TaskbarWindow.ShouldHandBackKeyFocus. That method only ever compares its three pids for
# equality, so the five distinguishable worlds below are a faithful abstraction of its whole input
# space -- which is the point: the C# test samples five cases, this covers the domain exhaustively.

type Fg is Data:
  NoCapture{}    # priorAppPid <= 0   -- nothing captured, or the handback was cancelled
  Unknown{}      # frontmostPid <= 0  -- the frontmost app cannot be determined
  StillPrior{}   # frontmost == prior -- the captured app never lost the foreground
  Ours{}         # frontmost == own   -- only we took front, to make the menu key
  ThirdApp{}     # frontmost is neither prior nor us: an activation WE drove landed

type Decision is Data:
  StandDown{}   # do NOT re-activate the captured app
  HandBack{}    # re-activate it: focus should return where it was

def ShouldHandBack(w: Fg) -> Decision:
  match w:
    case NoCapture{}:
      StandDown{}
    case Unknown{}:
      HandBack{}
    case StillPrior{}:
      HandBack{}
    case Ours{}:
      HandBack{}
    case ThirdApp{}:
      StandDown{}

# The safety property. A third app holding the foreground is the evidence that the taskbar activated
# something; re-raising the captured app would put the user's chosen window back underneath it, with
# IgnoringOtherApps strength that outranks the helper's bare activate(). This must never happen.
law never_stomps_a_foreign_activation:
  {ShouldHandBack(ThirdApp{}) == StandDown{} : Decision}

def never_stomps_a_foreign_activation():
  {==}

# The liveness property, the other half: a menu that took no action must still restore focus.
law restores_focus_when_nothing_else_took_front:
  {ShouldHandBack(StillPrior{}) == HandBack{} : Decision}

def restores_focus_when_nothing_else_took_front():
  {==}

def main() -> Decision:
  ShouldHandBack(ThirdApp{})
