# SynthPerfFix v1.4.0

Fixes stuttering on **custom stages** on the Unity 6 branch of Synth Riders (6000.3.x, URP Render Graph) - recurring 15-30 ms frame hitches and raised frame-time jitter that don't occur on the 2021 branch. One DLL works on both game branches (it's simply idle where there's nothing to fix).

## What causes the stutter

Custom SDK stages drive their strobe/color reactivity through four hidden "StrobeReciever" cameras that each render into a tiny 2x2 texture every frame. On the old Unity 2021 branch these cost nothing. On Unity 6, every camera pass carries significant Render Graph overhead - measured to cause the hitching *regardless of what the cameras render* (even completely empty passes stutter identically). The only clean number of receiver passes is zero.

## What the mod does

Default mode **Emulate** removes the cameras from the equation while keeping the light show:

- The receiver cameras are disabled - zero extra render passes.
- Each frame, the mod reads the strobe color the game is animating (on the small quad each receiver used to photograph) and writes it into the receiver's render texture directly.
- Stage materials sample those textures exactly as before, so **strobe visuals keep working on every existing stage - no stage rebuilds needed.**

Result: Unity 6 custom-stage frame timing matches the 2021 branch (verified with per-frame timing instrumentation), with strobe color reactivity intact.

## Install

Drop `SynthPerfFix.dll` into your `Mods` folder (requires MelonLoader). On launch you'll see one line in the console confirming the mod is running. That's it.

## Config (UserData/MelonPreferences.cfg -> [SynthPerfFix])

- `ReceiverMode` (default `Emulate`) - `Emulate` / `Disable` (receivers off, strobe colors freeze) / `Throttle` (partial mitigation, some hitching remains) / `MaskTest` (diagnostic) / `Off` (mod does nothing)
- `StrobeColorProperty` (default `_Color`) - material property read in Emulate mode; only change if a specific stage's strobes don't react
- `ToggleKey` (default `F4`) - toggles the fix live in-game for A/B comparison; set `None` to disable
- `ShaderWarmupEnabled` (default `false`) - optional `Shader.WarmupAllShaders()` on song load
- `ThrottleInterval` (default `8`) - Throttle mode only
- `DebugLogging` (default `false`) - verbose console output for troubleshooting

## Troubleshooting

- **Strobe colors frozen on one specific stage:** its receiver quads may use a different shader; set `DebugLogging = true`, play the stage, and report the log.
- **Want to see it working:** press F4 mid-song to toggle the fix off/on and feel the difference.

## Compatibility

- Both game branches (Unity 2021.3.45f2 (not really needed in this build) and Unity 6000.3.x), MelonLoader 0.7.x, .NET 6 / IL2CPP.
- Compatible with SynthStrobeRGB - Emulate relays whatever strobe colors are active, including modded ones.
- Built-in (non-custom) stages have no receiver cameras and are unaffected.

## Building from source

Set `GamePath` in `SynthPerfFix.csproj` to your Synth Riders install, then `dotnet build -c Release`. References only MelonLoader-bundled assemblies.

## Acknowledgements

Built on [MelonLoader](https://melonwiki.xyz/).
Not affiliated with or endorsed by Kluge Interactive.
