using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;

if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: AssemblyInspector <assembly-path> [resolver-dir ...]");
    Environment.Exit(2);
}

string assemblyPath = Path.GetFullPath(args[0]);
string[] resolverDirs = args.Skip(1).Select(Path.GetFullPath).ToArray();

AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    foreach (string dir in resolverDirs)
    {
        string candidate = Path.Combine(dir, name.Name + ".dll");
        if (File.Exists(candidate))
            return AssemblyLoadContext.Default.LoadFromAssemblyPath(candidate);
    }

    return null;
};

var name = AssemblyName.GetAssemblyName(assemblyPath);
Assembly assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);
Console.WriteLine($"Assembly: {name.FullName}");
Console.WriteLine("References:");
foreach (AssemblyName reference in assembly.GetReferencedAssemblies())
    Console.WriteLine($"  {reference.FullName}");

Console.WriteLine("Types:");
foreach (Type type in assembly.GetTypes().OrderBy(t => t.FullName))
{
    string baseName = type.BaseType?.FullName ?? "<none>";
    string interfaces = string.Join(", ", type.GetInterfaces().Select(t => t.FullName).OrderBy(s => s));
    Console.WriteLine($"  {type.FullName}");
    Console.WriteLine($"    Base: {baseName}");
    if (interfaces.Length > 0)
        Console.WriteLine($"    Interfaces: {interfaces}");

    foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        Console.WriteLine($"    Field: {field.Attributes} {field.FieldType.FullName} {field.Name}");

    foreach (ConstructorInfo ctor in type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        Console.WriteLine($"    Ctor: {ctor.Attributes} ({string.Join(", ", ctor.GetParameters().Select(p => p.ParameterType.FullName))})");

    foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly).OrderBy(m => m.Name))
    {
        string parameters = string.Join(", ", method.GetParameters().Select(p => p.ParameterType.FullName));
        Console.WriteLine($"    Method: {method.Attributes} {method.ReturnType.FullName} {method.Name}({parameters})");
    }
}
