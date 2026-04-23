using System;
using System.Linq;
using System.Reflection;

var asm = Assembly.LoadFrom(@"C:\Users\ltang\.nuget\packages\lib60870.net\2.3.0\lib\netstandard2.0\lib60870.dll");

Console.WriteLine("=== NAMESPACES ===");
foreach (var ns in asm.GetTypes().Where(t => t.IsPublic).Select(t => t.Namespace).Distinct().OrderBy(n => n))
    Console.WriteLine(ns);

Console.WriteLine("\n=== PUBLIC TYPES ===");
foreach (var t in asm.GetTypes().Where(t => t.IsPublic).OrderBy(t => t.FullName))
{
    var kind = t.IsEnum ? "enum" : t.IsInterface ? "interface" : t.IsValueType ? "struct" : t.IsAbstract ? "abstract class" : "class";
    Console.WriteLine($"\n--- {kind} {t.FullName} ---");
    if (t.BaseType != null && t.BaseType != typeof(object) && t.BaseType != typeof(ValueType) && t.BaseType != typeof(Enum))
        Console.WriteLine($"  Base: {t.BaseType.FullName}");
    
    foreach (var iface in t.GetInterfaces().Where(i => i.IsPublic))
        Console.WriteLine($"  Implements: {iface.FullName}");

    if (t.IsEnum)
    {
        foreach (var v in Enum.GetNames(t))
            Console.WriteLine($"  {v}");
        continue;
    }

    foreach (var c in t.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
    {
        var parms = string.Join(", ", c.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
        Console.WriteLine($"  ctor({parms})");
    }

    foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly).OrderBy(m => m.Name))
    {
        var parms = string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
        var stat = m.IsStatic ? "static " : "";
        Console.WriteLine($"  {stat}{m.ReturnType.Name} {m.Name}({parms})");
    }

    foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly).OrderBy(p => p.Name))
    {
        var get = p.CanRead ? "get;" : "";
        var set = p.CanWrite ? "set;" : "";
        Console.WriteLine($"  property {p.PropertyType.Name} {p.Name} {{ {get} {set} }}");
    }

    foreach (var e in t.GetEvents(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly).OrderBy(e => e.Name))
    {
        Console.WriteLine($"  event {e.EventHandlerType.Name} {e.Name}");
    }
}
