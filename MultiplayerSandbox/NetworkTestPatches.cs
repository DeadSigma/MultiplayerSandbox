using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using HMLLibrary;
using Steamworks;

#pragma warning disable CS0618

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

    [HarmonyPatch]
    internal static class Patch_RAPI_SendNetworkMessage
    {
        static IEnumerable<MethodBase>
            TargetMethods()
        {
            MethodInfo[] methods =
                typeof(RAPI).GetMethods(
                    BindingFlags.Static |
                    BindingFlags.Public |
                    BindingFlags.NonPublic
                );

            for (int i = 0;
                 i < methods.Length;
                 i++)
            {
                MethodInfo method =
                    methods[i];

                if (method.Name !=
                    nameof(RAPI.SendNetworkMessage) ||
                    method.ContainsGenericParameters)
                {
                    continue;
                }

                yield return method;
            }
        }

        static bool Prefix(
            object[] __args)
        {
            if (__args == null)
            {
                return true;
            }

            Message message = null;
            int channel = 0;
            bool hasChannel = false;
            Target target =
                Target.Other;

            for (int i = 0;
                 i < __args.Length;
                 i++)
            {
                object arg =
                    __args[i];

                if (arg is Message)
                {
                    message =
                        (Message)arg;
                }
                else if (arg is int)
                {
                    channel =
                        (int)arg;

                    hasChannel =
                        true;
                }
                else if (arg is Target)
                {
                    target =
                        (Target)arg;
                }
            }

            if (message == null ||
                !hasChannel)
            {
                return true;
            }

            return !NetworkTest
                .TryRouteRapiMessage(
                    message,
                    channel,
                    target
                );
        }
    }

    [HarmonyPatch]
    internal static class Patch_RAPI_ListenForNetworkMessagesOnChannel
    {
        static IEnumerable<MethodBase>
            TargetMethods()
        {
            MethodInfo[] methods =
                typeof(RAPI).GetMethods(
                    BindingFlags.Static |
                    BindingFlags.Public |
                    BindingFlags.NonPublic
                );

            for (int i = 0;
                 i < methods.Length;
                 i++)
            {
                MethodInfo method =
                    methods[i];

                if (method.Name !=
                    nameof(
                        RAPI.ListenForNetworkMessagesOnChannel
                    ) ||
                    method.ContainsGenericParameters ||
                    method.ReturnType !=
                        typeof(NetworkMessage))
                {
                    continue;
                }

                yield return method;
            }
        }

        static bool Prefix(
            object[] __args,
            ref NetworkMessage __result)
        {
            if (__args == null)
            {
                return true;
            }

            int channel = 0;
            bool hasChannel = false;

            for (int i = 0;
                 i < __args.Length;
                 i++)
            {
                if (__args[i] is int)
                {
                    channel =
                        (int)__args[i];

                    hasChannel =
                        true;

                    break;
                }
            }

            if (!hasChannel)
            {
                return true;
            }

            NetworkMessage message;

            if (!NetworkTest
                    .TryListenForRapiMessage(
                        channel,
                        out message
                    ))
            {
                return true;
            }

            __result =
                message;

            return false;
        }
    }

}
