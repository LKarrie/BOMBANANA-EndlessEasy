using System;
using BOMBANANA.Library.Reflection;

namespace BombananaEndlessEasy;

/// <summary>
/// Read-only diagnostics for the lobby's mission-start gates.
///
/// History, kept because it explains why this class changes nothing: it began as a "start
/// endless solo" aid that wrote LobbyHandler.MinPlayerCount. Testing killed that idea. The
/// game re-pins MinPlayerCount and MaxPlayerCount to 3 every frame, and
/// AreLobbyRolesValidForStart() returns false with one player, because BOMBANANA! is a
/// three-player game in every mode. The override was removed rather than shipped as a knob
/// that does not work; what is left is the logging that proved it, so a player who cannot
/// start a mission can see which gate is closed instead of guessing.
///
/// Members verified against BepInEx\interop\Assembly-CSharp.dll:
///   class LobbyHandler : Unity.Netcode.NetworkBehaviour
///       static LobbyHandler Handler
///       int  MinPlayerCount { get; set; }
///       int  MaxPlayerCount { get; set; }
///       bool IsMissionStartLocked
///       bool AreLobbyRolesValidForStart()
///       bool CanLobbyBeStarted()
///       bool CanOfferLobbyStart()
///
/// Resolves "LobbyHandler" directly instead of using BOMBANANA.Library's LobbyHandlerApi:
/// that helper asks TypeResolver for "BombGame.LobbyHandler", but this build's interop types
/// carry no namespace and TypeResolver.Default has no external fallback, so the string
/// resolves to null. Every Library API with a hard-coded "BombGame." prefix is unusable here.
/// </summary>
internal static class LobbyDiagnostics
{
    private static readonly string[] LobbyTypeNames = { "LobbyHandler", "BombGame.LobbyHandler" };
    private static readonly string[] HandlerMemberNames = { "Handler" };

    private static bool _typeFound;
    private static string _resolveState;
    private static string _lastGateReport;

    internal static void Poll()
    {
        if (!Plugin.VerboseLogging.Value) return;

        RefObj lobby = ResolveLobby();

        ReportResolveState(lobby != null
            ? "lobby-found"
            : (_typeFound ? "type-ok-but-no-lobby-instance" : "type-not-found"));

        if (lobby == null)
        {
            _lastGateReport = null;
            return;
        }

        ReportStartGates(lobby);
    }

    private static void ReportResolveState(string state)
    {
        if (state == _resolveState) return;
        _resolveState = state;
        Plugin.Log.LogInfo($"LobbyDiagnostics: lobby resolve state = {state}");
    }

    private static void ReportStartGates(RefObj lobby)
    {
        int min = ReadInt(lobby, "MinPlayerCount");
        int max = ReadInt(lobby, "MaxPlayerCount");
        string rolesValid = CallBool(lobby, "AreLobbyRolesValidForStart");
        string canStart = CallBool(lobby, "CanLobbyBeStarted");

        string report =
            $"min={min} max={max} locked={ReadBool(lobby, "IsMissionStartLocked")} " +
            $"rolesValid={rolesValid} canStart={canStart} " +
            $"canOffer={CallBool(lobby, "CanOfferLobbyStart")}";

        if (report == _lastGateReport) return;   // only log on change
        _lastGateReport = report;

        Plugin.Log.LogInfo("LobbyDiagnostics: " + report);

        // BOMBANANA! is a three-player game in every mode, so this is the usual reason a lone
        // player cannot start. Say it plainly rather than leaving them to interpret booleans.
        if (min > 1 && rolesValid == "False")
        {
            Plugin.Log.LogInfo(
                $"LobbyDiagnostics: this lobby needs {min} players. BOMBANANA! requires three " +
                "players in every mode, which no mod setting can waive -- the start gate checks " +
                "the lobby roles themselves.");
        }
    }

    private static RefObj ResolveLobby()
    {
        _typeFound = false;

        foreach (string typeName in LobbyTypeNames)
        {
            Type type = null;
            try { type = TypeResolver.Default?.Resolve(typeName); }
            catch (Exception e) { Plugin.Log.LogWarning($"LobbyDiagnostics: resolving {typeName} failed: {e.Message}"); }
            if (type == null) continue;

            _typeFound = true;
            var refType = new RefType(type);

            foreach (string member in HandlerMemberNames)
            {
                object instance = null;
                try { instance = refType.GetStaticProperty(member); }
                catch { /* not a static property */ }
                if (instance == null)
                {
                    try { instance = refType.GetStaticField(member); }
                    catch { /* not a static field */ }
                }
                if (instance == null)
                {
                    try { instance = refType.Singleton(member)?.Instance; }
                    catch { /* absent */ }
                }

                if (instance != null) return new RefObj(instance);
            }
        }

        return null;
    }

    private static int ReadInt(RefObj target, string name)
    {
        try
        {
            object raw = target.GetProperty(name);
            return raw == null ? -1 : Convert.ToInt32(raw);
        }
        catch { return -1; }
    }

    private static bool ReadBool(RefObj target, string name)
    {
        try
        {
            object raw = target.GetProperty(name);
            return raw != null && Convert.ToBoolean(raw);
        }
        catch { return false; }
    }

    private static string CallBool(RefObj target, string name)
    {
        try
        {
            object raw = target.Call(name);
            return raw == null ? "null" : Convert.ToBoolean(raw).ToString();
        }
        catch (Exception e) { return "err:" + e.GetType().Name; }
    }
}
