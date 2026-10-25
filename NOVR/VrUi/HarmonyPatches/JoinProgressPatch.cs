using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace NOVR.VrUi.HarmonyPatches;

// The game shows how joining a server goes, and why it failed, only on JoinLobbyOverlay: its own screen-space canvas,
// which XR never draws to the headset, so in VR a join looked like it did nothing. Its Open and Close are watched here
// and the multiplayer screen (MultiplayerScreen) shows the same. Found by reflection and patched on its own instead of
// through CreateAndPatchAll, so a renamed game method only turns this off.
internal static class JoinProgressPatch
{
    private const string OverlayTypeName = "NuclearOption.Networking.Lobbies.JoinLobbyOverlay";
    private const BindingFlags AnyStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    // Stopping a connection makes the game report "Local client stopped"; after Cancel that isn't news.
    private const float CancelEchoSeconds = 3f;

    private static MethodInfo? _close;
    private static MethodInfo? _cancel;
    private static FieldInfo? _instance;
    private static float _cancelledAt = float.NegativeInfinity;
    private static bool _loggedFailure;

    // Goes up whenever what the overlay shows changes.
    public static int Version { get; private set; }

    // Whether the game's overlay is up, and whether it is still joining (else it says why joining failed).
    public static bool Showing { get; private set; }
    public static bool Joining { get; private set; }
    public static string Body { get; private set; } = "";

    public static void Apply()
    {
        try
        {
            var overlayType = AccessTools.TypeByName(OverlayTypeName);
            var open = overlayType?.GetMethod("Open", AnyStatic);
            _close = overlayType?.GetMethod("Close", AnyStatic, null, Type.EmptyTypes, null);
            if (overlayType == null || open == null || _close == null)
            {
                NOVRPlugin.Log.LogWarning("[NOVR] Could not find the game's JoinLobbyOverlay; joining a server won't show its progress in VR.");
                return;
            }

            _cancel = overlayType.GetMethod("CloseButton", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            _instance = overlayType.GetField("Instance", AnyStatic);
            var harmony = new Harmony("novr.vrui.joinprogress");
            harmony.Patch(open, postfix: new HarmonyMethod(typeof(JoinProgressPatch), nameof(OpenPostfix)));
            harmony.Patch(_close, postfix: new HarmonyMethod(typeof(JoinProgressPatch), nameof(ClosePostfix)));
        }
        catch (Exception exception)
        {
            NOVRPlugin.Log.LogWarning($"[NOVR] Could not patch the game's join progress for VR: {exception.Message}");
        }
    }

    // As the overlay's own Cancel: it goes, and the connection being made is stopped.
    public static void Cancel()
    {
        _cancelledAt = Time.unscaledTime;
        Hide();
        try
        {
            var overlay = _instance?.GetValue(null);
            if (overlay != null && _cancel != null)
            {
                _cancel.Invoke(overlay, null);
                return;
            }
        }
        catch (Exception exception)
        {
            LogFailure("cancel joining", exception);
        }

        Dismiss();
    }

    // As the overlay's own Close, once joining has failed.
    public static void Dismiss()
    {
        Hide();
        try
        {
            _close?.Invoke(null, null);
        }
        catch (Exception exception)
        {
            LogFailure("close the join message", exception);
        }
    }

    private static void OpenPostfix(object[] __args)
    {
        try
        {
            var progress = __args.Length > 0 ? __args[0] : null;
            if (progress == null) return;

            var type = progress.GetType();
            var joining = type.GetField("Join")?.GetValue(progress) is true;
            var body = type.GetField("Body")?.GetValue(progress) as string ?? "";
            if (!joining && Time.unscaledTime - _cancelledAt < CancelEchoSeconds) return;

            if (!joining) NOVRPlugin.Log.LogWarning($"[NOVR] Joining the server failed: {body}");
            Showing = true;
            Joining = joining;
            Body = body;
            Version++;
        }
        catch (Exception exception)
        {
            LogFailure("read the join progress", exception);
        }
    }

    private static void ClosePostfix() => Hide();

    private static void Hide()
    {
        if (!Showing) return;

        Showing = false;
        Version++;
    }

    private static void LogFailure(string what, Exception exception)
    {
        if (_loggedFailure) return;

        _loggedFailure = true;
        NOVRPlugin.Log.LogWarning($"[NOVR] Could not {what}: {(exception as TargetInvocationException)?.InnerException?.Message ?? exception.Message}");
    }
}
