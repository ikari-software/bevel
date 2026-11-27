cask "bevel" do
  version "0.1.0"
  sha256 "cc1c0d78274ed98dd919e911053120f4f661389b3f343696240d0fb2f9a6c17c"

  url "https://github.com/ikari-software/bevel/releases/download/v#{version}/Bevel-#{version}.dmg"
  name "Bevel Desktop"
  desc "Cross-platform desktop shell replacement (taskbar, Start menu, file manager)"
  homepage "https://bevel.run/"

  depends_on macos: ">= :sequoia"

  app "Bevel.app"
  binary "#{appdir}/Bevel.app/Contents/MacOS/bevelctl"

  uninstall quit: "pl.ikari.bevel"

  zap trash: [
    "~/Library/Logs/Bevel",
    "~/.bevel",
    "~/.config/bevel",
  ]
end
