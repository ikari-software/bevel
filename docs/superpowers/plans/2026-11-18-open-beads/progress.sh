#!/bin/bash
# Prints progress per wave and the next card for an agent. Status and park state come from bd, not from this folder.
# Park a card:  bd label add <id> plan-parked          Wait on another bead:  bd label add <id> plan-waits:<other-id>
set -uo pipefail
dir="$(cd "$(dirname "$0")" && pwd)"
tmp="$(mktemp)"; trap 'rm -f "$tmp" "$tmp.st"' EXIT
if ! bd export -o "$tmp" >/dev/null 2>&1 || [ ! -s "$tmp" ]; then
  echo "progress.sh: 'bd export' failed. Run this from inside the repo that holds the .beads workspace." >&2
  exit 1
fi
jq -r '[.id, .status, ((.labels // []) | join(","))] | @tsv' "$tmp" > "$tmp.st"
awk -F'|' -v st="$tmp.st" '
BEGIN { while ((getline l < st) > 0) { split(l, a, "\t"); s[a[1]] = a[2]; lab[a[1]] = a[3] } }
function parked(id,   n, i, parts, dep) {
  n = split(lab[id], parts, ",")
  for (i = 1; i <= n; i++) {
    if (parts[i] == "plan-parked") return 1
    if (index(parts[i], "plan-waits:") == 1) { dep = substr(parts[i], 12); if (s[dep] != "closed") return 1 }
  }
  return 0
}
{
  w = $1; if (!(w in tot)) order[++n] = w
  tot[w]++; all++; listed[$2] = 1
  status = (($2 in s) ? s[$2] : "missing")
  if (status == "closed") { done[w]++; alldone++ }
  else if ($3 == "HUMAN" || w == "H") owner++
  else if (parked($2)) park++
  else if (next_id == "") { next_id = $2; next_line = $2 "  [" w ", size " $4 ", " status "]  " $6 "\n      card: " ($5 == "-" ? "none; brief is `bd show " $2 "`" : $5) }
}
END {
  for (i = 1; i <= n; i++) printf "%-3s %3d / %-3d closed\n", order[i], done[order[i]] + 0, tot[order[i]]
  printf "ALL %3d / %-3d closed   (%d owner-only, %d parked or waiting)\n", alldone + 0, all, owner + 0, park + 0
  for (id in s) if (!(id in listed) && s[id] != "closed" && s[id] != "deferred") unl = unl " " id
  if (unl != "") print "Open beads not in waves.txt (add a row):" unl
  print "Next: " (next_id == "" ? "none" : next_line)
}' "$dir/waves.txt"
