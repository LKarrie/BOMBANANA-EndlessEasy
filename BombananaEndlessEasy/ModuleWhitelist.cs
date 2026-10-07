using System;
using System.Collections.Generic;
using System.Reflection;
using BOMBANANA.Library.Reflection;
using HarmonyLib;

namespace BombananaEndlessEasy;

/// <summary>
/// Optionally restricts endless waves to a caller-supplied whitelist of module types.
///
/// BOMBANANA exposes no "allowed modules" setting anywhere -- EndlessModeConfig's complete
/// member list is StartTimeSeconds, StrikesPerWave, CoverAnimDuration, the three time bonuses,
/// WaveTiers and two avoid-repeat booleans. So this works at the pick point instead.
///
/// The endless wave builder selects modules by NAME (verified live: the patch fired and the
/// game handed back "Calculator"):
///
///   bool EndlessMissionData.TryPickPuzzleModule(Difficulty, HashSet&lt;string&gt; used,
///                                               Random, bool avoidRecent, out string moduleName)
///   bool EndlessMissionData.TryPickAllowedPuzzleModule(HashSet&lt;string&gt; used,
///                                               Random, bool avoidRecent, out string moduleName)
///
/// out string is a plain managed System.String, so the postfix simply hands back a different
/// name, drawn at random from the whitelist. No compile-time reference to the game's interop
/// assemblies is needed, which keeps the mod working across game updates.
///
/// A whitelist (rather than a blacklist) is deliberate: replacing a pick requires a pool of
/// names to replace it WITH, and the whitelist is exactly that pool.
///
/// Every configured name is verified against the registry's own lookup before use:
///
///   static ModuleRegistry.Module ModuleRegistry.Get(string name)
///
/// Unknown names are reported and dropped, so a typo cannot turn a wave into names the game
/// cannot spawn. If nothing survives validation, no rewriting happens at all.
///
/// Empty by default: the game's normal module pool is used and this class only observes.
/// </summary>
internal static class ModuleWhitelist
{
    private static readonly string[] MissionDataTypeNames = { "EndlessMissionData", "BombGame.EndlessMissionData" };
    private static readonly string[] RegistryTypeNames = { "ModuleRegistry", "BombGame.ModuleRegistry" };
    private static readonly string[] PickMethodNames = { "TryPickPuzzleModule", "TryPickAllowedPuzzleModule" };

    /// <summary>
    /// The module names this build of BOMBANANA ships, read out of the game's own
    /// ModuleRegistry asset (BOMBANANA_Data/resources.assets). Used only to REPORT which names
    /// exist, never to pick one silently: every entry below is validated against
    /// ModuleRegistry.Get before it is mentioned, so a wrong or removed name shows up as
    /// "not found in this build" rather than being taken on faith.
    /// </summary>
    private static readonly string[] KnownModuleNames =
    {
        "Cable", "Calculator", "Direction", "ColorSlider", "Symbol", "Piano", "Switch",
        "MonkeySays", "Morse", "Soundboard", "Maze", "Pressure", "Slider", "Alarm",
    };

    private static readonly List<string> Allowed = new List<string>();
    private static readonly Random Rng = new Random();

    private static bool _validated;
    private static bool _togglesApplied;
    private static bool _firstCallLogged;
    private static int _pickCalls;
    private static int _replaced;
    private static int _passed;

    internal static void Install(Harmony harmony)
    {
        if (string.IsNullOrWhiteSpace(Plugin.EnabledModules.Value))
        {
            Plugin.Log.LogInfo(
                "ModuleWhitelist: EnabledModules is empty; endless waves use the game's normal " +
                "module pool. Available names: " + string.Join(", ", KnownModuleNames) + ".");
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
                "ModuleWhitelist: could not patch any module picker, so the whitelist will not " +
                "take effect. Tried: " + string.Join(", ", PickMethodNames));
        }

        Poll();
    }

    /// <summary>Retried from the heartbeat until the names can be validated.</summary>
    internal static void Poll()
    {
        if (_validated) return;
        if (string.IsNullOrWhiteSpace(Plugin.EnabledModules.Value)) return;
        if (!TryValidate()) return;

        _validated = true;

        if (Allowed.Count == 0) return;   // nothing valid: fail safe, no rewriting

        ApplyRepeatToggles();
        Plugin.Log.LogInfo(
            $"ModuleWhitelist: active with {Allowed.Count} module(s): {string.Join(", ", Allowed)}. " +
            "Every other module the game picks for an endless wave is replaced.");
    }

    private static bool PatchPicker(Harmony harmony, string methodName)
    {
        Type type = Resolve(MissionDataTypeNames);
        if (type == null)
        {
            Plugin.Log.LogError($"ModuleWhitelist: EndlessMissionData not found for {methodName}.");
            return false;
        }

        MethodInfo target = type.GetMethod(
            methodName,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);

        if (target == null)
        {
            Plugin.Log.LogError($"ModuleWhitelist: EndlessMissionData.{methodName} not found.");
            return false;
        }

        MethodInfo postfix = typeof(ModuleWhitelist).GetMethod(
            nameof(RestrictPostfix), BindingFlags.Static | BindingFlags.NonPublic);

        try
        {
            harmony.Patch(target, postfix: new HarmonyMethod(postfix));
            Plugin.Log.LogInfo($"ModuleWhitelist: patched EndlessMissionData.{methodName}.");
            return true;
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"ModuleWhitelist: patching {methodName} failed: {e}");
            return false;
        }
    }

    /// <summary>
    /// Bound by parameter name to the picker's out moduleName. The first call is always logged
    /// so the log distinguishes "patch never runs" from "patch runs but nothing needed changing".
    /// </summary>
    private static void RestrictPostfix(ref string moduleName)
    {
        _pickCalls++;

        if (!_firstCallLogged)
        {
            _firstCallLogged = true;
            Plugin.Log.LogInfo(
                $"ModuleWhitelist: module picker invoked (call #{_pickCalls}), game chose " +
                $"'{moduleName}'.");
        }

        if (Allowed.Count == 0) return;   // feature off, or nothing validated

        if (IsAllowed(moduleName))
        {
            _passed++;
            return;
        }

        string previous = moduleName;
        moduleName = Allowed[Rng.Next(Allowed.Count)];
        _replaced++;

        if (_replaced <= 5)
        {
            Plugin.Log.LogInfo(
                $"ModuleWhitelist: '{previous}' -> '{moduleName}' (replacement #{_replaced}).");
        }
    }

    private static bool IsAllowed(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;

        for (int i = 0; i < Allowed.Count; i++)
        {
            if (string.Equals(Allowed[i], name, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>
    /// Splits the configured list and keeps only names the registry actually knows. Returns
    /// false while the registry is not readable yet, so Poll() retries.
    /// </summary>
    private static bool TryValidate()
    {
        Type registryType = Resolve(RegistryTypeNames);
        if (registryType == null) return false;

        MethodInfo get = registryType.GetMethod(
            "Get", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);

        if (get == null)
        {
            Plugin.Log.LogError(
                "ModuleWhitelist: ModuleRegistry.Get(string) not found, so no configured name can " +
                "be verified. No rewriting will happen.");
            return true;   // definitive: stop retrying, Allowed stays empty
        }

        LogAvailableModules(get, registryType);

        var valid = new List<string>();
        var invalid = new List<string>();
        var chaos = new List<string>();

        foreach (string raw in Plugin.EnabledModules.Value.Split(new[] { ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (TryResolveName(get, registryType, raw, out string actual, out bool isChaos))
            {
                if (!ContainsIgnoreCase(valid, actual)) valid.Add(actual);
                if (isChaos && !ContainsIgnoreCase(chaos, actual)) chaos.Add(actual);
            }
            else
            {
                invalid.Add(raw);
            }
        }

        if (invalid.Count > 0)
        {
            Plugin.Log.LogWarning(
                "ModuleWhitelist: these names are not in the module registry and were ignored: " +
                string.Join(", ", invalid) + ". Valid names are: " + string.Join(", ", KnownModuleNames) + ".");
        }

        if (valid.Count == 0)
        {
            Plugin.Log.LogError(
                "ModuleWhitelist: no configured module name was valid, so no rewriting will happen " +
                "and endless waves keep the game's normal module mix.");
            return true;
        }

        if (chaos.Count > 0)
        {
            // Reported, not refused: the user asked for these by name, so honour it.
            Plugin.Log.LogInfo(
                "ModuleWhitelist: the game classifies these as chaos modules, they are still used " +
                "because you listed them: " + string.Join(", ", chaos));
        }

        Allowed.Clear();
        Allowed.AddRange(valid);
        return true;
    }

    /// <summary>
    /// Reports which of the module names known to ship with BOMBANANA actually resolve in this
    /// build, by asking the registry rather than trusting the list. A name the game no longer
    /// has shows up as "not found" instead of silently failing later.
    /// </summary>
    private static void LogAvailableModules(MethodInfo get, Type registryType)
    {
        var present = new List<string>();
        var missing = new List<string>();

        foreach (string name in KnownModuleNames)
        {
            if (TryResolveName(get, registryType, name, out string actual, out _)) present.Add(actual);
            else missing.Add(name);
        }

        Plugin.Log.LogInfo(
            $"ModuleWhitelist: {present.Count}/{KnownModuleNames.Length} known module names resolve " +
            "in this build: " + string.Join(", ", present));

        if (missing.Count > 0)
        {
            Plugin.Log.LogWarning(
                "ModuleWhitelist: listed in the mod but NOT present in this build: " +
                string.Join(", ", missing));
        }
    }

    /// <summary>True when the registry returns a real module for this name.</summary>
    private static bool TryResolveName(MethodInfo get, Type registryType, string name, out string actualName, out bool isChaos)
    {
        actualName = null;
        isChaos = false;

        try
        {
            object module = get.Invoke(null, new object[] { name });
            if (module == null) return false;

            string resolved = module.GetType().GetProperty("Name")?.GetValue(module) as string;
            if (string.IsNullOrEmpty(resolved)) return false;

            actualName = resolved;
            isChaos = IsChaos(registryType, resolved);
            return true;
        }
        catch
        {
            // Get() throws for unknown names; that just means "not a valid entry".
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

            return isChaos.Invoke(null, new object[] { name }) is bool flag && flag;
        }
        catch
        {
            return false;
        }
    }

    private static bool ContainsIgnoreCase(List<string> list, string value)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i], value, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>
    /// A wave built from a short whitelist needs the repeat guard off, otherwise the second
    /// pick of the same name is refused and the wave comes out short.
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
                "ModuleWhitelist: AvoidRepeatNormalModule/AvoidRepeatChaosModule set to false so a " +
                "small whitelist can fill a whole wave.");
        }
        catch (Exception e)
        {
            _togglesApplied = true;   // do not spam
            Plugin.Log.LogWarning($"ModuleWhitelist: could not clear the repeat guards: {e.Message}");
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
