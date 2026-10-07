# Endless Easy

Makes **BOMBANANA!** endless mode more forgiving: a configurable bomb-timer multiplier and extra
tolerated mistakes, applied to every wave.

## What it changes

The mod tunes the game's own `EndlessModeConfig` object. Values below are the real shipped ones,
captured from a running game:

| Lever | Game field | Vanilla | With defaults | Default |
| --- | --- | --- | --- | --- |
| Starting bomb timer | `StartTimeSeconds` | 150 s (2:30) | **225 s (3:45)** | x1.5 |
| Per-difficulty time bonus | `EasyTimeBonus` | 25 | 38 | scaled |
| " | `MediumTimeBonus` | 40 | 60 | scaled |
| " | `HardTimeBonus` | 55 | 82 | scaled |
| Mistakes tolerated per wave | `StrikesPerWave` | 3 | **5** | +2 |

Targets are computed from the **vanilla baseline captured on first use**, never from the current
value, so repeated application can never compound.

## Configuration

`BepInEx/config/BOMBANANA.EndlessEasy.cfg`:

```ini
[General]
Enabled = true
OnlyApplyAsHost = true

[Time]
TimeMultiplier = 1.5
MaxStartTimeSeconds = 0
ScaleTimeBonuses = true

[Difficulty]
HealthBonus = 2

[Modules]
DisableMorse = false
ForceCableOnly = false
CableModuleName =

[Debug]
VerboseLogging = true
```

* **Enabled** — master switch.
* **OnlyApplyAsHost** — only apply when you are the server/host. Endless mission data is
  synchronised over the network, so a non-host client changing the config can desync the lobby.
* **TimeMultiplier** — `1.5` = 50% more time. `1` disables this lever.
* **MaxStartTimeSeconds** — safety cap on the rewritten timer, in seconds. `0` = no cap.
* **ScaleTimeBonuses** — also scale the per-difficulty bonuses, so the multiplier holds whatever
  difficulty a wave rolls.
* **HealthBonus** — extra mistakes tolerated per wave. `0` disables this lever.
* **DisableMorse** — keep the Morse code module out of the puzzle pool. See below.
* **ForceCableOnly** — make endless waves use only the wire/cable module. See below.
* **CableModuleName** — module name used by `ForceCableOnly`. Empty means auto-detect.
* **VerboseLogging** — logs the vanilla baseline, every value written, and the lobby start gates.

## Only the host needs this mod

The host's values are what everyone plays with: the bomb timer is pushed to clients by
`Bomb.StartTickLoopClientRpc(int timeRemaining)` and `BroadcastTimer(int timer)`, and
`Bomb.Health` / `MaxHealth` / `Outcome` are `NetworkVariable`s that the server owns.

A client without the mod still shows the **vanilla timer in the lobby panel**, because that panel
reads each client's own local `EndlessModeConfig`. That is cosmetic: the in-mission countdown and
the strike count both come from the host. This was confirmed in a live three-player run — a
non-modded client displayed 2:30 in the lobby while the mission was played at the host's 25:00.

`OnlyApplyAsHost` defaults to `true` for exactly this reason. Set it to `false` if you want clients
that also have the mod to rewrite their configs, which makes their lobby panel agree too.

## Keeping the Morse module out

```ini
[Modules]
DisableMorse = true
```

BOMBANANA's module registry already takes an explicit `allowMorse` flag on both of its puzzle
selectors, so the mod simply forces that flag to `false` with a Harmony prefix — no module data is
edited, no entries are ripped out of the registry, and the game's own "Morse not allowed" path does
the rest:

```csharp
bool TryPickRandomPuzzleAtDifficulty(Difficulty, HashSet<string> usedModuleNames,
                                     bool allowMorse, Random, out Module)
bool TryBuildPuzzleCandidates(Difficulty, HashSet<string> usedModuleNames,
                              bool allowMorse, bool uniqueOnly, out List<Module>)
```

This applies wherever the game offers the module, not only in endless mode. Default is `false`, so
nothing changes unless you ask for it.

## Making every wave the same module

```ini
[Modules]
ForceCableOnly = true
```

BOMBANANA has no "allowed modules" setting anywhere — `EndlessModeConfig`'s complete member list is
only the timer, strikes, cover animation, the three time bonuses, the wave tiers and two
avoid-repeat booleans. So this works at the pick point instead. The endless wave builder selects
modules by name:

```csharp
bool TryPickPuzzleModule(Difficulty, HashSet<string> used, Random, bool avoidRecent,
                         out string moduleName)
bool TryPickAllowedPuzzleModule(HashSet<string> used, Random, bool avoidRecent,
                               out string moduleName)
```

`out string` is a plain managed `System.String`, so a Harmony postfix hands back the cable module's
name instead. `AvoidRepeatNormalModule` / `AvoidRepeatChaosModule` are cleared at the same time,
because a wave made of one module type needs the same name more than once.

If the cable module's name cannot be resolved from the registry, **nothing is rewritten** and the
log says so — a wrong guess would otherwise turn every module into a name the game cannot spawn.
Pin it yourself with `CableModuleName` if auto-detection fails; the log lists every registry name.

Verified in a live three-player run. The module registry's own lookup, not a guess, confirms the
name:

```
CableOnly: cable module verified via auto-detection as 'Cable'.
CableOnly: module picker invoked (call #1), game chose 'Direction'.
CableOnly: 'Direction' -> 'Cable' (replacement #1..5).
```

Five picks, five replacements: a five-module wave came out all-wires. Note the game kept rolling
`Direction` because the avoid-repeat guards are cleared for this feature; the postfix overrides
every pick, so the mix does not matter.

## Installation

Install with r2modman / Thunderstore Mod Manager. Dependencies come along automatically:

* `Bombanana_Modding-BepInExPack_BOMBANANA`
* `JekaJeka0-BOMBANANA_Library`

## How it works, and what it deliberately does not do

`EndlessModeConfig` is a `ScriptableObject` singleton holding the endless tuning: the starting
timer, per-difficulty time bonuses, tolerated strikes per wave, and per-wave difficulty tiers.
The mod resolves it through BOMBANANA Library's reflection API and rewrites those fields. There
is **no Harmony patch on game logic**, so no hard-coded game method signature to break.

Two things worth knowing, both established by decompiling the game:

* The per-wave mission object (`EndlessMissionData`) **overrides** the virtual `GetLength()` and
  `GetHealth()` of its base class, so writing `MissionData.Length` / `.Health` directly does
  nothing. The mod therefore tunes the config those overrides derive from.
* **Endless mode needs three players, and no mod setting can change that.** The lobby pins
  `MinPlayerCount`/`MaxPlayerCount` to 3 and `AreLobbyRolesValidForStart()` returns false with one
  player. This was tested directly: writing `MinPlayerCount` is reverted by the game every frame.
  BOMBANANA! is a three-player game in every mode. Bring two friends.

## Known issues

* **Three players required**, see above. This is not a bug and cannot be worked around by config.
* **Host only by default** — see `OnlyApplyAsHost`.
* **Applies to a run started after the config is written.** The mod rewrites the endless config
  shortly after launch; a run already in progress keeps its original timer.
* **Requires BOMBANANA Library.** Hard dependency: without it the plugin is skipped entirely.
* The internals of `EndlessMissionData.GetLength()` are native IL2CPP and not readable, so
  `StartTimeSeconds` is the value known to feed it. The change is visible in the lobby's endless
  panel (2:30 becomes 3:45 with the defaults), which is how it was verified.

## Changelog

* **1.5.0** — Verified `ForceCableOnly` in a live three-player run: a five-module wave came out
  all-wires. Cable-name resolution now validates each candidate through `ModuleRegistry.Get(string)`
  rather than scanning the registry, and the registry scan itself is fixed — `Registry<,>` declares
  its statics on the open generic type, which .NET reflection cannot see through a derived class.
* **1.3.0** — Added `[Modules] ForceCableOnly` (one module type per wave) and `CableModuleName`.
  Corrected `OnlyApplyAsHost`: the host is authoritative for the timer and strikes, so only the
  host needs the mod; a non-modded client's lobby panel just shows a stale local number.
* **1.1.0** — Added `[Modules] DisableMorse`, which forces the game's own `allowMorse` selector flag
  so the Morse module is never offered.
* **1.0.0** — Bombanana endless values verified in game (timer 2:30 -> 3:45). Removed the
  `OverrideMinPlayerCount` testing knob after it was proven ineffective: the game re-pins the
  player count, and BOMBANANA! needs three players in every mode. Lobby start gates are now
  logged instead, so a blocked start is diagnosable.
* **0.3.x** — Fixed a dead heartbeat: `BOMBANANA.Library.CoroutineApi` starts nothing on this
  build (`Start` looks up `StartCoroutine(IEnumerator)` by exact signature and silently no-ops
  when it misses), so the mod never ran. Replaced with a Harmony postfix on the game's own
  `Update` for a guaranteed main-thread tick.
* **0.2.0** — Retargeted to `EndlessModeConfig`. The 0.1.0 approach wrote `MissionData.Length` /
  `.Health`, which the endless mission's overridden getters ignore.
* **0.1.0** — Initial release.
