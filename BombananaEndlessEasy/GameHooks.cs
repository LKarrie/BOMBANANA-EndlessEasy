using System;
using System.Reflection;
using BOMBANANA.Library.Reflection;
using HarmonyLib;

namespace BombananaEndlessEasy;

/// <summary>
/// Gives the plugin a guaranteed main-thread heartbeat by postfixing an Update method the
/// game already calls every frame.
///
/// Why this is necessary: BOMBANANA.Library's CoroutineApi does NOT actually start
/// anything on this build. CoroutineApi.Start resolves the runner method with
///     GetMethod("StartCoroutine", new[] { typeof(System.Collections.IEnumerator) })
/// and its implementation then does `if (method != null) { ... }` -- when the lookup
/// returns null it silently does nothing at all, while Repeat() has already returned
/// normally. The plugin therefore logged "heartbeat started" and never ticked once.
///
/// Patching is done through reflection on the resolved type rather than with a compiled
/// [HarmonyPatch] type, so the mod keeps needing no compile-time reference to the game's
/// interop assemblies.
/// </summary>
internal static class GameHooks
{
    // Fast enough to feel immediate, slow enough to be free. Update runs per frame.
    private const int MinIntervalMs = 400;

    /// <summary>Candidate types whose Update is worth piggybacking on.</summary>
    private static readonly string[] CandidateTypes =
    {
        "Bombanana.UI.NewLobbyUI",   // lobby screen: where endless is configured and started
        "MissionHandler",            // mission / wave progression
        "GameManager",               // top-level state changes
    };

    private static int _lastPollTicks = int.MinValue;
    private static int _installed;

    internal static void Install(Harmony harmony)
    {
        foreach (string typeName in CandidateTypes)
        {
            if (TryHookUpdate(harmony, typeName)) _installed++;
        }

        if (_installed == 0)
        {
            Plugin.Log.LogError(
                "GameHooks: could not patch any Update method; the plugin has no heartbeat and " +
                "will not act. Candidates tried: " + string.Join(", ", CandidateTypes));
        }
        else
        {
            Plugin.Log.LogInfo($"GameHooks: heartbeat installed on {_installed} Update method(s).");
        }
    }

    private static bool TryHookUpdate(Harmony harmony, string typeName)
    {
        Type type = null;
        try { type = TypeResolver.Default?.Resolve(typeName); }
        catch (Exception e) { Plugin.Log.LogWarning($"GameHooks: resolving {typeName} failed: {e.Message}"); }

        if (type == null)
        {
            Plugin.Log.LogWarning($"GameHooks: type '{typeName}' not found.");
            return false;
        }

        MethodInfo update = type.GetMethod(
            "Update",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            null, Type.EmptyTypes, null);

        if (update == null)
        {
            Plugin.Log.LogWarning($"GameHooks: '{typeName}.Update()' not found.");
            return false;
        }

        MethodInfo postfix = typeof(GameHooks).GetMethod(
            nameof(OnUpdate), BindingFlags.Static | BindingFlags.NonPublic);

        try
        {
            harmony.Patch(update, postfix: new HarmonyMethod(postfix));
            Plugin.Log.LogInfo($"GameHooks: patched {type.FullName}.Update().");
            return true;
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"GameHooks: patching {type.FullName}.Update() failed: {e}");
            return false;
        }
    }

    /// <summary>Runs after every patched Update. Throttled, and never allowed to throw.</summary>
    private static void OnUpdate()
    {
        int now = Environment.TickCount;
        if (_lastPollTicks != int.MinValue && unchecked(now - _lastPollTicks) < MinIntervalMs) return;
        _lastPollTicks = now;

        try { EndlessTuner.Poll(); }
        catch (Exception e) { Plugin.Log.LogError($"EndlessTuner poll failed: {e}"); }

        try { LobbyDiagnostics.Poll(); }
        catch (Exception e) { Plugin.Log.LogError($"LobbyDiagnostics poll failed: {e}"); }

        try { ModuleFilter.Poll(); }
        catch (Exception e) { Plugin.Log.LogError($"ModuleFilter poll failed: {e}"); }

        try { ModuleWhitelist.Poll(); }
        catch (Exception e) { Plugin.Log.LogError($"ModuleWhitelist poll failed: {e}"); }
    }
}
