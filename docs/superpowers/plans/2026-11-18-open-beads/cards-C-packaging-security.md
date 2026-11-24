> Part of the [open-beads master plan](README.md). Read README.md first: it sets the rules, the order, and the rulings that override individual cards.
> Written 2026-11-18 from reading the tree at `51582c0`. Nothing here was built or run. `UNVERIFIED:` marks a claim the researcher could not confirm — check it before relying on it.

## Cluster C: Packaging, release, updates, security hardening, docs

Release lane = four bash stages in `packaging/macos/` (`build-app.sh` → `sign-app.sh` → `notarize-app.sh` → `make-dmg.sh`),
automated by `.github/workflows/release-macos.yml` (tag `v*` or manual dispatch). It has NEVER passed: both v0.1.0 tag runs
died at the cert-import step because 5 of 6 secrets are missing; the v0.1.0 assets on GitHub were uploaded by hand.
Updates = `IUpdateSource` seam (`src/Bevel.Core/Updates/UpdateCheck.cs`) + `UpdateCheckService` (core role only, cadence
shipped, bound to `NoUpdateSource`); target is Velopack reading GitHub Release assets, applied only on a user-chosen restart.
Three local sockets: helper gRPC (`AuthInterceptor.authenticate`, capability-scoped HMAC, already shipped), shell-core /
launcher / filer UDS (`Bevel.ShellCore.Ipc.Handshake`, HMAC nonce, 0700 dir), and the automation socket
(`AutomationSocketServer`, `~/.config/bevel/bevelctl.sock`, file perms ONLY — the one gap). `bevel://` is treated as a
hostile, web-reachable surface (`AutomationCommandRouter.LaunchAsync` note); `bevelctl` is the trusted one.
Target: one release lane (delete the dead `sign` job in `ci.yml`), every socket handshake-authenticated, updates and
uninstall that keep the helper's TCC identity (`pl.ikari.bevel.helper`, pinned in `sign-app.sh:36`) stable.

## Order

1. bevel-9po — the six secrets; nothing can be released by CI until a human does this (plus two agent-side lane fixes found here).
2. bevel-m5 — epic; drop the false m4/m3 block so packaging work shows as ready.
3. bevel-nsk — DUPLICATE of 9po; closing it also deletes a second, wrong signing lane in `ci.yml`.
4. bevel-ka6c — shipped/installed binaries link `/opt/homebrew` brotli; a cheap gate stops it recurring.
5. bevel-ayf — LIKELY DONE (v0.1.0 release exists); close.
6. bevel-wcqr — the only unauthenticated socket, and it accepts delete/move.
7. bevel-ym0 — Velopack: the real remaining update work; depends on 9po + ka6c.
8. bevel-rkxb — superseded by ym0 + one new UI bead; close after filing it.
9. bevel-q3l — uninstaller; required before a cask zap stanza and the user guide can be honest.
10. bevel-tak — durable crash records + export (today crash evidence lives in `$TMPDIR` and dies on reboot).
11. bevel-ct7 — settings surface; mostly done, remainder is small and depends on tak.
12. bevel-gsz — Homebrew cask; needs a human-created tap repo and a clean (ka6c) DMG.
13. bevel-cp2 — user guide / known issues / uninstall docs; write after q3l + tak land.
14. bevel-l3o — PARTLY DONE (capability HMAC shipped); small remainder: stop handing out the raw nonce.
15. bevel-4x0 — URL/CLI verb parity; stale count, needs a scope decision (made here).
16. bevel-osp — helper signature check before launch; defense in depth.
17. bevel-kyf — helper peer-UID check; the "transport can't do it" premise is now stale.
18. bevel-9ucj — WindowRef bit budget / pid reuse; improbable edges.
19. bevel-7c6m — standing constraint note (no MAS); record and close.
20. bevel-legal-branding — parked counsel packet; HUMAN only.

## Cards

### bevel-9po — M5: activate release-macos.yml — configure the 6 GitHub 'release' environment secrets
- **Meta:** P2 · task · size S · HUMAN (agent pre/post work listed)
- **State today:** `gh secret list` → only `DENO_DEPLOY_TOKEN`, `KEYCHAIN_PASSWORD` (2026-10-12), `RELEASE_PLEASE_TOKEN`. Environment `release` exists, 0 secrets, no protection rules. Runs 36715319484 and 36768138840 (tag v0.1.0) both failed at step "Import Developer ID cert into an ephemeral keychain" (empty `SIGNING_CERT_P12`). `packaging/macos/setup-release-secrets.sh` (commit 6c2f71d) automates everything after the two manual exports; it sets REPO-level secrets, which the `environment: release` job can read. Latent lane bugs found: (a) `release-macos.yml:27` `runs-on: macos-14` but `native/helper-macos/Package.swift` is `swift-tools-version: 6.0` / `.macOS(.v15)` — UNVERIFIED: `swift build` will fail on that runner (CI's `swift` job uses `macos-latest`); (b) release title is `"Bevel $VERSION"`, the existing release is "Bevel Desktop 0.1.0"; (c) `packaging/macos/Info.plist:27` says `LSMinimumSystemVersion 14.0` while the helper requires macOS 15.
- **Depends on:** none
- **Files:** `.github/workflows/release-macos.yml`, `packaging/macos/Info.plist`, `packaging/macos/setup-release-secrets.sh` (read only)
- **Decision:** Keep repo-level secrets exactly as the script sets them (no `--env`); the human may add a required reviewer to environment `release` later. Runner → `macos-15`. `LSMinimumSystemVersion` → `15.0` (the helper cannot load on 14, so 14.0 is a false promise). Release title → `"Bevel Desktop $VERSION"`.
- **Test first:** `./packaging/macos/setup-release-secrets.sh --check` prints six lines ending `set` and no `MISSING`.
- **Steps:**
  - [ ] AGENT (before): edit the three items in Decision; commit `fix(release): macos-15 runner, min OS 15, release title`.
  - [ ] HUMAN: Keychain Access ▸ login ▸ My Certificates ▸ "Developer ID Application: Cezar Pokorski (4TP7TPH2K6)" ▸ Export… ▸ .p12 with a throwaway password.
  - [ ] HUMAN: appstoreconnect.apple.com ▸ Users and Access ▸ Integrations ▸ Keys ▸ "+" (role Developer); download `AuthKey_<KEYID>.p8` (once only); copy the Issuer ID.
  - [ ] HUMAN: `./packaging/macos/setup-release-secrets.sh --p12 <file.p12> --p8 <AuthKey_….p8> --issuer <uuid>` then `--check`.
  - [ ] HUMAN: `gh workflow run release-macos.yml -f version=0.1.1` (manual dispatch: builds, signs, notarizes, uploads a 7-day artifact, creates NO release).
  - [ ] AGENT (after): `gh run watch`; if a step fails, read `gh run view <id> --log-failed`, fix the script/workflow, re-dispatch. Do not tag until a dispatch run is green.
  - [ ] HUMAN: delete the .p12 from disk; keep the .p8 in the password manager.
- **Verify:** `gh run list --workflow release-macos.yml -L1` → `completed success`
- **Done when:** a dispatch run is green and its DMG artifact passes `spctl --assess --type open --context context:primary-signature -vv`; then `bd close bevel-9po --reason "6 secrets set via setup-release-secrets.sh; dispatch run <id> green"`

### bevel-m5 — M5 — Polish + packaging = mac v1
- **Meta:** P2 · epic · size L · AGENT+HUMAN-VERIFY
- **State today:** No description, no formal children (children only say "Parent: bevel-m5" in text). Blocked by `bevel-m4` (open: 1 child left, `bevel-4qr` AppleScript corpus) which is blocked by `bevel-m3` (open: 5 tray beads). That chain is a milestone-ordering convention, not a technical dependency: no M5 child touches tray or Apple Event code; packaging already shipped a signed v0.1.0 by hand. `bevel-ytn` (M5-A bundle) is closed.
- **Depends on:** none real. Recommended edits (human runs): `bd dep remove bevel-m5 bevel-m4`. Keep `bevel-m4 → bevel-m3` as is. Add real edges: `bd dep add bevel-ym0 bevel-9po`; `bd dep add bevel-ym0 bevel-ka6c`; `bd dep add bevel-gsz bevel-ka6c`; `bd dep add bevel-gsz bevel-q3l`; `bd dep add bevel-cp2 bevel-q3l`; `bd dep add bevel-cp2 bevel-tak`; `bd dep add bevel-ct7 bevel-tak`.
- **Children, in order:** bevel-9po → bevel-nsk (close) → bevel-ka6c → bevel-ayf (close) → bevel-ym0 → bevel-rkxb (close) → bevel-q3l → bevel-tak → bevel-ct7 → bevel-gsz → bevel-cp2. Outside this cluster, also M5 by text: `bevel-ti3` (Win2000 theme completion), `bevel-b8t` (100k-entry perf validation).
- **New beads to create:**
  - "Surface update availability: Start-menu badge + 'Restart to update'" — reads the core's `UpdateCheckService.Latest` over shell-core; nothing reads it today and no "Restart to upgrade" label exists in `src/`.
  - "Release lane: build + attach Windows/Linux zips" — v0.1.0's `Bevel-0.1.0-win-x64.zip` / `-linux-x64.zip` were hand-uploaded; `release-macos.yml` publishes only the DMG.
  - "HUMAN: update-from-(n-1) acceptance on a real Mac" — 09-engineering-plan M5 acceptance 3 (helper relaunch + TCC grants survive); needs real TCC grants.
  - "Site: /downloads/ 404s" — `https://bevel.run/downloads/Bevel-0.1.0.dmg` returns 404 (binaries are gitignored, CI deploy has none); the page links GitHub assets, so either delete the `/downloads/` route + `site/downloads/` or stop advertising it in `site/main.ts`.
- **Done when:** every child above is closed and a tagged release produced entirely by `release-macos.yml` installs, updates from the previous version, and uninstalls cleanly on a Mac that has no Homebrew; then `bd close bevel-m5 --reason "mac v1 shipped via CI lane"`

### bevel-nsk — Configure CI secrets for signing + notarization
- **Meta:** P2 · task · size S · AGENT
- **State today:** DUPLICATE OF bevel-9po, and stale: it says "there is no git remote yet" (the repo is public at `ikari-software/bevel`) and names four secrets (`APPLE_DEVELOPER_ID`, `APPLE_DEVELOPER_ID_PASSWORD`, `APPLE_TEAM_ID`, `APPLE_NOTARIZE_KC_PROFILE`) that only the dead `sign` job in `.github/workflows/ci.yml:258-330` reads. That job signs with `codesign --deep` (forbidden by ENG-060 — it would discard the helper's pinned identifier), needs a keychain profile that cannot exist on a runner, and its own comment says `release-macos.yml` owns releases.
- **Depends on:** none
- **Files:** `.github/workflows/ci.yml`
- **Decision:** bevel-9po survives. Delete the whole `sign` job and its comment block (`ci.yml` from the `# ── macOS signing + notarization lane` banner to the end of the job). One signing lane only.
- **Test first:** `grep -c 'APPLE_DEVELOPER_ID' .github/workflows/ci.yml` → `0` after the edit.
- **Steps:**
  - [ ] Delete the `sign` job; confirm no other job has `needs: sign` (`grep -n 'needs:' .github/workflows/ci.yml`).
  - [ ] In the `publish` and `swift` jobs, the tag-only `upload-artifact` steps existed only to feed `sign` — delete those two steps as well.
  - [ ] Push on a branch; CI must stay green.
- **Verify:** `gh run list --workflow ci.yml -L1` → `completed success`
- **Done when:** `ci.yml` has no signing job; then `bd close bevel-nsk --reason "Duplicate of bevel-9po; dead --deep sign lane removed from ci.yml"`

### bevel-ka6c — Apphost links /opt/homebrew brotli — breaks portability of signed builds
- **Meta:** P2 · task · size S · AGENT+HUMAN-VERIFY
- **State today:** Confirmed on the installed bundle: `otool -L /Applications/Bevel.app/Contents/MacOS/Bevel` (built 2026-08-20) lists `/opt/homebrew/opt/brotli/lib/libbrotli{dec,enc}.1.dylib`, same for `bevelctl`. NOT present in today's `dist/Bevel.app` (built 2026-11-18 with the Microsoft SDK at `/usr/local/share/dotnet`). So the root fix (official SDK) is available on this machine, but nothing enforces it: `which -a dotnet` still finds `/opt/homebrew/bin/dotnet` first. `sign-app.sh:37-41` still carries the workaround (app entitlements on every secondary executable). UNVERIFIED: whether the published `Bevel-0.1.0.dmg` is affected.
- **Depends on:** none
- **Files:** `packaging/macos/build-app.sh`, `packaging/macos/sign-app.sh`
- **Decision:** Gate, do not bundle brotli. Add to `build-app.sh`, right after the existing "Validating bundle" block: fail if any Mach-O in `Contents/MacOS` links outside `/usr/lib` or `/System`. Then drop the `--entitlements "$ENT"` branch for secondary executables in `sign-app.sh` (lines 37-41) so `bevelctl` is signed like any other nested Mach-O.
- **Test first:** the gate itself: run it against `/Applications/Bevel.app` (old build) → must fail naming brotli; against `dist/Bevel.app` → must pass.
- **Steps:**
  - [ ] Add the gate: `for f in "$APP"/Contents/MacOS/*; do file -b "$f" | grep -q Mach-O || continue; if otool -L "$f" | tail -n +2 | awk '{print $1}' | grep -Ev '^(/usr/lib/|/System/|@rpath/|@executable_path/)' ; then echo "non-system dylib linked by $f" >&2; exit 1; fi; done`
  - [ ] Remove the `elif file -b "$f" | grep -q "executable"` branch in `sign-app.sh` and its comment.
  - [ ] HUMAN-VERIFY: download the release DMG and run `otool -L` on `Bevel.app/Contents/MacOS/Bevel`; if brotli is listed, v0.1.0 does not run on a Mac without Homebrew — ship 0.1.1 from the CI lane (bevel-9po).
- **Verify:** `PATH=/usr/local/share/dotnet:$PATH DOTNET_ROOT=/usr/local/share/dotnet ./packaging/macos/build-app.sh` → ends `Built …/dist/Bevel.app (unsigned)` with no "non-system dylib" line (run only when no other agent is building).
- **Done when:** gate is in `build-app.sh`, workaround gone from `sign-app.sh`, and a signed build still passes `codesign --verify --deep --strict`; then `bd close bevel-ka6c --reason "otool gate added; bevelctl entitlement workaround removed"`

### bevel-ayf — M5: publish DMG hosting + first GitHub release (v0.1.0 tech preview)
- **Meta:** P3 · task · size S · AGENT
- **State today:** LIKELY DONE — `gh release view v0.1.0` → "Bevel Desktop 0.1.0", published 2026-09-30, assets `Bevel-0.1.0.dmg`, `Bevel-0.1.0-win-x64.zip`, `Bevel-0.1.0-linux-x64.zip`; `site/index.html:381` links the DMG asset; its only dependency `bevel-ev5x` (licence) is closed. Caveat tracked elsewhere: the DMG was uploaded by hand (bevel-9po) and may link Homebrew brotli (bevel-ka6c).
- **Depends on:** none
- **Files:** none
- **Decision:** Close; the hosting decision is GitHub Release assets (free, unmetered), which is what the cask (bevel-gsz) and the update feed (bevel-ym0) will key off.
- **Test first:** n/a
- **Steps:**
  - [ ] Run the Verify command.
- **Verify:** `gh release view v0.1.0 --json assets -q '.assets[].name'` → three names including `Bevel-0.1.0.dmg`
- **Done when:** verified; then `bd close bevel-ayf --reason "v0.1.0 published 2026-09-30 with DMG + win/linux zips as GitHub Release assets"`

### bevel-wcqr — Automation control socket has file-perm-only auth (no nonce), unlike helper/shell-core
- **Meta:** P2 · bug · size M · AGENT
- **State today:** Still true. `AutomationSocketServer.HandleConnectionAsync` (`src/Bevel.Interop/Cli/AutomationSocket.cs:146`) reads one frame and executes it; the only protection is `TrySetOwnerOnly` (0600) + `TrySetDirOwnerOnly` (0700). `AutomationSocketClient.SendAsync` (line 200) sends argv with no proof. `bevelctl` (`src/bevelctl/Program.cs`) is started by the user from a terminal, so it cannot inherit an env nonce the way launcher children do. Stale comment: `AutomationSocketHost.cs:12` says "Registered only for the FM-hosting roles (All / Filer)" — it is the Taskbar role.
- **Depends on:** none
- **Files:** `src/Bevel.Interop/Cli/AutomationSocket.cs`, `src/Bevel.App/AutomationSocketHost.cs` (comment only), `tests/Bevel.Core.Tests/Interop/AutomationSocketTests.cs`
- **Decision:** Token file, same pattern as `ShellCoreEndpoint.ForServer` (`core.token`): the server mints 16 random bytes at `Start()`, writes hex to `<socket path>.token` (0600) BEFORE binding, deletes it on dispose. Wire change: the client's FIRST frame is `Handshake.BuildHelloPayload(nonce, "bevelctl")`, the second is argv; the server validates with `Handshake.ValidateHello` (constant-time) and on failure writes `CommandResult(ExitCodes.ShellNotRunning, "bevel: not authorized")` and closes. Add `AutomationSocket.TokenPathFor(string socketPath)` and `AutomationSocketClient.SendAsync` reads the token itself (signature unchanged, so `bevelctl/Program.cs` needs no edit). `Bevel.Interop` already references `Bevel.ShellCore.Ipc`.
- **Test first:** `tests/Bevel.Core.Tests/Interop/AutomationSocketTests.cs` — add `Connection_without_a_valid_hello_is_refused_and_runs_nothing` (raw socket sends an argv frame first; assert the handler was never invoked) and `Token_file_is_removed_when_the_server_stops`.
- **Steps:**
  - [ ] Write both tests; watch them fail.
  - [ ] Server: mint + write token in `Start()` before `UdsSocketClaim.BindListener`; delete in `DisposeAsync` only if this instance bound the socket.
  - [ ] Server: read hello frame, validate, then read argv frame.
  - [ ] Client: read `TokenPathFor(path)`; missing file → throw `SocketException` equivalent path so `bevelctl` prints "shell isn't running".
  - [ ] Fix the stale role comment in `AutomationSocketHost.cs`.
- **Verify:** `dotnet test tests/Bevel.Core.Tests/Bevel.Core.Tests.csproj --filter "FullyQualifiedName~AutomationSocketTests"` → all pass (existing 5 + 2 new)
- **Done when:** an unauthenticated frame executes nothing; then `bd close bevel-wcqr --reason "bevelctl socket now requires Handshake hello keyed by a 0600 token file"`

### bevel-ym0 — M5: Velopack auto-update integration (UPD-01)
- **Meta:** P2 · task · size L · AGENT+HUMAN-VERIFY
- **State today:** Not started. No Velopack package in `Directory.Packages.props`. `CompositionRoot.AddUpdateChecking` (`src/Bevel.App/CompositionRoot.cs:33-40`) binds `IUpdateSource` to `NoUpdateSource.Instance`. `UpdateCheckService.Latest` is read by nothing. `site/updates/releases.{osx,win,linux}.json` are empty stubs, live at `https://bevel.run/updates/` (HTTP 200).
- **Depends on:** bevel-9po (signed artifacts), bevel-ka6c (portable binary)
- **Files:** `Directory.Packages.props`, `src/Bevel.App/Bevel.App.csproj`, `src/Bevel.App/Updates/VelopackUpdateSource.cs` (new), `src/Bevel.App/CompositionRoot.cs`, `src/Bevel.App/Program.cs`, `packaging/macos/make-velopack.sh` (new), `.github/workflows/release-macos.yml`, `tests/Bevel.Taskbar.Tests/VelopackUpdateSourceTests.cs` (new)
- **Decision:** (1) Feed = GitHub Release assets, zero infra (master-plan decision 8): `updateFeedUrl` whose host is `github.com` → Velopack's GitHub source; any other URL → simple web source (keeps `bevel.run/updates` usable later). (2) Default `updateFeedUrl` stays EMPTY until the human flips it. (3) Core checks AND downloads; it never applies. Applying is a launcher job on a user-chosen restart (UPD-03) — out of scope here, see the new UI bead under bevel-m5. (4) Velopack lives in `Bevel.App` only. UNVERIFIED: every Velopack type/CLI flag — the executor MUST read current Velopack docs (Context7) before coding; do not guess names.
- **Test first:** `VelopackUpdateSourceTests.Check_against_a_local_feed_dir_reports_the_newer_version` — point the source at a temp dir containing a hand-written `releases.osx.json` with one higher version; assert `UpdateAvailability.Available` and `Version`. And `Check_never_throws_on_an_unreachable_feed` → `UpdateAvailability.None`.
- **Steps:**
  - [ ] Add the package; implement `VelopackUpdateSource : IUpdateSource` (never throws; maps "no update" to `UpdateAvailability.None`).
  - [ ] Register it in `AddUpdateChecking` instead of `NoUpdateSource` when running from an installed bundle; keep `NoUpdateSource` for `dotnet run`/tests.
  - [ ] Add Velopack's startup hook as the first statement of `Program.Main` (before the `--selftest-bus` check at `Program.cs:29`).
  - [ ] `make-velopack.sh`: pack the signed `dist/Bevel.app`; then assert `codesign -dv Contents/MacOS/BevelHelper 2>&1 | grep 'Identifier=pl.ikari.bevel.helper'` and `codesign --verify --deep --strict` on the packed bundle. If Velopack's own signing rewrites that identifier, STOP and report — TCC grants would be lost on every update.
  - [ ] Workflow: after `make-dmg.sh`, run `make-velopack.sh` and attach its output files to the release.
  - [ ] HUMAN-VERIFY: install version n-1, grant Accessibility + Screen Recording, update to n, confirm both grants and the helper survive.
- **Verify:** `dotnet test tests/Bevel.Taskbar.Tests/Bevel.Taskbar.Tests.csproj --filter "FullyQualifiedName~VelopackUpdateSourceTests"` → pass
- **Done when:** a release carries a Velopack feed, a shell pointed at it reports the newer version in `UpdateCheckService.Latest`, and the human check passes; then `bd close bevel-ym0 --reason "Velopack source + release packaging; TCC survival verified on <date>"`

### bevel-rkxb — Online updates for Bevel (check, download, apply new versions)
- **Meta:** P2 · feature · size S · AGENT
- **State today:** DUPLICATE OF bevel-ym0 for everything still open. Cadence half shipped in bd06c32 (`UpdateCheckPolicy`, `UpdateCheckState`, `UpdateCheckService`, tests in `tests/Bevel.Core.Tests/UpdateCheckTests.cs` and `tests/Bevel.Taskbar.Tests/UpdateCheckServiceTests.cs`). Its own "still blocked on" list is stale: item 1 (no feed endpoint, counsel gate) is false — the site is live on bevel.run and the gate was lifted 2026-09-30; item 2 = bevel-9po; item 3 = bevel-ym0; item 4 (UI) has no bead. The description's "consider Sparkle" is superseded by UPD-01. The promised "Restart to upgrade" relabel does not exist anywhere in `src/`.
- **Depends on:** none
- **Files:** none
- **Decision:** bevel-ym0 survives (it carries the spec IDs and the hard part). File the UI bead listed under bevel-m5 first so item 4 is not lost, then close this one.
- **Test first:** n/a
- **Steps:**
  - [ ] Confirm the new bead "Surface update availability: Start-menu badge + 'Restart to update'" exists (human/orchestrator creates it).
  - [ ] Close.
- **Verify:** `git show --stat bd06c32 | head -5` → the cadence commit exists
- **Done when:** `bd close bevel-rkxb --reason "Cadence shipped in bd06c32; feed+download = bevel-ym0; UI surfacing = <new bead id>"`

### bevel-q3l — M5: uninstaller / --uninstall (ENG-040)
- **Meta:** P2 · task · size M · AGENT+HUMAN-VERIFY
- **State today:** Nothing exists. No `--uninstall` in `src/Bevel.App/Program.cs`; `BevelCtlParser` has no `register`/`unregister` (the bead's INT-4 pairing is fiction today); `MacOSPal.RegisterAsShellAsync`/`UnregisterAsync` throw `NotImplementedException`. What Bevel leaves on a Mac today: login item (`LoginItemRegistrar.SetEnabled`), `~/.config/bevel/` (settings.db, sockets, lock), `~/.bevel/dock-claimed.json` (`MacOSDockController.MarkerFile`), `$TMPDIR/bevel-{core,filers,health,helper}` + `bevel-restart.log` + `bevel-filer-*.log`, a LaunchServices registration (bevel:// and `public.folder` in `Info.plist`), and TCC rows for `pl.ikari.bevel` and `pl.ikari.bevel.helper`. No launchd plists are installed.
- **Depends on:** none (bevel-tak adds one more directory to remove)
- **Files:** `src/Bevel.App/Uninstall/UninstallPlan.cs` (new), `src/Bevel.App/Uninstall/Uninstaller.cs` (new), `src/Bevel.App/Program.cs`, `tests/Bevel.Taskbar.Tests/UninstallPlanTests.cs` (new; `Bevel.App` is `InternalsVisibleTo` that project)
- **Decision:** `Bevel --uninstall [--dry-run] [--keep-settings]`, handled in `Program.Main` before role dispatch (same place as `--selftest-bus`), because it must work when the shell is dead — so no `bevelctl` verb. `UninstallPlan.Build(UninstallEnvironment env)` is PURE and returns an ordered `IReadOnlyList<UninstallAction>` (record: `Kind`, `Target`, `Description`); `Uninstaller.RunAsync(plan, dryRun)` executes and prints one line per action. Order: stop the running shell (SIGTERM the launcher pid from `shell.lock.pid`, wait ≤10 s — never SIGKILL) → restore the Dock only if `dock-claimed.json` still exists → login item off → `lsregister -u <bundle>` → `tccutil reset All` for both bundle ids → delete the directories above → print "Now move Bevel.app to the Trash". The app does not delete its own bundle.
- **Test first:** `UninstallPlanTests.Plan_lists_every_location_bevel_writes_in_order` (assert kinds + targets against a fake env rooted in a temp dir), `Keep_settings_omits_the_config_dir`, `Dry_run_executes_nothing` (temp files still exist after `RunAsync(plan, dryRun: true)`).
- **Steps:**
  - [ ] Write the three tests against a temp-dir `UninstallEnvironment` (never `BevelConfigDir.Path` — it throws in a test host).
  - [ ] Implement `UninstallPlan` + `Uninstaller`; wire the flag in `Program.Main`.
  - [ ] Dock step: UNVERIFIED whether a fresh `MacOSDockController.SetAutoHideAsync(false)` restores from the marker; if not, port the marker-read from `packaging/macos/bevel-recover.sh`.
  - [ ] HUMAN-VERIFY on a real install: run it, then check System Settings ▸ Login Items, the Dock, and `ls ~/.config/bevel ~/.bevel`.
- **Verify:** `dotnet test tests/Bevel.Taskbar.Tests/Bevel.Taskbar.Tests.csproj --filter "FullyQualifiedName~UninstallPlanTests"` → pass
- **Done when:** `Bevel --uninstall --dry-run` lists every item in State today and the real run leaves none; then `bd close bevel-q3l --reason "Bevel --uninstall: plan+executor, dry-run, human-verified clean leave"`

### bevel-tak — M5: local crash logging + export (offline, ENG s7)
- **Meta:** P3 · task · size M · AGENT
- **State today:** PARTLY DONE — capture exists but is not durable and has no export. `Program.cs:42-44` hooks `AppDomain.CurrentDomain.UnhandledException` → `RestartDiag.Log("FATAL unhandled …")`; `RestartDiag.LogPath` is `Path.GetTempPath()/bevel-restart.log`, so the evidence is wiped on reboot and mixed with restart chatter. `TaskScheduler.UnobservedTaskException` is not hooked anywhere. No export UI.
- **Depends on:** none
- **Files:** `src/Bevel.Core/Diagnostics/CrashLog.cs` (new), `src/Bevel.App/Program.cs`, `src/Bevel.Taskbar/OnboardingWindow.axaml` + `.axaml.cs`, `tests/Bevel.Core.Tests/CrashLogTests.cs` (new)
- **Decision:** Keep `RestartDiag` untouched (CLAUDE.md and `bevel-recover.sh` document that path). Add `CrashLog` in `Bevel.Core`: `static string DefaultDir` = `~/Library/Logs/Bevel` on macOS, `%LOCALAPPDATA%\bevel\logs` on Windows, `~/.local/state/bevel/logs` elsewhere; `static void Write(string dir, string role, int pid, string version, Exception? ex, string? text)` writes one file `crash-<utcStamp>-<role>-<pid>.txt` (sync, swallow-all — the process is dying); keep the newest 50 files. `static Task<string> ExportAsync(string dir, string destZip, IEnumerable<string> extraFiles, CancellationToken)` zips crash files + `bevel-restart.log` + `bevel-filer-*.log` off the UI thread. No network, no consent prompt.
- **Test first:** `CrashLogTests.Write_creates_one_file_per_crash_with_role_pid_and_stack`, `Write_prunes_to_the_newest_fifty`, `Export_zips_crash_files_and_extras`, `Write_never_throws_on_an_unwritable_dir`.
- **Steps:**
  - [ ] Tests first, all against a temp dir.
  - [ ] In `Program.Main`, call `CrashLog.Write` from the existing handler (after `RestartDiag.Log`) and add a `TaskScheduler.UnobservedTaskException` handler that writes too (and calls `SetObserved`).
  - [ ] General tab: add a "Diagnostics" `HeaderedContentControl` with a "Export crash logs…" button; handler awaits a save-file picker then `CrashLog.ExportAsync` on a thread-pool thread; disabled-with-text "No crashes recorded" state when the dir is empty (a real state, not a hidden control).
  - [ ] Re-harvest the Onboarding render if `RenderOnboardingTest` pixels change.
- **Verify:** `dotnet test tests/Bevel.Core.Tests/Bevel.Core.Tests.csproj --filter "FullyQualifiedName~CrashLogTests"` → pass
- **Done when:** a forced crash leaves a file under `~/Library/Logs/Bevel` that survives reboot and the button produces a zip; then `bd close bevel-tak --reason "Durable per-crash files + offline zip export in Settings"`

### bevel-ct7 — M5: Settings app — full v1 surface
- **Meta:** P3 · task · size S · AGENT
- **State today:** PARTLY DONE. The taskbar settings window is `src/Bevel.Taskbar/OnboardingWindow.axaml` (tabs General / Start / Buttons / Clock / Appearance / Tray / Behavior). Startup behaviour: done (`RunAtLoginCheck`, line 109, reconciled against the OS in `ReconcileRunAtLoginAsync`). Permissions: Accessibility and Screen Recording only (`GrantAccessibilityBtn`, `GrantScreenRecBtn`); `ShellPermission` also has `FullDiskAccess`, `AppleEvents`, `InputMonitoring` with no row — and Input Monitoring is required for the idle `Ctrl/Option+Esc` hotkey. Crash-log export: missing (bevel-tak builds it). Update settings (`UpdateFeedUrl`, `UpdateCheckIntervalHours` in `BevelSettings`) have no UI.
- **Depends on:** bevel-tak (export button)
- **Files:** `src/Bevel.Taskbar/OnboardingWindow.axaml`, `src/Bevel.Taskbar/OnboardingWindow.axaml.cs`, `tests/Bevel.Taskbar.Tests/SettingsPlatformGatingTests.cs`
- **Decision:** Remaining scope is exactly two things. (1) One reusable permission row for the three missing `ShellPermission` values, driven by `IPermissionBroker.GetStateAsync`/`RequestAsync`/`PermissionChanged`, each with a one-line "what this unlocks" hint; rows are macOS-only via the existing platform gating. (2) An "Updates" group on General: interval (hours, clamped by `UpdateCheckPolicy.ClampInterval`) + read-only status line; the feed URL is NOT exposed (owner flips the default). No new window, no rename.
- **Test first:** `SettingsPlatformGatingTests` — add `Permission_rows_cover_every_ShellPermission_on_macOS` (enumerate the enum, assert a named row exists for each) so a new enum value cannot ship without UI.
- **Steps:**
  - [ ] Add the test; fail.
  - [ ] Extract the existing status-dot + button + hint trio into a small UserControl used five times (colours from theme tokens only).
  - [ ] Query states off the UI thread; post results back.
  - [ ] Add the Updates group; write through `ISettingsService.UpdateAsync`.
- **Verify:** `dotnet test tests/Bevel.Taskbar.Tests/Bevel.Taskbar.Tests.csproj --filter "FullyQualifiedName~SettingsPlatformGatingTests"` → pass
- **Done when:** all five permissions show live status and the update interval is editable; then `bd close bevel-ct7 --reason "Permissions dashboard complete (5/5), update interval UI; crash export via bevel-tak"`

### bevel-gsz — M5: Homebrew cask (brew install --cask bevel)
- **Meta:** P2 · task · size S · AGENT+HUMAN-VERIFY (tap repo creation is HUMAN)
- **State today:** No cask file anywhere (`Casks/` does not exist). Inputs exist: DMG asset URL `https://github.com/ikari-software/bevel/releases/download/v0.1.0/Bevel-0.1.0.dmg`, sha256 `cc1c0d78274ed98dd919e911053120f4f661389b3f343696240d0fb2f9a6c17c` (`site/downloads/SHA256SUMS.txt`), min macOS 15 (see bevel-9po).
- **Depends on:** bevel-ka6c (a cask must not ship a binary that needs `/opt/homebrew` brotli by accident), bevel-q3l (uninstall stanza)
- **Files:** `packaging/homebrew/bevel.rb` (new), `packaging/homebrew/README.md` (new — only the three tap commands)
- **Decision:** Own tap first (`ikari-software/homebrew-tap`), not homebrew/cask — the official repo has a notability bar a tech preview will not clear; UNVERIFIED whether the token `bevel` is free there. Install line: `brew install --cask ikari-software/tap/bevel`. Stanzas: `version`, `sha256`, `url` (release asset with `#{version}`), `name "Bevel Desktop"`, `homepage "https://bevel.run/"`, `depends_on macos: ">= :sequoia"`, `app "Bevel.app"`, `binary "#{appdir}/Bevel.app/Contents/MacOS/bevelctl"`, `uninstall quit: "pl.ikari.bevel"`, `zap trash: ["~/.config/bevel", "~/.bevel", "~/Library/Logs/Bevel"]`. The source of truth lives in this repo; the tap gets a copy.
- **Test first:** `brew style packaging/homebrew/bevel.rb` → no offenses.
- **Steps:**
  - [ ] Write the cask and README.
  - [ ] HUMAN: create public repo `ikari-software/homebrew-tap`, add `Casks/bevel.rb`.
  - [ ] HUMAN-VERIFY: `brew install --cask ikari-software/tap/bevel` on a Mac, launch, `bevelctl query version`, then `brew uninstall --zap --cask bevel`.
  - [ ] Add a release-lane follow-up note in the README: bump `version` + `sha256` per release (automation is a later bead).
- **Verify:** `brew audit --cask --new ikari-software/tap/bevel` → no errors (after the tap exists)
- **Done when:** install/uninstall round-trips from the tap; then `bd close bevel-gsz --reason "Cask in ikari-software/tap; brew style/audit clean"`

### bevel-cp2 — M5: user guide + known-issues docs
- **Meta:** P3 · task · size M · AGENT
- **State today:** None exist. `docs/` holds specs, plans, design and legal only; `README.md` (123 lines) is developer-facing; `site/index.html` has no guide or known-issues section.
- **Depends on:** bevel-q3l (uninstall command to document), bevel-tak (crash export to document)
- **Files:** `docs/user/guide.md` (new), `docs/user/known-issues.md` (new), `docs/user/uninstall.md` (new), `README.md` (three links under "Status")
- **Decision:** Markdown in-repo, linked from README; no site page yet (the site rule is real renders only, and this is prose). Guide sections, in order: install (DMG) → first run and the two permission grants (app AND helper are separate TCC entries) → taskbar/Start/tray → Filer → themes → `bevelctl` + `bevel://` (state plainly which verbs each surface accepts, per bevel-4x0) → quitting (Start ▸ Turn Off; never `kill -9`, it strands the Dock) → recovering a half-dead shell (`packaging/macos/bevel-recover.sh`) → exporting crash logs. Known issues are sourced from OPEN beads only, one line each with the bead id; no speculation.
- **Test first:** a link check: every relative link in `docs/user/*.md` resolves (`grep -o '](\.\{0,2\}/[^)]*)'` + `test -e`).
- **Steps:**
  - [ ] Draft the three files; every command shown must be copy-pasted from a script or parser that exists (`BevelCtlParser.Usage` is the source for CLI syntax).
  - [ ] Known issues: pull from `bd list --status open --priority-max 2` filtered to user-visible bugs; cite ids.
  - [ ] Add the README links.
  - [ ] Run the `stop-slop` skill over the prose.
- **Verify:** link check prints nothing
- **Done when:** a new user can install, grant, quit and uninstall from the guide alone; then `bd close bevel-cp2 --reason "docs/user: guide, known issues, uninstall"`

### bevel-l3o — M1: per-capability RPC auth scoping for helper services (ScriptingService/InputService)
- **Meta:** P2 · feature · size S · AGENT
- **State today:** PARTLY DONE — design items 1, 3, 4 shipped in ca00f6e. Wire format `x-bevel-token: <capability>:<HMAC-SHA256(nonce, capability)>` on both sides (`HelperClient.BuildAuthMetadata`, `AuthInterceptor.authenticate`); each Swift service passes its own `expectedCapability` (`"supervision"` ×1, `"window"` ×10, `"tray"` ×4); cross-capability rejection is tested (`AuthTests.testAuthenticateRejectsWrongCapability`). `ScriptingService`/`InputService` do not exist in `proto/bevel.helper.v1.proto`. NOT done (item 2): scoping on the C# side is nominal — `HelperLifecycle.NonceToken` exposes the raw nonce, so `MacOSWindowManager.cs:286` and `MacOSSystemTrayHost.cs:196` could mint any capability; `HelperClient._capabilities` is assigned and never read. Also: `AuthInterceptor.capabilityFromMethod` would yield `"window"` but its doc comment and `HelperClientTests` line 87 say `"windows"` (harmless today — the interceptor class is not installed on the server).
- **Depends on:** none
- **Files:** `src/Bevel.Ipc/HelperClient.cs`, `src/Bevel.Pal.MacOS/HelperLifecycle.cs`, `src/Bevel.Pal.MacOS/HelperProcessHost.cs`, `src/Bevel.Pal.MacOS/MacOSWindowManager.cs`, `src/Bevel.Pal.MacOS/MacOSSystemTrayHost.cs`, `tests/Bevel.Pal.MacOS.Tests/HelperClientTests.cs`
- **Decision:** Hand out derived MACs, not the key. Add `Grpc.Core.Metadata HelperLifecycle.AuthFor(string capability)` which throws `InvalidOperationException` for a capability outside the set granted at construction; window manager asks for `"window"`, tray host for `"tray"`. Remove `NonceToken` from the public surface of `HelperLifecycle` (keep it on the host, internal). Delete the dead `_capabilities` field and the `capabilities` parameter chain rather than half-enforcing it. Typed sub-clients for Scripting/Input are deferred to the bead that adds those services.
- **Test first:** `HelperLifecycleTests.AuthFor_refuses_a_capability_that_was_not_granted` and `AuthFor_returns_only_the_requested_capability_header` (new, in `tests/Bevel.Pal.MacOS.Tests/HelperLifecycleTests.cs`).
- **Steps:**
  - [x] Tests first (AuthFor_refuses…, AuthFor_returns_only…).
  - [x] Added AuthFor; both call sites switched; NonceToken removed from the public surface.; switch the two call sites; make `NonceToken` non-public.
  - [x] Removed _capabilities + the whole capabilities parameter chain; "windows"→"window" fixed in Swift comment + C# test. + parameter; fix the `"windows"` → `"window"` wording in the Swift comment and the C# test.
- **Verify:** `dotnet test tests/Bevel.Pal.MacOS.Tests/Bevel.Pal.MacOS.Tests.csproj --filter "FullyQualifiedName~Helper"` → pass
- **Done when:** no type outside `HelperProcessHost` can read the nonce; then `bd close bevel-l3o --reason "Capability HMAC shipped ca00f6e; C# side now hands out per-capability MACs only"`

### bevel-4x0 — bevel:// implements only 2 of 8 automation verbs; 'set' unreachable from CLI/URL
- **Meta:** P3 · task · size S · AGENT
- **State today:** Count is stale: `BevelVerb` now has 10 values; `BevelUrlParser.Parse` (`src/Bevel.Interop/Cli/BevelUrlParser.cs:32-57`) handles `reveal`, `open`, `launch`; `select`/`mkdir`/`delete`/`duplicate`/`move`/`query`/`quit` fall to "unknown bevel:// verb". Since the bead was filed, the URL surface was declared hostile (web pages can fire it — bevel-318a note in `AutomationCommandRouter.LaunchAsync`). `set`: no CLI verb, and both surfaces reject it (`FileManagerShellSurface.SetAsync`, `SpawningShellSurface.SetAsync` throw "not supported").
- **Depends on:** none
- **Files:** `src/Bevel.Interop/Cli/BevelUrlParser.cs`, `tests/Bevel.Core.Tests/Interop/CliParserTests.cs`, `docs/spec/08-os-interop.md` (scope paragraph)
- **Decision:** Do NOT reach parity — document the intentional split. `bevel://` gets only non-destructive verbs: add `select` (`bevel://select?path=…[&path=…]`); `mkdir`/`delete`/`duplicate`/`move`/`quit` return the explicit error `"bevel://<verb> is not available from a URL (untrusted surface); use bevelctl"`; `query` returns `"bevel://query has nowhere to return a result; use bevelctl"`. Note: the router comment claims `quit` is URL-reachable; the parser never allowed it — fix the comment, keep it blocked. `set` is a separate feature (needs a new `FilerCommandKind` and surface work): file a new bead "bevelctl set view|sidebar|toolbar|bounds" rather than widen this one.
- **Test first:** in `CliParserTests` (URL section): `Select_parses_paths`, `Destructive_verbs_are_refused_with_a_pointer_to_bevelctl` (Theory over mkdir/delete/duplicate/move/quit), `Query_is_refused_with_a_reason`.
- **Steps:**
  - [ ] Tests first.
  - [ ] Add the `select` case and the two refusal cases before `default`.
  - [ ] Correct the `QuitAsync` doc comment in `AutomationCommandRouter.cs`.
  - [ ] Add a "Surface scope" table to `docs/spec/08-os-interop.md` §3.3.
- **Verify:** `dotnet test tests/Bevel.Core.Tests/Bevel.Core.Tests.csproj --filter "FullyQualifiedName~CliParserTests"` → pass
- **Done when:** every `BevelVerb` has an explicit, tested URL outcome; then `bd close bevel-4x0 --reason "URL scope decided: reveal/open/launch/select only; destructive verbs refused by name; 'set' split to <new id>"`

### bevel-osp — Verify helper code signature / team-id before launch (defense-in-depth)
- **Meta:** P3 · task · size S · AGENT+HUMAN-VERIFY
- **State today:** Not done. `HelperProcessHost.LaunchAndConnectAsync` (`src/Bevel.Pal.MacOS/HelperProcessHost.cs:67-103`) resolves an absolute path via `ResolveHelperBinary` and calls `Process.Start` with no signature check. The bead's blocker ("needs the signing team-id") is resolved: team `4TP7TPH2K6` and identifier `pl.ikari.bevel.helper` are fixed in `packaging/macos/sign-app.sh:11,36`.
- **Depends on:** none
- **Files:** `src/Bevel.Pal.MacOS/HelperSignatureVerifier.cs` (new), `src/Bevel.Pal.MacOS/HelperProcessHost.cs`, `tests/Bevel.Pal.MacOS.Tests/HelperSignatureVerifierTests.cs` (new)
- **Decision:** Shell out to `/usr/bin/codesign --verify --strict -R='identifier "pl.ikari.bevel.helper" and anchor apple generic and certificate leaf[subject.OU] = "4TP7TPH2K6"' <path>` (exit 0 = trusted); no SecStaticCode P/Invoke — launch is already off the UI thread and rare. `HelperSignatureVerifier.VerifyAsync(string path, Func<string, string[], CancellationToken, Task<int>> run, CancellationToken)` with the runner injected for tests. Enforce ONLY when the resolved path is inside an app bundle (`…/Contents/MacOS/BevelHelper`) and `AllowEnvOverride` is false; dev build outputs are unsigned and stay exempt. On failure throw `InvalidOperationException` (fail closed; the monitor's retry loop owns it). Verify once per resolved path (it is cached in `_helperBinaryPath`).
- **Test first:** `Bundle_helper_with_failing_codesign_is_refused`, `Dev_build_path_skips_verification`, `Requirement_string_pins_identifier_and_team` (assert the exact args handed to the fake runner).
- **Steps:**
  - [ ] Tests first with a fake runner.
  - [ ] Implement; call it right after `ResolveHelperBinary` on first resolution.
  - [ ] HUMAN-VERIFY: signed `/Applications/Bevel.app` still starts its helper; a bundle whose `BevelHelper` was replaced by an ad-hoc-signed copy logs the refusal.
- **Verify:** `dotnet test tests/Bevel.Pal.MacOS.Tests/Bevel.Pal.MacOS.Tests.csproj --filter "FullyQualifiedName~HelperSignatureVerifierTests"` → pass
- **Done when:** a tampered bundled helper is never exec'd; then `bd close bevel-osp --reason "codesign -R requirement (identifier + team) enforced for bundled helper"`

### bevel-kyf — Helper: verify peer UID/credentials on the UDS connection (defense-in-depth)
- **Meta:** P3 · task · size S · AGENT
- **State today:** Not done, but the stated blocker is stale. The helper builds its server with `.http2NIOPosix(address: .unixDomainSocket(path:), transportSecurity: .plaintext)` (`native/helper-macos/Sources/BevelHelper/BevelHelper.swift:150-156`). The pinned transport (checkout in `native/helper-macos/.build/checkouts/grpc-swift-nio-transport`) exposes `HTTP2ServerTransport…ChannelDebuggingCallbacks.onAcceptTCPConnection: (Channel) -> EventLoopFuture<Void>` (`Sources/GRPCNIOTransportCore/Server/HTTP2ServerTransport.swift:241-259`), and NIO channels offer `SocketOptionProvider.unsafeGetSocketOption(level:name:)`. UNVERIFIED: that the Posix config surfaces these callbacks for UDS listeners, and that a failed future closes the connection.
- **Depends on:** none
- **Files:** `native/helper-macos/Sources/BevelHelper/PeerCredentialGuard.swift` (new), `native/helper-macos/Sources/BevelHelper/BevelHelper.swift`, `native/helper-macos/Tests/BevelHelperTests/AuthTests.swift`
- **Decision:** `enum PeerCredentialGuard { static func isSameUser(peerUID: uid_t, ownUID: uid_t = getuid()) -> Bool; static func check(_ channel: any Channel) -> EventLoopFuture<Void> }` — `check` reads `LOCAL_PEERCRED` (`xucred.cr_uid`) through `SocketOptionProvider` and fails the future on mismatch or on any read error (fail closed). Wire it as `onAcceptTCPConnection`. If the UNVERIFIED point is false, fall back to a startup assertion instead: `stat` the socket's parent dir and exit(1) unless it is owned by `getuid()` with mode `0700` — same cross-user guarantee, no transport hook. Do one or the other, not both.
- **Test first:** `AuthTests.testPeerGuardAcceptsSameUID` / `testPeerGuardRejectsOtherUID` (pure function); plus, for the fallback, `testSocketDirGuardRejectsGroupReadableDir`.
- **Steps:**
  - [ ] Open the transport's `HTTP2ServerTransport+Posix.swift` and confirm how `channelDebuggingCallbacks` is set on the config; choose primary or fallback and say which in the commit message.
  - [ ] Tests first; implement; wire.
  - [ ] Existing `WindowServiceSpawnerTests` (real spawned helper over UDS) must still pass.
- **Verify:** `cd native/helper-macos && swift test --filter AuthTests` → pass
- **Done when:** a connection from another UID is dropped before any RPC is parsed (or the helper refuses to start on a non-0700 dir); then `bd close bevel-kyf --reason "<primary|fallback> peer-user guard in helper"`

### bevel-9ucj — Automation channel: harden composite WindowRef bit-budget + pid-reuse edge
- **Meta:** P3 · task · size S · AGENT
- **State today:** Both edges still present in `src/Bevel.App/TaskbarFilerControlClient.cs`: `Compose` = `(regId << 16) | (localId & 0xFFFF)` (line 62) with `_nextReg++` unbounded (line 231) → sign-bit overflow at regId ≥ 32768 and silent truncation of localId > 65535; `Prune` forgets a pid only after a failed dial, so a reused pid before any dial keeps the dead Filer's regId. Note: with one process per Filer window plus the parked pre-warm (bevel-t48y), regIds are consumed per window OPEN, so 32767 is a long-uptime number, not an absurd one.
- **Depends on:** none
- **Files:** `src/Bevel.App/TaskbarFilerControlClient.cs`, `src/Bevel.App/ShellCore/FilerControlEndpoint.cs`, `src/Bevel.App/FilerControlServer.cs`, `tests/Bevel.Taskbar.Tests/FilerControlChannelTests.cs`
- **Decision:** Keep `WindowRef(int Id)` (the Apple Event object model depends on it). (1) Recycle: `RegIdForPid` takes the lowest free id in 1…32767 (free = removed by `Prune`); if none is free, throw `AutomationException("too many file-manager processes")`. A `localId` outside 0…65535 makes that window absent from `QueryWindows` results with one stderr line — never a wrong ref. (2) Epoch: `FilerControlServer` mints `string Instance = Guid.NewGuid().ToString("N")` at construction; `FilerReply` gains `string? Instance`, `FilerRequest` gains `string? ExpectedInstance`. The client records the instance per pid on `QueryWindows`; a different instance for a known pid ⇒ drop the old mapping and assign a fresh regId; `Select`/`QuerySelection` send `ExpectedInstance` and the server replies `FilerReply.Fail("stale window reference")` on mismatch. Register both new properties in `FilerJsonContext` (source-generated JSON).
- **Test first:** `FilerControlChannelTests`: `RegIds_are_recycled_after_prune_and_never_exceed_15_bits`, `Reused_pid_with_a_new_instance_does_not_inherit_the_old_ref` (two servers on the same fake pid socket path, sequentially), `Select_with_a_stale_instance_fails_cleanly`.
- **Steps:**
  - [ ] Tests first.
  - [ ] Free-list + bounds in the client; instance on server/protocol/client.
  - [ ] Old peers: a null `Instance`/`ExpectedInstance` means "no check" so a mixed-version shell during an update still works.
- **Verify:** `dotnet test tests/Bevel.Taskbar.Tests/Bevel.Taskbar.Tests.csproj --filter "FullyQualifiedName~FilerControlChannelTests"` → pass
- **Done when:** no input can produce a negative or misrouted composite ref; then `bd close bevel-9ucj --reason "regId recycling within 15 bits + per-Filer instance epoch"`

### bevel-7c6m — Mac App Store is blocked by entitlements, not licence — pick real channels
- **Meta:** P3 · task · size S · HUMAN (channel choice) + AGENT (record it)
- **State today:** Finding re-confirmed against the tree: `packaging/macos/Bevel.entitlements` requests `cs.allow-jit`, `cs.allow-unsigned-executable-memory`, `cs.disable-library-validation`, `automation.apple-events` and has no `app-sandbox`. Nothing is blocked on this. Channels already in motion: direct DMG (shipped), Homebrew cask (bevel-gsz). No doc records the "no MAS" constraint, so it lives only in this bead.
- **Depends on:** none
- **Files:** `docs/spec/09-engineering-plan.md` (§6 distribution table), `docs/legal/open-core-boundary.md` (only if a store affects licensing)
- **Decision:** Agent-side: record the constraint where the distribution table already lives — one row "Mac App Store: not possible (no sandbox; hardened-runtime exceptions; writes `com.apple.dock` prefs)" — then close. Recommended channel order for the human to confirm or change: direct DMG + own-tap cask now; Setapp and Microsoft Store are business decisions (accounts, revenue share, review) and get their own beads only when the owner wants them. Note for accuracy: after bevel-ka6c, `disable-library-validation` may no longer be needed for brotli, but single-file self-extraction of native libs still relies on it (`build-app.sh:22-25`) — do not remove it on the strength of ka6c alone.
- **Test first:** n/a (doc change); check: `grep -n 'Mac App Store' docs/spec/09-engineering-plan.md` returns the new row.
- **Steps:**
  - [ ] Add the row + a two-line rationale citing the entitlement keys.
  - [ ] HUMAN: say yes/no to Setapp and Microsoft Store; if yes, create one bead each.
- **Verify:** the grep above prints one line
- **Done when:** the constraint is in the spec; then `bd close bevel-7c6m --reason "No-MAS constraint recorded in 09 §6; channels: DMG + tap cask; Setapp/MS Store = owner call"`

### bevel-legal-branding — Parked counsel asks: trade-dress + .beveltheme licensing (Bevel Desktop decided)
- **Meta:** P2 · decision · size S · HUMAN
- **State today:** Engineering is finished (0072d01; IP changes 1–5; vendor marks removed; site live at https://bevel.run). The bead is `in_progress` only as the holder of the counsel packet `docs/legal/open-questions-parked.md` (research brief: `docs/legal/2026-09-29-branding-research-brief.md`). Its notes are internally stale in two places an executor might trip on: the 2026-09-29 note says the site and `updateFeedUrl` are gated by this bead — both gates were lifted on 2026-09-30; and it names `bevel.ikari.software` as canonical — the canonical host is `bevel.run`.
- **Depends on:** none; blocks nothing in this cluster (update feed default is an owner go-ahead, tracked in bevel-ym0)
- **Files:** `docs/legal/open-questions-parked.md` (read; edit only to record answers)
- **Decision:** No agent work. An `in_progress` P2 that nobody is working distorts `bd ready`/status views — recommend the human run `bd update bevel-legal-branding --status open --priority 3` (or `bd defer`), keeping it as the packet holder.
- **Human checklist:**
  - [ ] Decide whether to engage counsel at all before 1.0 (cost) — if no, add a dated "accepted risk" line to the parked file and close.
  - [ ] If yes: send the section below the horizontal rule of the research brief (it is written to be pasted with no repo access).
  - [ ] Ask explicitly about the three still-open items: compound name "Bevel Desktop" clearance; trade-dress closeness of the default skins; licensing/disclaimer/provenance for third-party `.beveltheme` git repos.
  - [ ] Confirm the vendor app icons in `tests/fixtures/hero-apps/` were replaced with open-licensed ones and history purged (2026-09-30 decision; UNVERIFIED in this pass).
  - [ ] Record each answer in `docs/legal/open-questions-parked.md`.
- **Verify:** `grep -c '^- \[ \]' docs/legal/open-questions-parked.md` → 0 open questions (UNVERIFIED: the file may not use checkboxes; otherwise every question has a dated answer)
- **Done when:** every parked question has an answer or an accepted-risk line; then `bd close bevel-legal-branding --reason "Counsel answers / accepted risk recorded in docs/legal/open-questions-parked.md"`
