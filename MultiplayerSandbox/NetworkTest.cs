using System;
using System.Collections;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Steamworks;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MultiplayerTestHarness
{
    internal enum NetworkTestRole
    {
        Host,
        Client
    }

    internal static class NetworkTest
    {
        internal const ulong FakeClientIdValue = 999UL;
        internal const string PipeName = "RaftMultiplayerTest_v1";

        static readonly Network_UserId FakeClientId = new Network_UserId(FakeClientIdValue);

        static readonly FieldInfo IsHostField = AccessTools.Field(typeof(Raft_Network), "isHost");
        static readonly FieldInfo LocalSteamIdField = AccessTools.Field(typeof(Raft_Network), "localSteamID");
        static readonly FieldInfo HostIdField = AccessTools.Field(typeof(Raft_Network), "hostID");
        static readonly FieldInfo ConnectedToHostField = AccessTools.Field(typeof(Raft_Network), "connectedToHost");
        static readonly FieldInfo WorldLoadedCoroutineField = AccessTools.Field(typeof(Raft_Network), "worldLoadedCoroutine");

        static readonly MethodInfo LoadSceneMethod = AccessTools.Method(typeof(Raft_Network), "LoadScene", new[] { typeof(string) });
        static readonly MethodInfo HandleMessageMethod = AccessTools.Method(typeof(Raft_Network), "HandleMessage");
        static readonly MethodInfo InternalAddPlayerMethod = AccessTools.Method(typeof(Raft_Network), "InternalAddPlayer");
        static readonly MethodInfo RemovePlayerMethod = AccessTools.Method(typeof(Raft_Network), "RemovePlayer");
        static readonly MethodInfo SendWorldMethod = AccessTools.Method(typeof(Raft_Network), "SendWorld");
        static readonly MethodInfo WorldHasBeenLoadedMethod = AccessTools.Method(typeof(Raft_Network), "WorldHasBeenLoaded");

        static Harmony _harmony;
        static Mutex _hostSessionMutex;
        static NetworkTestTransport _transport;

        static Network_UserId _hostId;
        static int _hostGameMode;
        static bool _hostCrossplay;
        static bool _handshakeReceived;
        static bool _handshakeSent;
        static bool _clientSceneRequested;
        static bool _clientWorldReceivedPending;
        static int _clientFinalizeDelayFrames;
        static int _clientWaitLogFrames;
        static int _lastTransportDisconnectSerial;
        static bool _booted;

        const string HostSessionMutexName = "Local\\RaftMultiplayerTestHost_v1";

        internal static NetworkTestRole Role { get; private set; }
        internal static bool IsActive => _booted;
        internal static bool IsClient => _booted && Role == NetworkTestRole.Client;
        internal static bool IsHost => _booted && Role == NetworkTestRole.Host;

        internal static bool HostSessionExists()
        {
            try
            {
                using (Mutex mutex = Mutex.OpenExisting(HostSessionMutexName))
                    return mutex != null;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                return false;
            }
            catch
            {
                return false;
            }
        }

        internal static bool StartHost()
        {
            if (_booted)
                return IsHost;

            bool createdNew;
            Mutex mutex = new Mutex(false, HostSessionMutexName, out createdNew);

            if (!createdNew)
            {
                mutex.Dispose();
                Debug.LogError("[NetTest] уже существует другой Network Test host");
                return false;
            }

            _hostSessionMutex = mutex;

            if (!Boot(NetworkTestRole.Host, true))
            {
                _hostSessionMutex.Dispose();
                _hostSessionMutex = null;
                return false;
            }

            Debug.Log("[NetTest] HOST запущен - второй Raft нужно открыть через RaftModLoader");
            return true;
        }

        internal static bool StartClient()
        {
            if (_booted)
                return IsClient;

            return Boot(NetworkTestRole.Client, true);
        }

        static bool Boot(NetworkTestRole role, bool installNetworkPatches)
        {
            if (_booted)
                return Role == role;

            try
            {
                Role = role;

                _transport = new NetworkTestTransport(Role, PipeName);
                _transport.Start();
                _lastTransportDisconnectSerial = _transport.DisconnectSerial;

                if (installNetworkPatches)
                {
                    _harmony = new Harmony("RaftMod.MultiplayerTest.Transport");
                    Patch(typeof(Patch_RaftNetwork_IsHost));
                    Patch(typeof(Patch_RaftNetwork_LocalSteamID));
                    Patch(typeof(Patch_RaftNetwork_Awake));
                    Patch(typeof(Patch_RaftNetwork_OnPlayFabSignedIn));
                    Patch(typeof(Patch_RaftNetwork_SendP2P));
                    Patch(typeof(Patch_RaftNetwork_RPC));
                    Patch(typeof(Patch_RaftNetwork_RPCExclude));
                }

                _booted = true;
                Debug.Log("[NetTest] role=" + Role + " pipe=" + PipeName);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError("[NetTest] запуск не удался: " + e);
                Shutdown();
                return false;
            }
        }

        static void Patch(Type type)
        {
            _harmony.CreateClassProcessor(type).Patch();
        }

        internal static void Shutdown()
        {
            try
            {
                _transport?.Dispose();
            }
            catch
            {
            }

            try
            {
                if (_harmony != null)
                    _harmony.UnpatchAll(_harmony.Id);
            }
            catch
            {
            }

            if (_hostSessionMutex != null)
            {
                try
                {
                    _hostSessionMutex.Dispose();
                }
                catch
                {
                }

                _hostSessionMutex = null;
            }

            _transport = null;
            _harmony = null;
            _hostId = default(Network_UserId);
            _hostGameMode = 0;
            _hostCrossplay = false;
            _handshakeReceived = false;
            _handshakeSent = false;
            _clientSceneRequested = false;
            _clientWorldReceivedPending = false;
            _clientFinalizeDelayFrames = 0;
            _clientWaitLogFrames = 0;
            _lastTransportDisconnectSerial = 0;
            _booted = false;
        }

        internal static void Tick()
        {
            if (!_booted || _transport == null)
                return;

            NetTestFrame frame;
            while (_transport.TryDequeue(out frame))
            {
                try
                {
                    if (frame.Kind == NetTestFrameKind.Handshake)
                        ReceiveHandshake(frame.Payload);
                    else if (frame.Kind == NetTestFrameKind.Message)
                        ReceiveMessage(frame.Payload);
                }
                catch (Exception e)
                {
                    Debug.LogError("[NetTest] receive failed: " + e);
                }
            }

            Raft_Network network = ComponentManager<Raft_Network>.Value;
            if (network == null)
                return;

            HandleTransportDisconnect(network);

            if (IsClient)
            {
                ForceClientState(network);
                TryEnterClientWorld(network);
                TryFinalizeClientWorld(network);
            }
            else
            {
                TrySendHostHandshake(network);
            }
        }

        static void HandleTransportDisconnect(Raft_Network network)
        {
            int serial = _transport.DisconnectSerial;
            if (serial == _lastTransportDisconnectSerial)
                return;

            _lastTransportDisconnectSerial = serial;

            if (!IsHost)
                return;

            _handshakeSent = false;

            if (network.remoteUsers == null || !network.remoteUsers.ContainsKey(FakeClientId))
                return;

            try
            {
                RemovePlayerMethod.Invoke(network, new object[] { FakeClientId });
                Debug.Log("[NetTest] client 999 отключён - игрок удалён у HOST");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[NetTest] не удалось удалить отключённого client 999: " + e.Message);
            }
        }

        static void TrySendHostHandshake(Raft_Network network)
        {
            if (_handshakeSent || !_transport.Connected)
                return;

            if (SceneManager.GetActiveScene().name != Raft_Network.GameSceneName)
                return;

            if (!Raft_Network.WorldHasBeenRecieved)
                return;

            Network_Player localPlayer = network.GetLocalPlayer();
            if (localPlayer == null)
                return;

            Network_UserId hostId = network.LocalSteamID;
            if (!hostId.IsValid())
                return;

            _transport.SendHandshake(hostId.Id, (int)GameManager.GameMode, GameManager.Crossplay);
            _handshakeSent = true;

            Debug.Log("[NetTest] handshake sent host=" + hostId.Id + " mode=" + GameManager.GameMode);
        }

        static void ReceiveHandshake(byte[] payload)
        {
            if (!IsClient)
                return;

            ulong hostId;
            int gameMode;
            bool crossplay;

            NetworkTestTransport.DecodeHandshake(payload, out hostId, out gameMode, out crossplay);

            _hostId = new Network_UserId(hostId);
            _hostGameMode = gameMode;
            _hostCrossplay = crossplay;
            _handshakeReceived = true;

            Debug.Log("[NetTest] handshake received host=" + hostId + " mode=" + (GameMode)gameMode);
        }

        static void TryEnterClientWorld(Raft_Network network)
        {
            if (!_handshakeReceived || _clientSceneRequested)
                return;

            if (SceneManager.GetActiveScene().name == Raft_Network.GameSceneName)
            {
                _clientSceneRequested = true;
                return;
            }

            if (LoadSceneManager.IsLoadingScene)
                return;

            ForceClientState(network);

            Raft_Network.CurrentRequestJoinAuthSetting = RequestJoinAuthSetting.INVITE_ONLY;
            Raft_Network.WorldHasBeenRecieved = false;
            network.gameManagerStartCalled = false;
            WorldLoadedCoroutineField.SetValue(network, null);
            GameManager.IsLeavingGame = false;
            GameManager.Crossplay = _hostCrossplay;
            GameManager.Password = string.Empty;

            GameModeValueManager.SelectCurrentGameMode((GameMode)_hostGameMode);

            LoadSceneMethod.Invoke(network, new object[] { Raft_Network.GameSceneName });
            _clientSceneRequested = true;

            Debug.Log("[NetTest] client loading MainScene");
        }

        internal static void ForceClientState(Raft_Network network)
        {
            if (!IsClient || network == null)
                return;

            IsHostField.SetValue(null, false);
            LocalSteamIdField.SetValue(network, FakeClientId);

            if (_hostId.IsValid())
                HostIdField.SetValue(network, _hostId);

            ConnectedToHostField.SetValue(network, _hostId.IsValid());
        }

        internal static void ForceClientAwakeState(Raft_Network network)
        {
            if (!IsClient || network == null)
                return;

            IsHostField.SetValue(null, false);
            LocalSteamIdField.SetValue(network, FakeClientId);
        }

        internal static bool TryRouteSendP2P(Raft_Network network, Network_UserId target, Message message)
        {
            if (!_booted || _transport == null || !_transport.Connected || network == null || message == null)
                return false;

            if (IsClient)
            {
                if (!_hostId.IsValid() || target != _hostId)
                    return false;

                _transport.SendMessage(message);
                return true;
            }

            if (target != FakeClientId)
                return false;

            _transport.SendMessage(message);
            return true;
        }

        internal static bool TryRouteRpc(Raft_Network network, Message message, Target target)
        {
            if (!IsHost || _transport == null || !_transport.Connected || network == null || message == null)
                return false;

            if (!HostHasFakeClient(network))
                return false;

            if (target == Target.All)
                DispatchLocal(message, network.LocalSteamID);

            _transport.SendMessage(message);
            return true;
        }

        internal static bool TryRouteRpcExclude(Raft_Network network, Message message, Target target, Network_UserId excludeId)
        {
            if (!IsHost || _transport == null || !_transport.Connected || network == null || message == null)
                return false;

            if (!HostHasFakeClient(network))
                return false;

            if (excludeId != FakeClientId)
                _transport.SendMessage(message);

            return true;
        }

        static bool HostHasFakeClient(Raft_Network network)
        {
            return network.remoteUsers != null && network.remoteUsers.ContainsKey(FakeClientId);
        }

        static void ReceiveMessage(byte[] payload)
        {
            if (payload == null || payload.Length == 0)
                return;

            Message message;

            using (FastBufferReader reader = new FastBufferReader(payload, Allocator.Temp, payload.Length, 0))
            {
                if (!reader.TryBeginRead(payload.Length))
                    throw new InvalidOperationException("message buffer is not readable");

                message = FastMessageDeserializer.DeserializeMessage<Message>(reader);
            }

            if (message == null)
                return;

            Raft_Network network = ComponentManager<Raft_Network>.Value;
            if (network == null)
                return;

            Network_UserId sender = IsHost ? FakeClientId : _hostId;
            DispatchRemote(network, message, sender);
        }

        static void DispatchRemote(Raft_Network network, Message message, Network_UserId sender)
        {
            Message_Compound compound = message as Message_Compound;
            if (compound != null)
            {
                for (int i = 0; i < compound.messages.Count; i++)
                    DispatchRemote(network, compound.messages[i], sender);

                return;
            }

            if (message.Type == Messages.CreatePlayer)
            {
                Message_Player_Create create = message as Message_Player_Create;
                if (create != null)
                {
                    Debug.Log(
                        "[NetTest] CreatePlayer recv user=" + create.UserId.Id +
                        " local=" + network.LocalSteamID.Id +
                        " obj=" + create.ObjectIndex +
                        " beh=" + create.BehaviourIndex);
                }
            }

            if (IsHost && message.Type == Messages.PlayerJoined)
                HandlePlayerJoined(network, message as Message_PlayerJoined, sender);

            if (IsHost && message.Type == Messages.RequestWorld)
                HandleWorldRequest(network, sender);

            bool worldReceived = false;
            object[] args = { message, sender, worldReceived };
            HandleMessageMethod.Invoke(network, args);
            worldReceived = (bool)args[2];

            if (message.Type == Messages.CreatePlayer)
            {
                Network_Player lp = network.GetLocalPlayer();
                Debug.Log(
                    "[NetTest] CreatePlayer handled remoteUsers=" + network.remoteUsers.Count +
                    " localPlayer=" + (lp != null ? lp.steamID.Id.ToString() : "null"));
            }

            NetworkUpdateManager.DeserializeSingleMessage(message, sender);

            if (IsClient && worldReceived)
                QueueClientWorldLoaded();
        }

        static void HandlePlayerJoined(Raft_Network network, Message_PlayerJoined message, Network_UserId sender)
        {
            if (message == null)
                return;

            if (!network.remoteUsers.ContainsKey(sender))
            {
                Network_Player player = InternalAddPlayerMethod.Invoke(
                    network,
                    new object[] { sender, message.characterSettings }) as Network_Player;

                if (player == null)
                {
                    Debug.LogError("[NetTest] не удалось создать fake client id=" + sender.Id);
                    return;
                }

                Debug.Log("[NetTest] fake client добавлен напрямую id=" + sender.Id);
            }

            network.SendP2P(
                sender,
                new Message_NetworkVersion(Messages.NetworkVersion, 1),
                EP2PSend.k_EP2PSendReliable,
                NetworkChannel.Channel_Session);

            Debug.Log("[NetTest] fake client joined id=" + sender.Id);
        }

        static void HandleWorldRequest(Raft_Network network, Network_UserId sender)
        {
            IEnumerator routine = SendWorldMethod.Invoke(network, new object[] { sender }) as IEnumerator;
            if (routine != null)
                network.StartCoroutine(routine);

            Debug.Log("[NetTest] world requested by id=" + sender.Id);
        }

        static void QueueClientWorldLoaded()
        {
            if (_clientWorldReceivedPending)
                return;

            _clientWorldReceivedPending = true;
            _clientFinalizeDelayFrames = 5;

            Debug.Log("[NetTest] WorldReceived получен - ожидается готовность клиентской сцены");
        }

        static void TryFinalizeClientWorld(Raft_Network network)
        {
            if (!_clientWorldReceivedPending || network == null)
                return;

            _clientWaitLogFrames++;

            if (_clientWaitLogFrames >= 120)
            {
                _clientWaitLogFrames = 0;

                Network_Player debugLocal = network.GetLocalPlayer();

                Debug.Log(
                    "[NetTest] waiting finalize" +
                    " scene=" + SceneManager.GetActiveScene().name +
                    " gmStart=" + network.gameManagerStartCalled +
                    " landmarks=" + Raft_Network.IsAllLandmarksLoaded +
                    " users=" + (network.remoteUsers != null ? network.remoteUsers.Count : -1) +
                    " local=" + (debugLocal != null ? debugLocal.steamID.Id.ToString() : "null"));
            }

            if (GameManager.IsLeavingGame)
                return;

            if (SceneManager.GetActiveScene().name != Raft_Network.GameSceneName)
                return;

            if (!network.gameManagerStartCalled)
                return;

            if (!Raft_Network.IsAllLandmarksLoaded)
                return;

            GameManager gameManager = UnityEngine.Object.FindObjectOfType<GameManager>();
            SaveAndLoad saveAndLoad = ComponentManager<SaveAndLoad>.Value;
            Network_Player localPlayer = network.GetLocalPlayer();

            if (gameManager == null || saveAndLoad == null || localPlayer == null)
                return;

            if (!localPlayer.IsLocalPlayer ||
                localPlayer.PlayerScript == null ||
                localPlayer.Inventory == null ||
                localPlayer.Stats == null)
                return;

            if (ComponentManager<Network_Player>.Value != localPlayer)
                ComponentManager<Network_Player>.Value = localPlayer;

            if (_clientFinalizeDelayFrames > 0)
            {
                _clientFinalizeDelayFrames--;
                return;
            }

            _clientWorldReceivedPending = false;

            Debug.Log(
                "[NetTest] клиент готов к OnWorldReceived" +
                " local=" + localPlayer.steamID.Id +
                " remoteUsers=" + network.remoteUsers.Count +
                " save=" + (saveAndLoad != null) +
                " gm=" + (gameManager != null));

            StartClientWorldLoaded(network);
        }

        static void StartClientWorldLoaded(Raft_Network network)
        {
            if (WorldLoadedCoroutineField.GetValue(network) != null)
                return;

            IEnumerator routine = WorldHasBeenLoadedMethod.Invoke(network, null) as IEnumerator;
            if (routine == null)
                return;

            Coroutine coroutine = network.StartCoroutine(routine);
            WorldLoadedCoroutineField.SetValue(network, coroutine);

            Debug.Log("[NetTest] WorldReceived processed");
        }

        static void DispatchLocal(Message message, Network_UserId localId)
        {
            Message_Compound compound = message as Message_Compound;
            if (compound != null)
            {
                for (int i = 0; i < compound.messages.Count; i++)
                    DispatchLocal(compound.messages[i], localId);

                return;
            }

            NetworkUpdateManager.DeserializeSingleMessage(message, localId);
        }

        internal static Network_UserId GetClientLocalId()
        {
            return FakeClientId;
        }
    }
}
