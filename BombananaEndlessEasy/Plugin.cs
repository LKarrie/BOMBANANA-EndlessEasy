using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace BombananaEndlessEasy;

/// <summary>
/// BepInEx 6 IL2CPP entry point. Makes endless mode easier by tuning the game's
/// EndlessModeConfig: more time per wave and more tolerated mistakes.
/// </summary>
[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
// GUID verified by reading BOMBANANA.Library's metadata: the string "bombanana.library"
// sits right next to the [BepInPlugin] attribute. Hard dependency, because this plugin
// cannot function without the Library's API.
[BepInDependency("bombanana.library")]
public class Plugin : BasePlugin
{
    // Change PluginGuid to your own reverse-domain style id BEFORE publishing to Thunderstore.
    // GUIDs are permanent: other plugins reference them.
    public const string PluginGuid = "BOMBANANA.EndlessEasy";
    public const string PluginName = "Endless Easy";
    public const string PluginVersion = "1.6.0";

    // BasePlugin already exposes an instance "Log" property; the template deliberately
    // shadows it with a static so helper classes can log without holding the Plugin instance.
    internal static new ManualLogSource Log;

    internal static ConfigEntry<bool> Enabled;
    internal static ConfigEntry<bool> OnlyApplyAsHost;
    internal static ConfigEntry<float> TimeMultiplier;
    internal static ConfigEntry<int> MaxStartTimeSeconds;
    internal static ConfigEntry<bool> ScaleTimeBonuses;
    internal static ConfigEntry<int> HealthBonus;
    internal static ConfigEntry<bool> VerboseLogging;
    internal static ConfigEntry<bool> DisableMorse;
    internal static ConfigEntry<string> EnabledModules;

    public override void Load()
    {
        Log = base.Log;

        Enabled = Config.Bind(
            "General", "Enabled", true,
            "Master switch. When false the plugin does nothing.");

        OnlyApplyAsHost = Config.Bind(
            "General", "OnlyApplyAsHost", true,
            "Only the host needs this mod, so by default only the host rewrites the config. The " +
            "host's values are what everyone plays with: the bomb timer is pushed to clients via " +
            "Bomb.StartTickLoopClientRpc / BroadcastTimer, and Bomb.Health / MaxHealth / Outcome " +
            "are NetworkVariables. A client without (or skipping) the rewrite still shows the " +
            "vanilla number in the LOBBY panel, because that panel reads each client's own local " +
            "config -- but the in-mission countdown and strike count come from the host. Set this " +
            "to false to also rewrite clients' configs so their lobby panel matches.");

        TimeMultiplier = Config.Bind(
            "Time", "TimeMultiplier", 1.5f,
            new ConfigDescription(
                "Multiplier for the endless bomb timer. 1.5 = 50% more time (2:30 becomes 3:45). " +
                "1 disables this lever.",
                new AcceptableValueRange<float>(0.1f, 100f)));

        MaxStartTimeSeconds = Config.Bind(
            "Time", "MaxStartTimeSeconds", 0,
            new ConfigDescription(
                "Safety cap on the rewritten StartTimeSeconds, in seconds. 0 means no cap.",
                new AcceptableValueRange<int>(0, 3600)));

        ScaleTimeBonuses = Config.Bind(
            "Time", "ScaleTimeBonuses", true,
            "Also scale the per-difficulty time bonuses (EasyTimeBonus / MediumTimeBonus / " +
            "HardTimeBonus) by TimeMultiplier, so the multiplier holds for every difficulty.");

        HealthBonus = Config.Bind(
            "Difficulty", "HealthBonus", 2,
            new ConfigDescription(
                "Extra mistakes tolerated per endless wave, added to EndlessModeConfig." +
                "StrikesPerWave. 0 disables this lever.",
                new AcceptableValueRange<int>(0, 200)));

        VerboseLogging = Config.Bind(
            "Debug", "VerboseLogging", true,
            "Log the captured vanilla baseline, every value written back, and the lobby's " +
            "mission-start gate state. Turn off once you are happy.");

        DisableMorse = Config.Bind(
            "Modules", "DisableMorse", false,
            "Keep the Morse code module out of the puzzle pool. Uses the game's own allowMorse " +
            "selector flag, so no module data is edited. Applies wherever the game offers the " +
            "module, not only in endless mode.");

        EnabledModules = Config.Bind(
            "Modules", "EnabledModules", "",
            "Comma-separated list of the ONLY modules endless waves may use, for example 'Cable' " +
            "or 'Cable, Calculator'. Every other module the game picks for a wave is replaced " +
            "with one from this list. Leave EMPTY for the game's normal module pool (the default). " +
            "Each name is checked against the module registry at startup; unknown names are " +
            "reported and ignored, so a typo cannot break a wave. Module names look like " +
            "'Cable', 'Calculator', 'Direction'.");

        // Two patches, both installed by reflection so nothing here needs the game's interop
        // assemblies at compile time. Harmony is also kept for our own [HarmonyPatch] types.
        var harmony = new Harmony(PluginGuid);
        harmony.PatchAll(typeof(Plugin).Assembly);

        if (Enabled.Value)
        {
            try
            {
                GameHooks.Install(harmony);
            }
            catch (System.Exception e)
            {
                Log.LogError($"Failed to install game hooks: {e}");
            }

            try
            {
                ModuleFilter.Install(harmony);
            }
            catch (System.Exception e)
            {
                Log.LogError($"Failed to install module filter: {e}");
            }

            try
            {
                ModuleWhitelist.Install(harmony);
            }
            catch (System.Exception e)
            {
                Log.LogError($"Failed to install the module whitelist: {e}");
            }
        }

        Log.LogInfo($"{PluginName} {PluginVersion} loaded. Enabled={Enabled.Value}, " +
                    $"TimeMultiplier={TimeMultiplier.Value}, HealthBonus={HealthBonus.Value}.");
    }

    public override bool Unload() => true;
}
