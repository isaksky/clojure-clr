using System;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using clojure.lang;

const string NamespaceName = "dual.generated";
const string VarName = "answer";
const string FunctionTypeName = "dual.generated.form1_fn";
const string InitTypeName = "dual.generated.__init";

RT.Init();

string mode = args.Length == 0 ? "emit-and-load" : args[0];
string outputPath = args.Length >= 2
    ? Path.GetFullPath(args[1])
    : Path.GetFullPath(Path.Combine("out", "DualGenerationPersisted.dll"));

if (mode == "load")
{
    LoadPersistedAndPrint(outputPath);
    return;
}

Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

var evalMethod = BuildEvalFunction();
int compileTimeValue = (int)evalMethod.Invoke(null, [41])!;
RT.var(NamespaceName, VarName).bindRoot(compileTimeValue);
Console.WriteLine($"eval-value={RT.var(NamespaceName, VarName).deref()}");

SavePersistedAssembly(outputPath);
RT.var(NamespaceName, VarName).bindRoot(-1);
LoadPersistedAndPrint(outputPath);

static MethodInfo BuildEvalFunction()
{
    var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("DualGenerationEval"), AssemblyBuilderAccess.Run);
    var module = assembly.DefineDynamicModule("DualGenerationEval");
    var type = module.DefineType(FunctionTypeName, TypeAttributes.Public | TypeAttributes.Sealed, typeof(object));
    DefineIncrementMethod(type);
    var createdType = type.CreateType();
    return createdType.GetMethod("InvokeStatic", BindingFlags.Public | BindingFlags.Static)!;
}

static void SavePersistedAssembly(string outputPath)
{
    var assemblyName = new AssemblyName("DualGenerationPersisted");
    var assembly = new PersistedAssemblyBuilder(assemblyName, typeof(object).Assembly);
    var module = assembly.DefineDynamicModule(assemblyName.Name!);

    var fnType = module.DefineType(FunctionTypeName, TypeAttributes.Public | TypeAttributes.Sealed, typeof(object));
    var persistedInvoke = DefineIncrementMethod(fnType);

    var initType = module.DefineType(InitTypeName, TypeAttributes.Public | TypeAttributes.Sealed, typeof(object));
    var initialize = initType.DefineMethod("Initialize", MethodAttributes.Public | MethodAttributes.Static, typeof(void), Type.EmptyTypes);
    var il = initialize.GetILGenerator();
    il.Emit(OpCodes.Ldstr, NamespaceName);
    il.Emit(OpCodes.Ldstr, VarName);
    il.Emit(OpCodes.Call, typeof(RT).GetMethod(nameof(RT.var), [typeof(string), typeof(string)])!);
    il.Emit(OpCodes.Ldc_I4, 41);
    il.Emit(OpCodes.Call, persistedInvoke);
    il.Emit(OpCodes.Box, typeof(int));
    il.Emit(OpCodes.Callvirt, typeof(Var).GetMethod(nameof(Var.bindRoot), [typeof(object)])!);
    il.Emit(OpCodes.Ret);

    fnType.CreateType();
    initType.CreateType();
    assembly.Save(outputPath);
    Console.WriteLine($"persisted={outputPath}");
}

static MethodBuilder DefineIncrementMethod(TypeBuilder type)
{
    var method = type.DefineMethod("InvokeStatic", MethodAttributes.Public | MethodAttributes.Static, typeof(int), [typeof(int)]);
    var il = method.GetILGenerator();
    il.Emit(OpCodes.Ldarg_0);
    il.Emit(OpCodes.Ldc_I4_1);
    il.Emit(OpCodes.Add);
    il.Emit(OpCodes.Ret);
    return method;
}

static void LoadPersistedAndPrint(string outputPath)
{
    var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(outputPath);
    assembly.GetType(InitTypeName)!.GetMethod("Initialize", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null);
    Console.WriteLine($"persisted-value={RT.var(NamespaceName, VarName).deref()}");
}
