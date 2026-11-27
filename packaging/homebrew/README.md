# Bevel Homebrew tap

Bevel ships its own tap so you don't need the official `homebrew/cask` repo:

```bash
brew tap ikari-software/tap
brew install --cask bevel
bevelctl query version
```

To remove:

```bash
brew uninstall --zap --cask bevel
```

## For maintainers: publishing a new version

1. Bump `version` and `sha256` in `Casks/bevel.rb` in the
   [`ikari-software/homebrew-tap`](https://github.com/ikari-software/homebrew-tap)
   repo (the cask in this repo is the source of truth — copy it over on release).
2. The release workflow uploads the DMG to GitHub Releases; the cask URL
   pattern `v#{version}/Bevel-#{version}.dmg` must match the uploaded asset name.
