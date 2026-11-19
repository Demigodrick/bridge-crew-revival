# STBC Startup Skip

Standalone BepInEx plugin that removes two startup blockers:

- Desktop's **Select Controller** / press-any-button panel.
- VR's controller/recenter prompt.

It does not modify `StbcRevival`, RollPatch, game assets, or the game's input code. The plugin marks the two existing intro elements complete; it does not hide them while leaving the startup sequence blocked.

## Desktop input behaviour

No controller mode is forced. Bridge Crew's `InputManager.Update()` continues to detect keyboard/mouse and gamepad input every frame and switches its active controller type accordingly. This preserves normal desktop keyboard/mouse and controller use without the opening confirmation screen.

## Build and install locally

Close the game, then build the project from this folder:

```powershell
dotnet restore StartupSkip.csproj --ignore-failed-sources
dotnet build StartupSkip.csproj -c Release --no-restore
```

Copy the only output file into the game's plugins folder:

```text
startup-skip\bin\Release\net35\StartupSkip.dll
  -> <game install>\BepInEx\plugins\StartupSkip.dll
```

(If your game is installed somewhere other than the default Steam location, pass `-p:GameDir="<path>"` to both commands.)

To remove the feature, delete that one DLL. No game or Revival files need restoring.

## Package with a future Revival release

Keep this project separate. Build it, then add only:

```text
startup-skip\bin\Release\net35\StartupSkip.dll
```

to the release's `BepInEx\plugins\` payload next to `StbcRevival.dll`. Do not merge the source into Revival and do not edit or replace `RollPatch.dll`.

## Technical note

The stock desktop gate is `ControllerCheckElement`. It waits for a three-second timer and a new input edge, even though `InputManager.Update()` already does automatic input-device switching. The VR prompt is `CenterViewIntroElement`; normal completion sends `IntroViewRecenteredEvent`. Startup Skip completes both elements and emits that same event for the VR path, so the follow-on calibration-warning state is consistent without moving or recentering the player's headset.
