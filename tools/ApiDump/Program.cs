using System.Reflection;

// Throwaway signature dumper. PowerShell 5.1 cannot load these net9.0 assemblies via
// reflection (it is .NET Framework), so guessing at Vortice overloads costs a build cycle
// each time. This prints ground truth instead.
//
// Currently pointed at the DXGI adapter-selection API, so the dock can pick the integrated
// GPU instead of whatever D3D11CreateDevice(null, ...) happens to hand back.

Assembly dxgi = typeof(Vortice.DXGI.IDXGIDevice).Assembly;
Assembly d3d11 = typeof(Vortice.Direct3D11.ID3D11Device).Assembly;

Dump(dxgi, "Vortice.DXGI.IDXGIFactory6", "EnumAdapterByGpuPreference");
Dump(dxgi, "Vortice.DXGI.IDXGIFactory1", "EnumAdapters1");
Dump(dxgi, "Vortice.DXGI.IDXGIAdapter1", "Description1");
Dump(dxgi, "Vortice.DXGI.AdapterDescription1");
Dump(dxgi, "Vortice.DXGI.DXGI", "CreateDXGIFactory1", "CreateDXGIFactory2");
Dump(d3d11, "Vortice.Direct3D11.D3D11", "D3D11CreateDevice");

Type? pref = dxgi.GetType("Vortice.DXGI.GpuPreference");
Console.WriteLine($"==== Vortice.DXGI.GpuPreference ({(pref is null ? "NOT FOUND" : string.Join(", ", Enum.GetNames(pref)))})");

Type? flag = dxgi.GetType("Vortice.DXGI.AdapterFlags");
Console.WriteLine($"==== Vortice.DXGI.AdapterFlags ({(flag is null ? "NOT FOUND" : string.Join(", ", Enum.GetNames(flag)))})");

static void Dump(Assembly assembly, string typeName, params string[] wanted)
{
    Type? t = assembly.GetType(typeName);
    Console.WriteLine($"==== {typeName} ({(t is null ? "NOT FOUND" : "ok")})");
    if (t is null) return;

    foreach (MemberInfo m in t.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
    {
        if (wanted.Length > 0 && !wanted.Contains(m.Name)) continue;

        if (m is MethodInfo mi && !mi.IsSpecialName)
        {
            string generics = mi.IsGenericMethodDefinition
                ? "<" + string.Join(", ", mi.GetGenericArguments().Select(a => a.Name)) + ">"
                : "";
            string ps = string.Join(", ", mi.GetParameters().Select(p =>
                (p.ParameterType.IsByRef ? (p.IsIn ? "in " : p.IsOut ? "out " : "ref ") : "") +
                Pretty(p.ParameterType) + " " + p.Name));
            Console.WriteLine($"  {Pretty(mi.ReturnType)} {mi.Name}{generics}({ps})");
        }
        else if (m is PropertyInfo pi)
        {
            Console.WriteLine($"  [prop] {Pretty(pi.PropertyType)} {pi.Name} {{ {(pi.CanRead ? "get; " : "")}{(pi.CanWrite ? "set; " : "")}}}");
        }
        else if (m is FieldInfo fi)
        {
            Console.WriteLine($"  [field] {Pretty(fi.FieldType)} {fi.Name}");
        }
    }
}

static string Pretty(Type t)
{
    t = t.IsByRef ? t.GetElementType()! : t;
    if (!t.IsGenericType) return t.Name;
    return t.Name[..t.Name.IndexOf('`')] + "<" + string.Join(", ", t.GetGenericArguments().Select(Pretty)) + ">";
}
