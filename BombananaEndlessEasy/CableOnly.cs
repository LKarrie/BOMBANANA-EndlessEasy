using System;
using System.Collections.Generic;
using System.Reflection;
using BOMBANANA.Library.Reflection;
using HarmonyLib;

namespace BombananaEndlessEasy;

/// <summary>
/// Restricts endless waves to a single module type (the wire/cable module by default).
///
/// BOMBANANA exposes no "allowed modules" setting anywhere -- EndlessModeConfig's complete
/// member list is StartTimeSeconds, StrikesPerWave, CoverAnimDuration, the three time bonuses,
/// WaveTiers, AvoidRepeatNormalModule and AvoidRepeatChaosModule. So this works at the pick
/// point instead.
///
/// The endless wave builder selects modules by NAME, verified live: the patch on
/// TryPickPuzzleModule fired and the game handed back "Calculator".
///
///   bool EndlessMissionData.TryPickPuzzleModule(Difficulty, HashSet&lt;string&gt; used,
///                                               Random, bool avoidRecent, out string moduleName)
///   bool EndlessMissionData.TryPickAllowedPuzzleModule(HashSet&lt;string&gt; used,
///                                               Random, bool avoidRecent, out string moduleName)
///
/// out string is a plain managed System.String, so the postfix just hands back a different
/// name -- no compile-time reference to the game's interop assemblies is needed.
///
/// Name resolution avoids the module registry's data list on purpose. Registry&lt;TRegistry,TEntry&gt;
/// declares its statics on the OPEN generic type, and .NET reflection cannot read a static
/// property inherited from a generic base class, which is exactly how the first attempt failed.
/// Instead each candidate name is verified with the registry's own static lookup:
///
///   static ModuleRegistry.Module ModuleRegistry.Get(string name)
///   static bool ModuleRegistry.IsChaosModule(string name)
///
/// A name only counts once Get() hands back a Module with a non-empty Name, so a wrong guess
/// can never turn the whole bomb into unspawnable modules.
/// </summary>
internal static class CableOnly
{
    private static readonly string[] MissionDataTypeNames = { "EndlessMissionData", "BombGame.EndlessMissionData" };
    private static readonly string[] RegistryTypeNames = { "ModuleRegistry", "BombGame.ModuleRegistry" };
    private static readonly string[] PickMethodNames = { "TryPickPuzzleModule", "TryPickAllowedPuzzleModule" };

    /// <summary>Probed in order, after any name pinned in the config.</summary>
    private static readonly string[] CandidateNames =
    {
        "Cable", "Cables", "CableModule", "Wires", "Wire", "WireModule",
    };

    private static string _cableName;
    private static bool _resolveReported;
    private static bool _togglesApplied;
    private static bool _firstCallLogged;
    private static int _pickCalls;
    private static int _forced;

    internal static void Install(Harmony harmony)
    {
        if (!Plugin.ForceCableOnly.Value)
        {
            Plugin.Log.LogInfo("CableOnly: ForceCableOnly is off; modules are left as the game picks them.");
            return;
        }

        int patched = 0;
        foreach (string methodName in PickMethodNames)
        {
            if (PatchPicker(harmony, methodName)) patched++;
        }

        if (patched == 0)
        {
            Plugin.Log.LogError(
                "CableOnly: could not patch any module picker; waves will keep their normal module " +
                "mix. Tried: " + string.Join(", ", PickMethodNames));
        }

        Poll();
    }

    /// <summary>Retried from the heartbeat until a name validates.</summary>
    internal static void Poll()
    {
        if (!Plugin.ForceCableOnly.Value) return;

        if (_cableName == null) ResolveCableName();
        if (_cableName != null) ApplyRepeatToggles();
    }

    private static bool PatchPicker(Harmony harmony, string methodName)
    {
        Type type = Resolve(MissionDataTypeNames);
        if (type == null)
        {
            Plugin.Log.LogError($"CableOnly: EndlessMissionData not found for {methodName}.");
            return false;
        }

        MethodInfo target = type.GetMethod(
            methodName,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);

        if (target == null)
        {
            Plugin.Log.LogError($"CableOnly: EndlessMissionData.{methodName} not found.");
            return false;
        }

        MethodInfo postfix = typeof(CableOnly).GetMethod(
            nameof(ForceCablePostfix), BindingFlags.Static | BindingFlags.NonPublic);

        try
        {
            harmony.Patch(target, postfix: new HarmonyMethod(postfix));
            Plugin.Log.LogInfo($"CableOnly: patched EndlessMissionData.{methodName}.");
            return true;
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"CableOnly: patching {methodName} failed: {e}");
            return false;
        }
    }

    /// <summary>
    /// Bound by parameter name to the picker's out moduleName. The first call is always logged
    /// so the log distinguishes "patch never runs" from "patch runs but nothing needed changing".
    /// </summary>
    private static void ForceCablePostfix(ref string moduleName)
    {
        _pickCalls++;

        if (!_firstCallLogged)
        {
            _firstCallLogged = true;
            Plugin.Log.LogInfo(
                $"CableOnly: module picker invoked (call #{_pickCalls}), game chose '{moduleName}'.");
        }

        if (_cableName == null) return;   // unresolved: fail safe, change nothing
        if (string.Equals(moduleName, _cableName, StringComparison.OrdinalIgnoreCase)) return;

        string previous = moduleName;
        moduleName = _cableName;
        _forced++;

        if (_forced <= 5)
        {
            Plugin.Log.LogInfo($"CableOnly: '{previous}' -> '{_cableName}' (replacement #{_forced}).");
        }
    }

    /// <summary>
    /// Each candidate is validated against the registry's own static Get(), so only a name the
    /// game can actually spawn is ever used.
    /// </summary>
    private static void ResolveCableName()
    {
        Type registryType = Resolve(RegistryTypeNames);
        if (registryType == null) return;

        MethodInfo get = registryType.GetMethod(
            "Get", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);

        if (get == null)
        {
            if (!_resolveReported)
            {
                _resolveReported = true;
                Plugin.Log.LogError(
                    "CableOnly: ModuleRegistry.Get(string) not found, so no module name can be " +
                    "verified. Nothing is being rewritten.");
            }
            return;
        }

        var tried = new List<string>();

        string configured = Plugin.CableModuleName.Value;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            string pinned = configured.Trim();
            if (Validate(get, registryType, pinned, out string actualPinned))
            {
                ReportResolved(actualPinned, "the CableModuleName setting");
                return;
            }

            tried.Add(pinned);
            Plugin.Log.LogWarning(
                $"CableOnly: CableModuleName is set to '{pinned}' but the registry does not know " +
                "that module; falling back to auto-detection.");
        }

        foreach (string candidate in CandidateNames)
        {
            tried.Add(candidate);
            if (!Validate(get, registryType, candidate, out string actual)) continue;

            ReportResolved(actual, "auto-detection");
            return;
        }

        if (_resolveReported) return;
        _resolveReported = true;
        Plugin.Log.LogError(
            "CableOnly: could not verify any cable module name, so nothing is being rewritten and " +
            "waves keep their normal module mix. Tried: " + string.Join(", ", tried) +
            ". Set CableModuleName to the exact registry name (see the ModuleFilter registry list " +
            "in this log).");
    }

    /// <summary>True when the registry returns a real, non-chaos module for this name.</summary>
    private static bool Validate(MethodInfo get, Type registryType, string name, out string actualName)
    {
        actualName = null;

        try
        {
            object module = get.Invoke(null, new object[] { name });
            if (module == null) return false;

            PropertyInfo nameProperty = module.GetType().GetProperty("Name");
            string resolved = nameProperty?.GetValue(module) as string;
            if (string.IsNullOrEmpty(resolved)) return false;

            if (IsChaos(registryType, resolved))
            {
                Plugin.Log.LogWarning($"CableOnly: '{resolved}' is a chaos module; ignoring it.");
                return false;
            }

            actualName = resolved;
            return true;
        }
        catch
        {
            // Get() throws for unknown names; that just means "try the next candidate".
            return false;
        }
    }

    private static bool IsChaos(Type registryType, string name)
    {
        try
        {
            MethodInfo isChaos = registryType.GetMethod(
                "IsChaosModule", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
            if (isChaos == null) return false;

            object result = isChaos.Invoke(null, new object[] { name });
            return result is bool flag && flag;
        }
        catch
        {
            return false;
        }
    }

    private static void ReportResolved(string name, string source)
    {
        _cableName = name;   // the postfix reads this; without it every rewrite is skipped
        if (_resolveReported) return;
        _resolveReported = true;
        Plugin.Log.LogInfo($"CableOnly: cable module verified via {source} as '{name}'.");
    }

    /// <summary>
    /// One type repeated across a wave needs the repeat guard off. Written straight to the same
    /// config object the rest of the mod uses.
    /// </summary>
    private static void ApplyRepeatToggles()
    {
        if (_togglesApplied) return;

        RefObj config = EndlessTuner.ResolveConfig();
        if (config == null) return;

        try
        {
            config.SetProperty("AvoidRepeatNormalModule", false);
            config.SetProperty("AvoidRepeatChaosModule", false);
            _togglesApplied = true;
            Plugin.Log.LogInfo(
                "CableOnly: AvoidRepeatNormalModule/AvoidRepeatChaosModule set to false so one " +
                "module type can fill a whole wave.");
        }
        catch (Exception e)
        {
            _togglesApplied = true;   // do not spam
            Plugin.Log.LogWarning($"CableOnly: could not clear the repeat guards: {e.Message}");
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
}
