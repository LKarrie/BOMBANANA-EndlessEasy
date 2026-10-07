using System;
using System.Collections;
using System.Reflection;
using BOMBANANA.Library.Reflection;
using HarmonyLib;

namespace BombananaEndlessEasy;

/// <summary>
/// Keeps the Morse module out of the puzzle pool.
///
/// The game already supports this itself. BOMBANANA's module registry takes an explicit
/// allowMorse flag on both of its puzzle selectors:
///
///   class ModuleRegistry : Registry&lt;ModuleRegistry, ModuleRegistry.Module&gt;
///       bool TryPickRandomPuzzleAtDifficulty(Difficulty, HashSet&lt;string&gt; usedModuleNames,
///                                            bool allowMorse, Random, out Module)
///       bool TryBuildPuzzleCandidates(Difficulty, HashSet&lt;string&gt; usedModuleNames,
///                                     bool allowMorse, bool uniqueOnly, out List&lt;Module&gt;)
///   struct ModuleRegistry.Module { int Id; string Name; Difficulty Difficulty; GameObject Prefab; ... }
///
/// So instead of ripping entries out of the registry (which other code may look up by id),
/// this forces the flag to false. A Harmony prefix binds to the parameter by name, so one
/// prefix method serves both targets.
///
/// The method bodies are native IL2CPP and unreadable, so where the flag's original value
/// comes from is unknown -- forcing it at the registry is the narrowest point that covers
/// every caller. Whether the detour also intercepts calls made from native game code is the
/// one thing this cannot prove offline, hence the runtime logging.
/// </summary>
internal static class ModuleFilter
{
    private static readonly string[] RegistryTypeNames = { "ModuleRegistry", "BombGame.ModuleRegistry" };
    private static readonly string[] MorseFlagMethods =
    {
        "TryPickRandomPuzzleAtDifficulty",
        "TryBuildPuzzleCandidates",
    };

    private static int _calls;
    private static int _forced;
    private static bool _firstCallLogged;
    private static bool _entriesLogged;
    private static int _entriesAttempts;

    private const int MaxEntryAttempts = 60;   // ~30s at a 0.5s poll

    internal static void Install(Harmony harmony)
    {
        if (!Plugin.DisableMorse.Value)
        {
            Plugin.Log.LogInfo("ModuleFilter: DisableMorse is off; the game keeps the Morse module.");
            return;
        }

        int patched = 0;
        foreach (string methodName in MorseFlagMethods)
        {
            if (ForceAllowMorseFalse(harmony, methodName)) patched++;
        }

        if (patched == 0)
        {
            Plugin.Log.LogError(
                "ModuleFilter: could not patch any Morse selector; the Morse module will still " +
                "appear. Tried: " + string.Join(", ", MorseFlagMethods));
        }
        else
        {
            Plugin.Log.LogInfo($"ModuleFilter: Morse disabled via {patched} selector patch(es).");
        }

        Poll();
    }

    /// <summary>
    /// Retried from the heartbeat rather than done once at load: the registry's data is empty
    /// while plugins load, so an early read legitimately returns nothing. The first version
    /// retired after one such read and logged "0 module(s)", which is useless for diagnosis.
    /// </summary>
    internal static void Poll()
    {
        if (_entriesLogged) return;
        if (!Plugin.DisableMorse.Value) return;

        _entriesAttempts++;
        LogRegistryEntries();

        if (!_entriesLogged && _entriesAttempts >= MaxEntryAttempts)
        {
            _entriesLogged = true;
            Plugin.Log.LogWarning(
                $"ModuleFilter: gave up listing registry modules after {_entriesAttempts} attempts; " +
                "the registry stayed empty. Names can still be verified with ModuleRegistry.Get.");
        }
    }

    private static bool ForceAllowMorseFalse(Harmony harmony, string methodName)
    {
        Type type = Resolve(RegistryTypeNames);
        if (type == null)
        {
            Plugin.Log.LogError($"ModuleFilter: type ModuleRegistry not found for {methodName}.");
            return false;
        }

        MethodInfo target = type.GetMethod(
            methodName,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);

        if (target == null)
        {
            Plugin.Log.LogError($"ModuleFilter: ModuleRegistry.{methodName} not found.");
            return false;
        }

        MethodInfo prefix = typeof(ModuleFilter).GetMethod(
            nameof(NoMorsePrefix), BindingFlags.Static | BindingFlags.NonPublic);

        try
        {
            harmony.Patch(target, prefix: new HarmonyMethod(prefix));
            Plugin.Log.LogInfo($"ModuleFilter: patched ModuleRegistry.{methodName} (force allowMorse=false).");
            return true;
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"ModuleFilter: patching ModuleRegistry.{methodName} failed: {e}");
            return false;
        }
    }

    /// <summary>
    /// Bound by parameter name to the target's allowMorse argument. Harmony resolves the
    /// name at patch time, so a mismatch surfaces as a caught patch failure rather than
    /// silently doing nothing.
    ///
    /// The first invocation is logged whatever the incoming value, because "no log line at
    /// all" previously left it ambiguous whether the detour runs or whether the game simply
    /// always passes false.
    /// </summary>
    private static void NoMorsePrefix(ref bool allowMorse)
    {
        _calls++;

        if (!_firstCallLogged)
        {
            _firstCallLogged = true;
            Plugin.Log.LogInfo(
                $"ModuleFilter: selector invoked (call #{_calls}), incoming allowMorse={allowMorse}. " +
                "The detour is intercepting module selection.");
        }

        if (!allowMorse) return;   // already excluded; nothing to do

        allowMorse = false;
        _forced++;
        if (_forced == 1)
        {
            Plugin.Log.LogInfo(
                $"ModuleFilter: game asked for Morse (call #{_calls}) and was overridden to false.");
        }
    }

    /// <summary>
    /// Read-only. Dumps the registry's module names so the log shows how the Morse entry is
    /// actually spelled, and confirms the registry is reachable at all. Retried by Poll()
    /// until the registry actually exists; only then is the attempt retired.
    /// </summary>
    private static void LogRegistryEntries()
    {
        try
        {
            Type type = Resolve(RegistryTypeNames);
            if (type == null)
            {
                Plugin.Log.LogWarning("ModuleFilter: ModuleRegistry type not found; cannot list modules.");
                _entriesLogged = true;
                return;
            }

            var names = new System.Collections.Generic.List<string>();
            bool sawAccessor = false;

            // Registry<TRegistry,TEntry> exposes BOTH "instance" and "Instance" as statics, and
            // only one of them holds the populated registry. Try both, plus both collection
            // accessors, and keep whichever yields the most names.
            foreach (string accessor in new[] { "instance", "Instance" })
            {
                PropertyInfo instanceProperty = FindProperty(type, accessor, isStatic: true);
                if (instanceProperty == null) continue;

                sawAccessor = true;

                object registry = null;
                try { registry = instanceProperty.GetValue(null); }
                catch (Exception e) { Plugin.Log.LogWarning($"ModuleFilter: reading {accessor} failed: {e.Message}"); }

                if (registry == null) continue;   // not constructed yet; Poll retries

                foreach (string member in new[] { "Data", "Entries" })
                {
                    PropertyInfo p = FindProperty(type, member, isStatic: false);
                    if (p == null) continue;

                    object entries = null;
                    try { entries = p.GetValue(registry); } catch { }
                    if (!(entries is IEnumerable enumerable)) continue;

                    var found = new System.Collections.Generic.List<string>();
                    foreach (object element in enumerable)
                    {
                        if (element == null) continue;
                        string name = element.GetType().GetProperty("Name")?.GetValue(element) as string;
                        found.Add(name ?? "?");
                    }

                    if (found.Count > names.Count) names = found;
                }
            }

            if (!sawAccessor)
            {
                Plugin.Log.LogWarning(
                    "ModuleFilter: no static instance/Instance property anywhere on the base chain " +
                    $"of {type.FullName}; cannot list modules.");
                _entriesLogged = true;
                return;
            }

            // An empty read only means the registry has not been populated yet. Keep retrying
            // instead of retiring with a useless "0 module(s)" line.
            if (names.Count == 0) return;

            Plugin.Log.LogInfo($"ModuleFilter: registry has {names.Count} module(s): {string.Join(", ", names)}");
            _entriesLogged = true;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"ModuleFilter: listing registry entries failed: {e.Message}");
        }
    }

    private static Type Resolve(string[] candidates)
    {
        foreach (string typeName in candidates)
        {
            try
            {
                Type type = TypeResolver.Default?.Resolve(typeName);
                if (type != null) return type;
            }
            catch { /* try the next name */ }
        }

        return null;
    }

    /// <summary>
    /// Registry&lt;TRegistry,TEntry&gt; declares its statics on the OPEN generic type, and .NET
    /// reflection does not return a static property inherited from a generic base class. So walk
    /// the chain and inspect each level's declared members instead of searching the derived type.
    /// This is what made the first registry listing attempt report "no Instance property".
    /// </summary>
    private static PropertyInfo FindProperty(Type type, string name, bool isStatic)
    {
        BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly |
                             (isStatic ? BindingFlags.Static : BindingFlags.Instance);

        for (Type t = type; t != null; t = t.BaseType)
        {
            PropertyInfo p = t.GetProperty(name, flags);
            if (p != null) return p;
        }

        return null;
    }
}
