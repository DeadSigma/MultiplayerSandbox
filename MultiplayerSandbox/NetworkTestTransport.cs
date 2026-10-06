using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using UnityEngine;

namespace MultiplayerTestHarness
{
    internal enum NetTestFrameKind : byte
    {
        Handshake = 1,
        Message = 2
    }

    internal sealed class NetTestFrame
    {
        internal NetTestFrameKind Kind;
        internal byte[] Payload;
    }

    internal sealed class NetworkTestTransport : IDisposable
    {
        const int MaxFrameSize = 16 * 1024 * 1024;

        readonly NetworkTestRole _role;
        readonly string _pipeName;
        readonly ConcurrentQueue<NetTestFrame> _incoming = new ConcurrentQueue<NetTestFrame>();
        readonly object _writeLock = new object();

        Thread _thread;
        PipeStream _stream;
        BinaryWriter _writer;
        volatile bool _stop;
        volatile bool _connected;
        int _disconnectSerial;

        internal bool Connected
        {
            get
            {
                try
                {
                    return _connected && _stream != null && _stream.IsConnected;
                }
                catch
                {
                    return false;
                }
            }
        }

        internal int DisconnectSerial => Volatile.Read(ref _disconnectSerial);

        internal NetworkTestTransport(NetworkTestRole role, string pipeName)
        {
            _role = role;
            _pipeName = pipeName;
        }

        internal void Start()
        {
            _thread = new Thread(ThreadMain);
            _thread.IsBackground = true;
            _thread.Name = "Raft NetTest Pipe";
            _thread.Start();
        }

        void ThreadMain()
        {
            while (!_stop)
            {
                try
                {
                    if (_role == NetworkTestRole.Host)
                        RunServer();
                    else
                        RunClient();
                }
                catch (IOException)
                {
                }
                catch (ObjectDisposedException)
                {
                }
                catch (Exception e)
                {
                    if (!_stop)
                        Debug.LogWarning("[NetTest] pipe reconnect: " + e.Message);
                }

                CleanupConnection();

                if (!_stop)
                    Thread.Sleep(500);
            }
        }

        void RunServer()
        {
            NamedPipeServerStream server = new NamedPipeServerStream(
                _pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            _stream = server;
            server.WaitForConnection();
            BeginConnected(server);
            ReadLoop(server);
        }

        void RunClient()
        {
            NamedPipeClientStream client = new NamedPipeClientStream(
                ".",
                _pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);

            _stream = client;

            while (!_stop && !client.IsConnected)
            {
                try
                {
                    client.Connect(1000);
                }
                catch (TimeoutException)
                {
                }
            }

            if (_stop)
                return;

            BeginConnected(client);
            ReadLoop(client);
        }

        void BeginConnected(PipeStream stream)
        {
            lock (_writeLock)
            {
                _writer = new BinaryWriter(stream);
                _connected = true;
            }

            Debug.Log("[NetTest] pipe connected");
        }

        void ReadLoop(PipeStream stream)
        {
            BinaryReader reader = new BinaryReader(stream);

            while (!_stop && stream.IsConnected)
            {
                int frameLength = reader.ReadInt32();

                if (frameLength < 1 || frameLength > MaxFrameSize)
                    throw new InvalidDataException("invalid frame length " + frameLength);

                NetTestFrameKind kind = (NetTestFrameKind)reader.ReadByte();
                byte[] payload = ReadExactly(reader, frameLength - 1);

                _incoming.Enqueue(new NetTestFrame
                {
                    Kind = kind,
                    Payload = payload
                });
            }
        }

        static byte[] ReadExactly(BinaryReader reader, int count)
        {
            byte[] buffer = new byte[count];
            int offset = 0;

            while (offset < count)
            {
                int read = reader.Read(buffer, offset, count - offset);
                if (read <= 0)
                    throw new EndOfStreamException();

                offset += read;
            }

            return buffer;
        }

        internal bool TryDequeue(out NetTestFrame frame)
        {
            return _incoming.TryDequeue(out frame);
        }

        internal void SendHandshake(ulong hostId, int gameMode, bool crossplay)
        {
            byte[] payload;

            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(ms))
            {
                writer.Write(hostId);
                writer.Write(gameMode);
                writer.Write(crossplay);
                writer.Flush();
                payload = ms.ToArray();
            }

            SendFrame(NetTestFrameKind.Handshake, payload);
        }

        internal static void DecodeHandshake(byte[] payload, out ulong hostId, out int gameMode, out bool crossplay)
        {
            using (MemoryStream ms = new MemoryStream(payload))
            using (BinaryReader reader = new BinaryReader(ms))
            {
                hostId = reader.ReadUInt64();
                gameMode = reader.ReadInt32();
                crossplay = reader.ReadBoolean();
            }
        }

        internal void SendMessage(Message message)
        {
            byte[] payload = NetcodeBridge.Serialize(message);
            SendFrame(NetTestFrameKind.Message, payload);
        }

        void SendFrame(NetTestFrameKind kind, byte[] payload)
        {
            if (!Connected)
                return;

            bool broken = false;

            lock (_writeLock)
            {
                try
                {
                    if (_writer == null || !_connected || _stream == null || !_stream.IsConnected)
                        return;

                    _writer.Write(payload.Length + 1);
                    _writer.Write((byte)kind);
                    _writer.Write(payload);
                    _writer.Flush();
                }
                catch (IOException)
                {
                    broken = true;
                }
                catch (ObjectDisposedException)
                {
                    broken = true;
                }
                catch (InvalidOperationException)
                {
                    broken = true;
                }
            }

            if (broken)
                CleanupConnection();
        }

        void CleanupConnection()
        {
            bool wasConnected;

            lock (_writeLock)
            {
                wasConnected = _connected;
                _connected = false;

                try
                {
                    _writer?.Dispose();
                }
                catch
                {
                }

                try
                {
                    _stream?.Dispose();
                }
                catch
                {
                }

                _writer = null;
                _stream = null;
            }

            if (wasConnected)
                Interlocked.Increment(ref _disconnectSerial);
        }

        public void Dispose()
        {
            _stop = true;
            CleanupConnection();

            try
            {
                if (_thread != null && _thread.IsAlive)
                    _thread.Join(1500);
            }
            catch
            {
            }

            _thread = null;
        }
    }
}
