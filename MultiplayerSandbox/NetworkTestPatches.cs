using System;
using System.Reflection;
using HarmonyLib;
using Steamworks;

namespace MultiplayerTestHarness
{
    [HarmonyPatch]
    internal static class Patch_RaftNetwork_IsHost
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.PropertyGetter(typeof(Raft_Network), "IsHost");
        }

        static bool Prefix(ref bool __result)
        {
            if (!NetworkTest.IsClient)
                return true;

            __result = false;
            return false;
        }
    }

    [HarmonyPatch]
    internal static class Patch_RaftNetwork_LocalSteamID
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.PropertyGetter(typeof(Raft_Network), "LocalSteamID");
        }

        static bool Prefix(ref Network_UserId __result)
        {
            if (!NetworkTest.IsClient)
                return true;

            __result = NetworkTest.GetClientLocalId();
            return false;
        }
    }

    [HarmonyPatch(typeof(Raft_Network), "Awake")]
    internal static class Patch_RaftNetwork_Awake
    {
        static void Postfix(Raft_Network __instance)
        {
            NetworkTest.ForceClientAwakeState(__instance);
        }
    }

    [HarmonyPatch(typeof(Raft_Network), "OnPlayFabSignedIn")]
    internal static class Patch_RaftNetwork_OnPlayFabSignedIn
    {
        static void Postfix(Raft_Network __instance)
        {
            NetworkTest.ForceClientAwakeState(__instance);
        }
    }

    [HarmonyPatch]
    internal static class Patch_RaftNetwork_SendP2P
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(
                typeof(Raft_Network),
                "SendP2P",
                new[]
                {
                    typeof(Network_UserId),
                    typeof(Message),
                    typeof(EP2PSend),
                    typeof(NetworkChannel)
                });
        }

        static bool Prefix(
            Raft_Network __instance,
            Network_UserId steamID,
            Message message,
            EP2PSend sendType,
            NetworkChannel channel)
        {
            return !NetworkTest.TryRouteSendP2P(__instance, steamID, message);
        }
    }

    [HarmonyPatch]
    internal static class Patch_RaftNetwork_RPC
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(
                typeof(Raft_Network),
                "RPC",
                new[]
                {
                    typeof(Message),
                    typeof(Target),
                    typeof(EP2PSend),
                    typeof(NetworkChannel)
                });
        }

        static bool Prefix(
            Raft_Network __instance,
            Message message,
            Target target,
            EP2PSend sendType,
            NetworkChannel channel)
        {
            return !NetworkTest.TryRouteRpc(__instance, message, target);
        }
    }

    [HarmonyPatch]
    internal static class Patch_RaftNetwork_RPCExclude
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(
                typeof(Raft_Network),
                "RPCExclude",
                new[]
                {
                    typeof(Message),
                    typeof(Target),
                    typeof(Network_UserId),
                    typeof(EP2PSend),
                    typeof(NetworkChannel)
                });
        }

        static bool Prefix(
            Raft_Network __instance,
            Message message,
            Target target,
            Network_UserId excludeID,
            EP2PSend sendType,
            NetworkChannel channel)
        {
            return !NetworkTest.TryRouteRpcExclude(__instance, message, target, excludeID);
        }
    }
}
