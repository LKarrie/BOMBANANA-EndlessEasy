# Endless Easy

Makes **BOMBANANA!** endless mode more forgiving: more time per wave, more tolerated mistakes, and
two optional ways to control which modules a wave can contain.

## What it changes

### Difficulty

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

### Modules — both optional, off by default

| Option | Effect | Default |
| --- | --- | --- |
| `[Modules] EnabledModules` | Whitelist: the ONLY module types endless waves may use, comma-separated. Empty means the game's normal module pool. | empty |
| `[Modules] DisableMorse` | Drops the Morse module from the normal pool, using the game's own `allowMorse` selector flag. | `false` |

Details: [choosing which modules endless waves may use](#choosing-which-modules-endless-waves-may-use)
and [keeping the Morse module out](#keeping-the-morse-module-out).

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
EnabledModules =

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
* **EnabledModules** — the ONLY modules endless waves may use. Empty (the default) means the
  game's normal module pool. See below.
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

## Choosing which modules endless waves may use

```ini
[Modules]
EnabledModules = Cable
```

Comma-separate to allow more than one:

```ini
EnabledModules = Cable, Calculator, Direction
```

Leave it **empty** for the game's normal pool — that is the default, so nothing changes unless you
opt in.

### Module names this game ships

| | | | |
| --- | --- | --- | --- |
| `Cable` | `Calculator` | `Direction` | `ColorSlider` |
| `Symbol` | `Piano` | `Switch` | `MonkeySays` |
| `Morse` | `Soundboard` | `Maze` | `Pressure` |
| `Slider` | `Alarm` | | |

Fourteen in total. This list comes from the game's own `ModuleRegistry` asset, and the mod does not
take it on faith: at startup it asks the registry about every one of them and logs the result, so a
name that no longer exists is reported rather than failing silently later.

```
ModuleWhitelist: 14/14 known module names resolve in this build: Cable, Calculator, ...
```

So "only wires and keypads" is `EnabledModules = Cable, Calculator`, and "everything except Morse"
is `DisableMorse = true` (see the next section) rather than listing the other thirteen.

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

`out string` is a plain managed `System.String`, so a Harmony postfix hands back a different name,
drawn at random from your list. `AvoidRepeatNormalModule` / `AvoidRepeatChaosModule` are cleared at
the same time, because a wave built from a short list needs the same name more than once.

A **whitelist** rather than a blacklist is deliberate: replacing a pick requires a pool of names to
replace it *with*, and the whitelist is exactly that pool.

Every name you write is checked against the module registry's own lookup (`ModuleRegistry.Get`)
before anything is rewritten. Unknown names are reported and dropped; if nothing survives
validation, **no rewriting happens at all**. A typo therefore cannot turn a wave into modules the
game cannot spawn. Modules the game classifies as chaos are accepted if you name them, but the log
tells you which ones they are.

Verified in a live three-player run, with the registry doing the confirming rather than a guess:

```
ModuleWhitelist: module picker invoked (call #1), game chose 'Direction'.
ModuleWhitelist: 'Direction' -> 'Cable' (replacement #1..5).
```

Five picks, five replacements: a five-module wave came out all-wires.

## Installation

Install with r2modman / Thunderstore Mod Manager. Dependencies come along automatically:

* `Bombanana_Modding-BepInExPack_BOMBANANA`
* `JekaJeka0-BOMBANANA_Library`

## How it works, and what it deliberately does not do

`EndlessModeConfig` is a `ScriptableObject` singleton holding the endless tuning: the starting
timer, per-difficulty time bonuses, tolerated strikes per wave, and per-wave difficulty tiers.
The mod resolves it through BOMBANANA Library's reflection API and rewrites those fields.

The two difficulty levers therefore patch nothing at all. The module features do patch game logic —
a Harmony postfix on `EndlessMissionData`'s two module pickers, and a prefix on
`ModuleRegistry`'s selectors for `DisableMorse` — but both targets are found **by name through
reflection at startup**, so the mod still compiles against no game assembly. Each patch is
installed in a try/catch and reports failure to the log rather than silently doing nothing.

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

* **1.7.0** — The available module names are now listed in the config and the README (taken from the
  game's own `ModuleRegistry` asset), and the mod verifies them against the registry at startup
  instead of assuming they still exist. Chaos modules are accepted when named explicitly rather than
  being silently refused.
* **1.6.0** — Replaced `ForceCableOnly` / `CableModuleName` with a general `[Modules] EnabledModules`
  whitelist, empty by default. Every name is validated against `ModuleRegistry.Get` before use, and
  invalid names are reported and ignored instead of being assumed correct.
* **1.5.0** — Verified the one-module-type feature in a live three-player run: a five-module wave
  came out all-wires. Name resolution validates each candidate through `ModuleRegistry.Get(string)`
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
