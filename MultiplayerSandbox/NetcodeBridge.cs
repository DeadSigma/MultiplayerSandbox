using System;
using System.Reflection;
using HarmonyLib;

namespace MultiplayerTestHarness
{
    internal static class NetcodeBridge
    {
        static Type _writerType;
        static Type _readerType;
        static Type _allocatorType;
        static object _tempAllocator;
        static MethodInfo _deserializeMethod;

        static Type WriterType => _writerType ?? (_writerType = FindType("Unity.Netcode.FastBufferWriter"));
        static Type ReaderType => _readerType ?? (_readerType = FindType("Unity.Netcode.FastBufferReader"));
        static Type AllocatorType => _allocatorType ?? (_allocatorType = FindType("Unity.Collections.Allocator"));

        static object TempAllocator
        {
            get
            {
                if (_tempAllocator == null)
                    _tempAllocator = Enum.Parse(AllocatorType, "Temp");

                return _tempAllocator;
            }
        }

        internal static byte[] Serialize(Message message)
        {
            if (message == null)
                return null;

            object writer = CreateWriter();

            try
            {
                MethodInfo serialize = FindSerializeFast(message.GetType());
                serialize.Invoke(message, new[] { writer });

                MethodInfo toArray = WriterType.GetMethod(
                    "ToArray",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null,
                    Type.EmptyTypes,
                    null);

                if (toArray == null)
                    throw new MissingMethodException("FastBufferWriter.ToArray");

                return (byte[])toArray.Invoke(writer, null);
            }
            finally
            {
                Dispose(writer);
            }
        }

        internal static Message Deserialize(byte[] payload)
        {
            if (payload == null || payload.Length == 0)
                return null;

            object reader = CreateReader(payload);

            try
            {
                MethodInfo tryBeginRead = ReaderType.GetMethod(
                    "TryBeginRead",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null,
                    new[] { typeof(int) },
                    null);

                if (tryBeginRead != null)
                {
                    object result = tryBeginRead.Invoke(reader, new object[] { payload.Length });

                    if (result is bool && !(bool)result)
                        throw new InvalidOperationException("message buffer is not readable");
                }

                MethodInfo deserialize = GetDeserializeMethod();
                return deserialize.Invoke(null, new[] { reader }) as Message;
            }
            finally
            {
                Dispose(reader);
            }
        }

        static object CreateWriter()
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
                {
                    return constructors[i].Invoke(
                        new object[] { 256, TempAllocator, 10485760 });
                }
            }

            for (int i = 0; i < constructors.Length; i++)
            {
                ParameterInfo[] p = constructors[i].GetParameters();

                if (p.Length == 2 &&
                    p[0].ParameterType == typeof(int) &&
                    p[1].ParameterType == AllocatorType)
                {
                    return constructors[i].Invoke(
                        new object[] { 256, TempAllocator });
                }
            }

            throw new MissingMethodException("FastBufferWriter constructor");
        }

        static object CreateReader(byte[] payload)
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
                {
                    return constructors[i].Invoke(
                        new object[] { payload, TempAllocator, payload.Length, 0 });
                }
            }

            for (int i = 0; i < constructors.Length; i++)
            {
                ParameterInfo[] p = constructors[i].GetParameters();

                if (p.Length == 3 &&
                    p[0].ParameterType == typeof(byte[]) &&
                    p[1].ParameterType == AllocatorType &&
                    p[2].ParameterType == typeof(int))
                {
                    return constructors[i].Invoke(
                        new object[] { payload, TempAllocator, payload.Length });
                }
            }

            throw new MissingMethodException("FastBufferReader constructor");
        }

        static MethodInfo FindSerializeFast(Type messageType)
        {
            Type current = messageType;

            while (current != null)
            {
                MethodInfo[] methods = current.GetMethods(
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic |
                    BindingFlags.DeclaredOnly);

                for (int i = 0; i < methods.Length; i++)
                {
                    if (methods[i].Name != "SerializeFast")
                        continue;

                    ParameterInfo[] p = methods[i].GetParameters();

                    if (p.Length == 1 && p[0].ParameterType == WriterType)
                        return methods[i];
                }

                current = current.BaseType;
            }

            throw new MissingMethodException(messageType.FullName, "SerializeFast");
        }

        static MethodInfo GetDeserializeMethod()
        {
            if (_deserializeMethod != null)
                return _deserializeMethod;

            Type deserializerType = FindType("FastMessageDeserializer");

            MethodInfo[] methods = deserializerType.GetMethods(
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo method = methods[i];

                if (method.Name != "DeserializeMessage" ||
                    !method.IsGenericMethodDefinition)
                    continue;

                ParameterInfo[] p = method.GetParameters();

                if (p.Length == 1 && p[0].ParameterType == ReaderType)
                {
                    _deserializeMethod = method.MakeGenericMethod(typeof(Message));
                    return _deserializeMethod;
                }
            }

            throw new MissingMethodException("FastMessageDeserializer.DeserializeMessage");
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

        static void Dispose(object instance)
        {
            if (instance == null)
                return;

            MethodInfo dispose = instance.GetType().GetMethod(
                "Dispose",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                Type.EmptyTypes,
                null);

            if (dispose != null)
                dispose.Invoke(instance, null);
        }
    }
}
