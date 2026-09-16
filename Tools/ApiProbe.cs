using System.Reflection;

namespace MapExtract;

/// Dumps the shape of a dependency so we write against what is actually there.
public static class ApiProbe
{
    public static void Run(string filter)
    {
        var asms = new[]
        {
            typeof(DotRecast.Recast.RcConfig).Assembly,
            typeof(DotRecast.Detour.DtNavMesh).Assembly,
        };

        foreach (var asm in asms)
        {
            foreach (var t in asm.GetExportedTypes().OrderBy(t => t.FullName))
            {
                if (t.FullName == null) continue;
                if (filter.Length > 0 && !t.FullName.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;

                Console.WriteLine($"== {t.FullName}{(t.IsEnum ? "  (enum)" : "")}");

                if (t.IsEnum)
                {
                    Console.WriteLine("     " + string.Join(", ", Enum.GetNames(t).Take(14)));
                    continue;
                }

                foreach (var c in t.GetConstructors())
                    Console.WriteLine($"   .ctor({Params(c)})");

                foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static |
                                               BindingFlags.Instance | BindingFlags.DeclaredOnly)
                                   .Where(m => !m.IsSpecialName)
                                   .OrderBy(m => m.Name).Take(24))
                    Console.WriteLine($"   {(m.IsStatic ? "static " : "")}{Short(m.ReturnType)} {m.Name}({Params(m)})");

                foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Take(24))
                    Console.WriteLine($"   field {Short(f.FieldType)} {f.Name}");
            }
        }

        static string Params(MethodBase m) =>
            string.Join(", ", m.GetParameters().Select(p => $"{Short(p.ParameterType)} {p.Name}"));

        static string Short(Type t)
        {
            if (!t.IsGenericType) return t.Name;
            return t.Name[..t.Name.IndexOf('`')] + "<" + string.Join(",", t.GetGenericArguments().Select(Short)) + ">";
        }
    }
}
