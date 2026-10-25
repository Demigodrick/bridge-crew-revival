# Bridge Crew Revival uninstaller for Windows:
#
#   irm https://demigodrick.github.io/bridge-crew-revival/uninstall.ps1 | iex
#
# Runs the installer with -Uninstall: removes the mod and switches BepInEx off. Your settings stay in the
# game's BepInEx folder (delete it too if you like).
& ([scriptblock]::Create((Invoke-RestMethod https://demigodrick.github.io/bridge-crew-revival/install.ps1))) -Uninstall
