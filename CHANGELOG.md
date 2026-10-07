# Changelog

## 1.5.0

Verified `ForceCableOnly` in a live three-player run: a five-module endless wave came out entirely
wires. Cable-name resolution now validates each candidate through `ModuleRegistry.Get(string)`
instead of scanning the registry, and the registry scan itself is fixed — `Registry<,>` declares its
statics on the open generic type, which .NET reflection cannot see through a derived class.

## 1.3.0

Added `[Modules] ForceCableOnly` (one module type per wave) and `CableModuleName`.

Corrected `OnlyApplyAsHost`: the host is authoritative for the timer and strikes, so only the host
needs the mod. A non-modded client's lobby panel just shows a stale local number.

## 1.1.0

Added `[Modules] DisableMorse`, which forces the game's own `allowMorse` selector flag so the Morse
module is never offered.

## 1.0.0

BOMBANANA endless values verified in game: the timer goes from 2:30 to 3:45 at the default 1.5x.

Removed the `OverrideMinPlayerCount` testing knob after it was proven ineffective — the game re-pins
the player count every frame, and BOMBANANA! requires three players in every mode. Lobby start gates
are logged instead, so a blocked start is diagnosable.

## 0.2.0

Retargeted to `EndlessModeConfig`. The 0.1.0 approach wrote `MissionData.Length` / `.Health`, which
the endless mission's overridden `GetLength()` / `GetHealth()` ignore.

## 0.1.0

Initial release.
