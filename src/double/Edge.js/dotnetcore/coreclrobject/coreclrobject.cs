using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

public partial class CoreCLREmbedding
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct ObjectDescriptionContext
    {
        public long ObjectId;

        public IntPtr ObjectDescription;
    }

    private static readonly JavascriptObjectRepository repository = new JavascriptObjectRepository();

    private static Assembly GetAssembly(string assemblyFile)
    {
        Assembly assembly;

        if (assemblyFile.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || assemblyFile.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            if (!Path.IsPathRooted(assemblyFile))
            {
                assemblyFile = Path.Combine(Directory.GetCurrentDirectory(), assemblyFile);
                Resolver.AddAssemblyPath(Directory.GetCurrentDirectory());
            }
            else
            {
                Resolver.AddAssemblyPath(Path.GetDirectoryName(assemblyFile));
            }

            assembly = LoadContext.LoadFromAssemblyPath(assemblyFile);
        }

        else
        {
            assembly = Assembly.Load(new AssemblyName(assemblyFile));
        }

        return assembly;
    }

    [SecurityCritical]
    public static IntPtr CreateObject(string assemblyFile, string typeName, IntPtr exception)
    {
        try
        {
            Marshal.WriteIntPtr(exception, IntPtr.Zero);


            Assembly assembly = GetAssembly(assemblyFile);
            DebugMessage("CoreCLREmbedding::CreateObject (CLR) - Assembly {0} loaded successfully", assemblyFile);

            var instance = CreateInstance(assembly, typeName);
            var jsObject = repository.Register(instance);
            DebugMessage("CoreCLREmbedding::CreateObject (CLR) - {0} loaded successfully", typeName);

            var context = new ObjectDescriptionContext();
            context.ObjectId = jsObject.Id;
            context.ObjectDescription = Marshal.StringToHGlobalAnsi(JsonSerializer.Serialize(jsObject));

            return GCHandle.ToIntPtr(GCHandle.Alloc(context));
        }

        catch (Exception e)
        {
            DebugMessage("CoreCLREmbedding::GetFunc (CLR) - Exception was thrown: {0}{1}{2}", e.Message, Environment.NewLine, e.StackTrace);

            V8Type v8Type;
            Marshal.WriteIntPtr(exception, MarshalCLRToV8(e, out v8Type));

            return IntPtr.Zero;
        }
    }

    [SecurityCritical]
    public static void CallObjectFunc(IntPtr objectContextHandle, string functionName, IntPtr payload, int payloadType, IntPtr taskState, IntPtr result, IntPtr resultType)
    {
        try
        {
            DebugMessage("CoreCLREmbedding::CallObjectFunc (CLR) - Starting");

            GCHandle contextHandle = GCHandle.FromIntPtr(objectContextHandle);
            var context = (ObjectDescriptionContext)contextHandle.Target;

            DebugMessage("CoreCLREmbedding::CallObjectFunc (CLR) - Marshalling data of type {0} and calling the .NET method", ((V8Type)payloadType).ToString("G"));

            var callMethodResult = repository.TryCallMethod(context.ObjectId, functionName, MarshalV8ToCLRArray(payload, (V8Type)payloadType));

            if (callMethodResult.Success)
            {
                Marshal.WriteInt32(taskState, (int)TaskStatus.RanToCompletion);
                DebugMessage("CoreCLREmbedding::CallObjectFunc (CLR) - .NET method ran synchronously, marshalling data for V8");

                V8Type taskResultType;
                IntPtr marshalData = MarshalCLRToV8(callMethodResult.ReturnValue, out taskResultType);

                DebugMessage("CoreCLREmbedding::CallObjectFunc (CLR) - Method return data is of type {0}", taskResultType.ToString("G"));

                Marshal.WriteIntPtr(result, marshalData);
                Marshal.WriteInt32(resultType, (int)taskResultType);
            }
            else
            {
                Marshal.WriteInt32(taskState, (int)TaskStatus.Faulted);
                DebugMessage("CoreCLREmbedding::CallObjectFunc (CLR) - .NET method ran synchronously and faulted, marshalling exception data for V8");

                V8Type taskExceptionType;

                Marshal.WriteIntPtr(result, MarshalCLRToV8(callMethodResult.Exception, out taskExceptionType));
                Marshal.WriteInt32(resultType, (int)V8Type.Exception);
            }

            DebugMessage("CoreCLREmbedding::CallObjectFunc (CLR) - Finished");
        }

        catch (Exception e)
        {
            DebugMessage("CoreCLREmbedding::CallObjectFunc (CLR) - Exception was thrown: {0}{1}{2}", e.Message, Environment.NewLine, e.StackTrace);

            V8Type v8Type;

            Marshal.WriteIntPtr(result, MarshalCLRToV8(e, out v8Type));
            Marshal.WriteInt32(resultType, (int)v8Type);
            Marshal.WriteInt32(taskState, (int)TaskStatus.Faulted);
        }
    }

    private static object CreateInstance(Assembly assembly, string typeName)
    {
        Type startupType = assembly.GetType(typeName);

        if (startupType == null)
        {
            throw new TypeLoadException("Could not load type '" + typeName + "'");
        }

        return Activator.CreateInstance(startupType);
    }
}

public class ClrObjectReflectionWrap
{
    object instance;

    public static ClrObjectReflectionWrap Create(Assembly assembly, string typeName)
    {
        Type startupType = assembly.GetType(typeName);

        if (startupType == null)
        {
            throw new TypeLoadException("Could not load type '" + typeName + "'");
        }

        ClrObjectReflectionWrap wrap = new ClrObjectReflectionWrap();
        wrap.instance = Activator.CreateInstance(startupType);
        return wrap;
    }
};
