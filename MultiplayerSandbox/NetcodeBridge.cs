using System;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace MultiplayerTestHarness
{
    internal static class NetcodeBridge
    {
        static Type _writerType;
        static Type _readerType;
        static Type _allocatorType;

        static Func<Message, byte[]> _serialize;
        static Func<byte[], Message> _deserialize;
        static Func<byte[], Message> _deserializeRMessage;

        static Type WriterType => _writerType ?? (_writerType = FindType("Unity.Netcode.FastBufferWriter"));
        static Type ReaderType => _readerType ?? (_readerType = FindType("Unity.Netcode.FastBufferReader"));
        static Type AllocatorType => _allocatorType ?? (_allocatorType = FindType("Unity.Collections.Allocator"));

        internal static byte[] Serialize(Message message)
        {
            if (message == null)
                return null;

            if (_serialize == null)
                _serialize = BuildSerialize();

            byte[] payload = _serialize(message);

            if (payload == null || payload.Length < 2)
                throw new InvalidOperationException(
                    "[MPTest] SerializeFast вернул пустой пакет для " + message.GetType().Name);

            short encodedType = BitConverter.ToInt16(payload, 0);
            short expectedType = (short)message.Type;

            if (encodedType != -1 && encodedType != expectedType)
            {
                throw new InvalidOperationException(
                    "[MPTest] повреждён пакет после SerializeFast: expected=" +
                    expectedType + " actual=" + encodedType +
                    " message=" + message.GetType().Name);
            }

            return payload;
        }

        internal static Message Deserialize(byte[] payload)
        {
            if (payload == null || payload.Length < 2)
                return null;

            short encodedType = BitConverter.ToInt16(payload, 0);

            if (encodedType == -1)
            {
                if (_deserializeRMessage == null)
                    _deserializeRMessage = BuildRMessageDeserialize();

                return _deserializeRMessage(payload);
            }

            if (_deserialize == null)
                _deserialize = BuildDeserialize();

            return _deserialize(payload);
        }

        static Func<Message, byte[]> BuildSerialize()
        {
            ConstructorInfo constructor = FindWriterConstructor();

            MethodInfo serializeFast = typeof(Message).GetMethod(
                "SerializeFast",
                BindingFlags.Instance | BindingFlags.Public,
                null,
                new[] { WriterType },
                null);

            MethodInfo toArray = WriterType.GetMethod(
                "ToArray",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                Type.EmptyTypes,
                null);

            MethodInfo dispose = WriterType.GetMethod(
                "Dispose",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                Type.EmptyTypes,
                null);

            if (serializeFast == null)
                throw new MissingMethodException("Message.SerializeFast");

            if (toArray == null)
                throw new MissingMethodException("FastBufferWriter.ToArray");

            DynamicMethod method = new DynamicMethod(
                "MPTest_SerializeFast",
                typeof(byte[]),
                new[] { typeof(Message) },
                typeof(NetcodeBridge).Module,
                true);

            ILGenerator il = method.GetILGenerator();

            LocalBuilder writer = il.DeclareLocal(WriterType);
            LocalBuilder result = il.DeclareLocal(typeof(byte[]));

            il.Emit(OpCodes.Ldloca_S, writer);
            EmitWriterConstructorArguments(il, constructor);
            il.Emit(OpCodes.Call, constructor);

            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldloc, writer);
            il.Emit(OpCodes.Callvirt, serializeFast);

            il.Emit(OpCodes.Ldloca_S, writer);
            il.Emit(OpCodes.Call, toArray);
            il.Emit(OpCodes.Stloc, result);

            if (dispose != null)
            {
                il.Emit(OpCodes.Ldloca_S, writer);
                il.Emit(OpCodes.Call, dispose);
            }

            il.Emit(OpCodes.Ldloc, result);
            il.Emit(OpCodes.Ret);

            return (Func<Message, byte[]>)method.CreateDelegate(
                typeof(Func<Message, byte[]>));
        }

        static Func<byte[], Message> BuildDeserialize()
        {
            ConstructorInfo constructor = FindReaderConstructor();

            MethodInfo tryBeginRead = ReaderType.GetMethod(
                "TryBeginRead",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { typeof(int) },
                null);

            MethodInfo dispose = ReaderType.GetMethod(
                "Dispose",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                Type.EmptyTypes,
                null);

            MethodInfo deserializeMessage = FindDeserializeMethod();

            DynamicMethod method = new DynamicMethod(
                "MPTest_DeserializeFast",
                typeof(Message),
                new[] { typeof(byte[]) },
                typeof(NetcodeBridge).Module,
                true);

            ILGenerator il = method.GetILGenerator();

            LocalBuilder reader = il.DeclareLocal(ReaderType);
            LocalBuilder result = il.DeclareLocal(typeof(Message));

            il.Emit(OpCodes.Ldloca_S, reader);
            EmitReaderConstructorArguments(il, constructor);
            il.Emit(OpCodes.Call, constructor);

            if (tryBeginRead != null)
            {
                Label readable = il.DefineLabel();

                il.Emit(OpCodes.Ldloca_S, reader);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldlen);
                il.Emit(OpCodes.Conv_I4);
                il.Emit(OpCodes.Call, tryBeginRead);
                il.Emit(OpCodes.Brtrue_S, readable);

                il.Emit(OpCodes.Ldstr, "message buffer is not readable");
                il.Emit(OpCodes.Newobj,
                    typeof(InvalidOperationException).GetConstructor(
                        new[] { typeof(string) }));
                il.Emit(OpCodes.Throw);

                il.MarkLabel(readable);
            }

            il.Emit(OpCodes.Ldloc, reader);
            il.Emit(OpCodes.Call, deserializeMessage);
            il.Emit(OpCodes.Stloc, result);

            if (dispose != null)
            {
                il.Emit(OpCodes.Ldloca_S, reader);
                il.Emit(OpCodes.Call, dispose);
            }

            il.Emit(OpCodes.Ldloc, result);
            il.Emit(OpCodes.Ret);

            return (Func<byte[], Message>)method.CreateDelegate(
                typeof(Func<byte[], Message>));
        }

        static Func<byte[], Message> BuildRMessageDeserialize()
        {
            Type rMessageType = FindType("RMessage");

            if (!typeof(Message).IsAssignableFrom(rMessageType))
                throw new InvalidOperationException("RMessage не наследуется от Message");

            ConstructorInfo messageConstructor = rMessageType.GetConstructor(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                Type.EmptyTypes,
                null);

            if (messageConstructor == null)
                throw new MissingMethodException("RMessage constructor");

            ConstructorInfo readerConstructor = FindReaderConstructor();

            ParameterInfo[] readerParameters = readerConstructor.GetParameters();

            if (readerParameters.Length != 4)
                throw new MissingMethodException(
                    "Для RMessage требуется FastBufferReader(byte[], Allocator, int, int)");

            MethodInfo deserializeFast = rMessageType.GetMethod(
                "DeserializeFast",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { ReaderType },
                null);

            if (deserializeFast == null)
                throw new MissingMethodException("RMessage.DeserializeFast");

            MethodInfo dispose = ReaderType.GetMethod(
                "Dispose",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                Type.EmptyTypes,
                null);

            DynamicMethod method = new DynamicMethod(
                "MPTest_DeserializeRMessage",
                typeof(Message),
                new[] { typeof(byte[]) },
                typeof(NetcodeBridge).Module,
                true);

            ILGenerator il = method.GetILGenerator();

            LocalBuilder reader = il.DeclareLocal(ReaderType);
            LocalBuilder message = il.DeclareLocal(rMessageType);

            il.Emit(OpCodes.Ldloca_S, reader);
            il.Emit(OpCodes.Ldarg_0);
            EmitTempAllocator(il);

            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldlen);
            il.Emit(OpCodes.Conv_I4);
            il.Emit(OpCodes.Ldc_I4_2);
            il.Emit(OpCodes.Sub);

            il.Emit(OpCodes.Ldc_I4_2);
            il.Emit(OpCodes.Call, readerConstructor);

            il.Emit(OpCodes.Newobj, messageConstructor);
            il.Emit(OpCodes.Stloc, message);

            il.Emit(OpCodes.Ldloc, message);
            il.Emit(OpCodes.Ldloc, reader);
            il.Emit(OpCodes.Callvirt, deserializeFast);

            if (dispose != null)
            {
                il.Emit(OpCodes.Ldloca_S, reader);
                il.Emit(OpCodes.Call, dispose);
            }

            il.Emit(OpCodes.Ldloc, message);
            il.Emit(OpCodes.Ret);

            return (Func<byte[], Message>)method.CreateDelegate(
                typeof(Func<byte[], Message>));
        }

        static ConstructorInfo FindWriterConstructor()
        {
            ConstructorInfo[] constructors = WriterType.GetConstructors(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            for (int i = 0; i < constructors.Length; i++)
            {
                ParameterInfo[] p = constructors[i].GetParameters();

                if (p.Length == 3 &&
                    p[0].ParameterType == typeof(int) &&
                    p[1].ParameterType == AllocatorType &&
                    p[2].ParameterType == typeof(int))
                    return constructors[i];
            }

            for (int i = 0; i < constructors.Length; i++)
            {
                ParameterInfo[] p = constructors[i].GetParameters();

                if (p.Length == 2 &&
                    p[0].ParameterType == typeof(int) &&
                    p[1].ParameterType == AllocatorType)
                    return constructors[i];
            }

            throw new MissingMethodException("FastBufferWriter constructor");
        }

        static ConstructorInfo FindReaderConstructor()
        {
            ConstructorInfo[] constructors = ReaderType.GetConstructors(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            for (int i = 0; i < constructors.Length; i++)
            {
                ParameterInfo[] p = constructors[i].GetParameters();

                if (p.Length == 4 &&
                    p[0].ParameterType == typeof(byte[]) &&
                    p[1].ParameterType == AllocatorType &&
                    p[2].ParameterType == typeof(int) &&
                    p[3].ParameterType == typeof(int))
                    return constructors[i];
            }

            for (int i = 0; i < constructors.Length; i++)
            {
                ParameterInfo[] p = constructors[i].GetParameters();

                if (p.Length == 3 &&
                    p[0].ParameterType == typeof(byte[]) &&
                    p[1].ParameterType == AllocatorType &&
                    p[2].ParameterType == typeof(int))
                    return constructors[i];
            }

            throw new MissingMethodException("FastBufferReader constructor");
        }

        static void EmitWriterConstructorArguments(
            ILGenerator il,
            ConstructorInfo constructor)
        {
            ParameterInfo[] p = constructor.GetParameters();

            il.Emit(OpCodes.Ldc_I4, 256);
            EmitTempAllocator(il);

            if (p.Length == 3)
                il.Emit(OpCodes.Ldc_I4, 10485760);
        }

        static void EmitReaderConstructorArguments(
            ILGenerator il,
            ConstructorInfo constructor)
        {
            ParameterInfo[] p = constructor.GetParameters();

            il.Emit(OpCodes.Ldarg_0);
            EmitTempAllocator(il);

            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldlen);
            il.Emit(OpCodes.Conv_I4);

            if (p.Length == 4)
                il.Emit(OpCodes.Ldc_I4_0);
        }

        static void EmitTempAllocator(ILGenerator il)
        {
            object value = Enum.Parse(AllocatorType, "Temp");
            il.Emit(OpCodes.Ldc_I4, Convert.ToInt32(value));
        }

        static MethodInfo FindDeserializeMethod()
        {
            Type deserializerType = FindType("FastMessageDeserializer");

            MethodInfo[] methods = deserializerType.GetMethods(
                BindingFlags.Static |
                BindingFlags.Public |
                BindingFlags.NonPublic);

            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo method = methods[i];

                if (method.Name != "DeserializeMessage" ||
                    !method.IsGenericMethodDefinition)
                    continue;

                ParameterInfo[] p = method.GetParameters();

                if (p.Length == 1 &&
                    p[0].ParameterType == ReaderType)
                {
                    return method.MakeGenericMethod(typeof(Message));
                }
            }

            throw new MissingMethodException(
                "FastMessageDeserializer.DeserializeMessage");
        }

        static Type FindType(string fullName)
        {
            Type type = AccessTools.TypeByName(fullName);

            if (type != null)
                return type;

            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();

            for (int i = 0; i < assemblies.Length; i++)
            {
                type = assemblies[i].GetType(fullName, false);

                if (type != null)
                    return type;
            }

            throw new TypeLoadException(fullName);
        }
    }
}
