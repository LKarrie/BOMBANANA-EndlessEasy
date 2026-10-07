using System;
using BOMBANANA.Library.Game;
using BOMBANANA.Library.Reflection;

namespace BombananaEndlessEasy;

/// <summary>
/// Endless-mode tuning driven entirely by BOMBANANA.Library's reflection API -- no Harmony
/// patch, and no compile-time dependency on the game's il2cpp interop assemblies.
///
/// Targets verified by decompiling the generated interop assemblies
/// (BepInEx\interop\Assembly-CSharp.dll):
///
///   class EndlessModeConfig : ScriptableObject          // global endless tuning object
///       static EndlessModeConfig Instance { get; }      // null until endless is set up
///       uint   StartTimeSeconds                         // "starting bomb timer in seconds"
///       ushort StrikesPerWave                           // mistakes tolerated per wave
///       int    EasyTimeBonus / MediumTimeBonus / HardTimeBonus
///       EndlessWaveTier[] WaveTiers; GetTierForWave(int)
///
/// Why NOT GameApi.SetMissionLength / SetMissionHealth (the first attempt):
///   EndlessMissionData overrides the virtual getters:
///       public override uint   GetLength()
///       public override ushort GetHealth()
///   so the base MissionData.Length / .Health fields are not what the game reads for an
///   endless wave. Writing them would have silently done nothing. Tuning EndlessModeConfig
///   instead feeds the values the overrides actually derive from.
///
/// Detection is free: EndlessModeConfig.Instance is non-null only once endless mode exists,
/// so no "am I in endless?" heuristic is needed at all.
///
/// All targets are computed from captured VANILLA baselines, never from the current value,
/// which makes compounding impossible even if the object is refreshed or reused.
/// </summary>
internal static class EndlessTuner
{
    private const int MissClearEveryNPolls = 20;

    private static readonly string[] ConfigTypeNames = { "EndlessModeConfig", "BombGame.EndlessModeConfig" };
    private static readonly string[] InstanceMemberNames = { "Instance", "cached" };

    private static int _missClears;
    private static bool? _lastConfigPresent;

    private static bool _baselinesCaptured;
    private static uint _baseStartTimeSeconds;
    private static ushort _baseStrikesPerWave;
    private static int _baseEasyTimeBonus;
    private static int _baseMediumTimeBonus;
    private static int _baseHardTimeBonus;

    /// <summary>
    /// Called from GameHooks' Update postfix. Do NOT reintroduce CoroutineApi here:
    /// CoroutineApi.Repeat returns normally but never ticks, because its Start() looks up
    /// StartCoroutine(IEnumerator) by exact signature and silently no-ops when that returns
    /// null. That failure mode produced a "heartbeat started" log with zero activity.
    /// </summary>
    internal static void Poll()
    {
        if (!Plugin.Enabled.Value) return;

        RefObj config = ResolveConfig();
        ReportConfigState(config != null);
        if (config == null) return;   // endless mode not set up (yet)

        if (Plugin.OnlyApplyAsHost.Value && !(NetworkApi.IsServer || NetworkApi.IsHost)) return;

        Apply(config);
    }

    /// <summary>
    /// Logs only on change. Silence here was the reason an earlier run looked like the mod
    /// was doing nothing: a failed resolve used to return without a word.
    /// </summary>
    private static void ReportConfigState(bool present)
    {
        if (_lastConfigPresent.HasValue && _lastConfigPresent.Value == present) return;
        _lastConfigPresent = present;
        Plugin.Log.LogInfo($"EndlessTuner: EndlessModeConfig.Instance present = {present}");
    }

    /// <summary>Shared with CableOnly, which needs the same config object.</summary>
    internal static RefObj ResolveConfig()
    {
        bool anyTypeFound = false;

        foreach (string typeName in ConfigTypeNames)
        {
            Type type = null;
            try { type = TypeResolver.Default?.Resolve(typeName); }
            catch (Exception e) { Plugin.Log.LogWarning($"EndlessTuner: resolving {typeName} failed: {e.Message}"); }
            if (type == null) continue;

            anyTypeFound = true;
            var refType = new RefType(type);
            foreach (string member in InstanceMemberNames)
            {
                object instance = null;
                try { instance = refType.GetStaticProperty(member); }
                catch { try { instance = refType.GetStaticField(member); } catch { } }
                if (instance == null) continue;

                return new RefObj(instance);
            }
        }

        // TypeResolver caches misses permanently, so if the game assembly was not visible on
        // an early poll the failure would stick forever. Clear the cache occasionally so a
        // later poll can retry.
        if (!anyTypeFound && ++_missClears % MissClearEveryNPolls == 0)
        {
            try { TypeResolver.Default?.Clear(); }
            catch (Exception e) { Plugin.Log.LogWarning($"EndlessTuner: resolver clear failed: {e.Message}"); }
        }

        return null;
    }

    private static void Apply(RefObj config)
    {
        if (!TryReadUInt(config, "StartTimeSeconds", out uint curStart)) return;
        TryReadUShort(config, "StrikesPerWave", out ushort curStrikes);
        TryReadInt(config, "EasyTimeBonus", out int curEasy);
        TryReadInt(config, "MediumTimeBonus", out int curMedium);
        TryReadInt(config, "HardTimeBonus", out int curHard);

        if (!_baselinesCaptured)
        {
            _baseStartTimeSeconds = curStart;
            _baseStrikesPerWave = curStrikes;
            _baseEasyTimeBonus = curEasy;
            _baseMediumTimeBonus = curMedium;
            _baseHardTimeBonus = curHard;
            _baselinesCaptured = true;

            Plugin.Log.LogInfo(
                "EndlessTuner: vanilla endless baseline captured -- " +
                $"StartTimeSeconds={_baseStartTimeSeconds}, StrikesPerWave={_baseStrikesPerWave}, " +
                $"timeBonus Easy/Medium/Hard={_baseEasyTimeBonus}/{_baseMediumTimeBonus}/{_baseHardTimeBonus}");
        }

        float mult = Plugin.TimeMultiplier.Value;

        uint targetStart = ScaleToUInt(_baseStartTimeSeconds, mult, Plugin.MaxStartTimeSeconds.Value);
        if (curStart != targetStart)
        {
            if (TryWrite(config, "StartTimeSeconds", targetStart))
                LogVerbose($"EndlessTuner: StartTimeSeconds {curStart} -> {targetStart} (x{mult}).");
        }

        if (Plugin.ScaleTimeBonuses.Value)
        {
            WriteScaledInt(config, "EasyTimeBonus", _baseEasyTimeBonus, curEasy, mult);
            WriteScaledInt(config, "MediumTimeBonus", _baseMediumTimeBonus, curMedium, mult);
            WriteScaledInt(config, "HardTimeBonus", _baseHardTimeBonus, curHard, mult);
        }

        int bonus = Plugin.HealthBonus.Value;
        if (bonus != 0)
        {
            int target = _baseStrikesPerWave + bonus;
            if (target < 1) target = 1;
            if (target > ushort.MaxValue) target = ushort.MaxValue;

            if (curStrikes != target && TryWrite(config, "StrikesPerWave", (ushort)target))
                LogVerbose($"EndlessTuner: StrikesPerWave {curStrikes} -> {target}.");
        }
    }

    private static void WriteScaledInt(RefObj config, string name, int baseline, int current, float mult)
    {
        int target = ScaleToInt(baseline, mult);
        if (current == target) return;
        if (TryWrite(config, name, target))
            LogVerbose($"EndlessTuner: {name} {current} -> {target} (x{mult}).");
    }

    private static void LogVerbose(string message)
    {
        if (Plugin.VerboseLogging.Value) Plugin.Log.LogInfo(message);
    }

    private static uint ScaleToUInt(uint baseline, float mult, int cap)
    {
        double scaled = Math.Round(baseline * (double)mult);
        if (scaled < 1.0) scaled = 1.0;
        if (cap > 0 && scaled > cap) scaled = cap;
        return (uint)scaled;
    }

    private static int ScaleToInt(int baseline, float mult)
    {
        double scaled = Math.Round(baseline * (double)mult);
        if (baseline > 0 && scaled < 1.0) scaled = 1.0;
        if (baseline < 0 && scaled > -1.0) scaled = -1.0;
        return (int)scaled;
    }

    private static bool TryReadUInt(RefObj config, string name, out uint value)
    {
        value = 0;
        object raw = TryRead(config, name);
        if (raw == null) return false;
        try { value = Convert.ToUInt32(raw); return true; }
        catch (Exception e) { Plugin.Log.LogWarning($"EndlessTuner: {name} not convertible: {e.Message}"); return false; }
    }

    private static void TryReadUShort(RefObj config, string name, out ushort value)
    {
        value = 0;
        object raw = TryRead(config, name);
        if (raw == null) return;
        try { value = Convert.ToUInt16(raw); } catch { }
    }

    private static void TryReadInt(RefObj config, string name, out int value)
    {
        value = 0;
        object raw = TryRead(config, name);
        if (raw == null) return;
        try { value = Convert.ToInt32(raw); } catch { }
    }

    private static object TryRead(RefObj config, string name)
    {
        try { return config.GetProperty(name); }   // property-first, field fallback
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"EndlessTuner: reading {name} failed: {e.Message}");
            return null;
        }
    }

    private static bool TryWrite(RefObj config, string name, object value)
    {
        try
        {
            config.SetProperty(name, value);       // property-first, field fallback
            return true;
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"EndlessTuner: writing {name} failed: {e.Message}");
            return false;
        }
    }
}
