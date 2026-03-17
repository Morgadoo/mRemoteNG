# typed: false
# frozen_string_literal: true

# Homebrew cask for mRemoteNG
# Place in: $(brew --repository)/Library/Taps/homebrew/homebrew-cask/Casks/m/mremoteng.rb
# Or in a custom tap: https://docs.brew.sh/Taps

cask "mremoteng" do
  version "1.78.2"

  on_arm do
    url "https://github.com/mRemoteNG/mRemoteNG/releases/download/v#{version}/mRemoteNG-#{version}-macos-arm64.dmg"
    sha256 "UPDATE_WITH_ACTUAL_SHA256_ARM64"
  end

  on_intel do
    url "https://github.com/mRemoteNG/mRemoteNG/releases/download/v#{version}/mRemoteNG-#{version}-macos-x64.dmg"
    sha256 "UPDATE_WITH_ACTUAL_SHA256_X64"
  end

  name "mRemoteNG"
  desc "Multi-protocol remote connection manager (SSH, RDP, VNC, Telnet)"
  homepage "https://mremoteng.org"

  livecheck do
    url :url
    strategy :github_latest
  end

  auto_updates true
  depends_on macos: ">= :monterey"

  app "mRemoteNG.app"

  # CLI launcher
  binary "#{appdir}/mRemoteNG.app/Contents/MacOS/mRemoteNG.Avalonia", target: "mremoteng"

  zap trash: [
    "~/Library/Application Support/mRemoteNG",
    "~/Library/Logs/mRemoteNG",
    "~/Library/Preferences/org.mremoteng.mRemoteNG.plist",
    "~/Library/Saved Application State/org.mremoteng.mRemoteNG.savedState",
  ]

  caveats <<~EOS
    mRemoteNG requires FreeRDP for RDP connections on macOS.
    Install it with:
      brew install freerdp

    For VNC, SSH, Telnet, and HTTP connections no additional packages are needed.
  EOS
end
