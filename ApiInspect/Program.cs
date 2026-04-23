using System;
using System.Linq;
using System.Reflection;

var asm = typeof(lib60870.CS104.Server).Assembly;

// --- 1. CauseOfTransmission enum members ---
Console.WriteLine("=== CauseOfTransmission Enum Members ===");
var cotType = asm.GetType("lib60870.CS101.CauseOfTransmission");
foreach (var name in Enum.GetNames(cotType))
{
    Console.WriteLine($"  {name} = {(int)Enum.Parse(cotType, name)}");
}

// --- 2. StatusAndStatusChangeDetection ---
Console.WriteLine("\n=== StatusAndStatusChangeDetection ===");
var ssdType = asm.GetType("lib60870.CS101.StatusAndStatusChangeDetection");
Console.WriteLine($"  IsClass: {ssdType.IsClass}, IsValueType: {ssdType.IsValueType}");

Console.WriteLine("  Constructors:");
foreach (var ctor in ssdType.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
{
    var ps = string.Join(", ", ctor.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
    Console.WriteLine($"    .ctor({ps})");
}

Console.WriteLine("  Properties:");
foreach (var prop in ssdType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
{
    Console.WriteLine($"    {prop.PropertyType.Name} {prop.Name} {{ {(prop.CanRead?"get;":"")} {(prop.CanWrite?"set;":"")} }}");
}

Console.WriteLine("  Methods:");
foreach (var m in ssdType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
{
    var ps = string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
    Console.WriteLine($"    {m.ReturnType.Name} {m.Name}({ps})");
}

Console.WriteLine("  Fields:");
foreach (var f in ssdType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
{
    Console.WriteLine($"    {f.FieldType.Name} {f.Name} ({(f.IsPublic?"public":"private")})");
}

// --- 3. PackedSinglePointWithSCD ---
Console.WriteLine("\n=== PackedSinglePointWithSCD ===");
var pspType = asm.GetType("lib60870.CS101.PackedSinglePointWithSCD");
Console.WriteLine("  Constructors:");
foreach (var ctor in pspType.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
{
    var ps = string.Join(", ", ctor.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
    Console.WriteLine($"    .ctor({ps})");
}
Console.WriteLine("  Properties:");
foreach (var prop in pspType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
{
    Console.WriteLine($"    {prop.PropertyType.Name} {prop.Name} {{ {(prop.CanRead?"get;":"")} {(prop.CanWrite?"set;":"")} }}");
}
