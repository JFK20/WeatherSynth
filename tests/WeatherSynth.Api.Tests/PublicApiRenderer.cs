using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

namespace WeatherSynthApiTests;

/// <summary>
/// Renders the exported surface of an assembly as deterministic text, so that a change to it shows
/// up as a diff in review rather than as a silent break in someone else's build.
///
/// <para>Reflection over <see cref="Assembly.GetExportedTypes"/> rather than a package such as
/// PublicApiGenerator, because the whole of what is needed is about eighty lines and the project
/// carries no dependency it does not need. What matters is only that the output is stable: sorted
/// ordinally at every level, with nothing in it that depends on the order the runtime happens to
/// hand members back in.</para>
/// </summary>
internal static class PublicApiRenderer
{
    public static string Render(params Assembly[] assemblies)
    {
        var text = new StringBuilder();

        foreach (var assembly in assemblies.OrderBy(a => a.GetName().Name, StringComparer.Ordinal))
        {
            text.Append("# ").Append(assembly.GetName().Name).Append('\n');

            foreach (
                var type in assembly
                    .GetExportedTypes()
                    .OrderBy(t => t.FullName, StringComparer.Ordinal)
            )
            {
                text.Append(Header(type)).Append('\n');

                foreach (var member in Members(type))
                    text.Append("    ").Append(member).Append('\n');
            }

            text.Append('\n');
        }

        return text.ToString();
    }

    private static string Header(Type type)
    {
        var parts = new List<string>();

        if (type.IsInterface)
            parts.Add("interface");
        else if (type.IsEnum)
            parts.Add("enum");
        else if (type.IsValueType)
            parts.Add(IsRecord(type) ? "record struct" : "struct");
        else
        {
            if (type.IsAbstract && type.IsSealed)
                parts.Add("static");
            else if (type.IsAbstract)
                parts.Add("abstract");
            else if (type.IsSealed)
                parts.Add("sealed");

            parts.Add(IsRecord(type) ? "record" : "class");
        }

        // Full name, not the short one: moving a type between namespaces is a breaking change for
        // callers and has to show up here as one.
        parts.Add(type.FullName ?? type.Name);

        var bases = new List<string>();
        if (
            type.BaseType is not null
            && type.BaseType != typeof(object)
            && type.BaseType != typeof(ValueType)
        )
            bases.Add(Name(type.BaseType));

        bases.AddRange(type.GetInterfaces().Select(Name).OrderBy(n => n, StringComparer.Ordinal));

        string header = string.Join(' ', parts);
        return bases.Count == 0 ? header : header + " : " + string.Join(", ", bases);
    }

    private static bool IsRecord(Type type) =>
        type.GetMethod("<Clone>$", BindingFlags.Public | BindingFlags.Instance) is not null
        || type.GetMethod(
            "PrintMembers",
            BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly
        )
            is not null;

    private static IEnumerable<string> Members(Type type)
    {
        const BindingFlags Flags =
            BindingFlags.Public
            | BindingFlags.NonPublic
            | BindingFlags.Instance
            | BindingFlags.Static
            | BindingFlags.DeclaredOnly;

        var lines = new List<string>();

        foreach (var member in type.GetMembers(Flags))
        {
            // Compiler-manufactured names (<Clone>$, backing fields) are an implementation detail of
            // records, not something a caller can write. Everything else visible stays.
            if (member.Name.StartsWith('<'))
                continue;

            string? line = member switch
            {
                ConstructorInfo c when IsVisible(c) => Method(c, ".ctor"),
                MethodInfo m when IsVisible(m) && !m.IsSpecialName => Method(m, m.Name),
                PropertyInfo p => Property(p),
                FieldInfo f when IsVisible(f) => Field(f),
                EventInfo e => $"event {Name(e.EventHandlerType!)} {e.Name}",
                _ => null,
            };

            if (line is not null)
                lines.Add(line);
        }

        lines.Sort(StringComparer.Ordinal);
        return lines;
    }

    private static bool IsVisible(MethodBase m) => m.IsPublic || m.IsFamily || m.IsFamilyOrAssembly;

    private static bool IsVisible(FieldInfo f) => f.IsPublic || f.IsFamily || f.IsFamilyOrAssembly;

    private static string Method(MethodBase method, string name)
    {
        string returns = method is MethodInfo m ? Name(m.ReturnType) + " " : "";
        string modifiers = method.IsStatic ? "static " : "";
        string parameters = string.Join(", ", method.GetParameters().Select(Parameter));

        return $"{modifiers}{returns}{name}({parameters})";
    }

    private static string Parameter(ParameterInfo p)
    {
        string name = Name(p.ParameterType);
        return p.HasDefaultValue ? name + " = default" : name;
    }

    private static string? Property(PropertyInfo p)
    {
        var getter = p.GetGetMethod(nonPublic: true);
        var setter = p.GetSetMethod(nonPublic: true);

        bool getVisible = getter is not null && IsVisible(getter);
        bool setVisible = setter is not null && IsVisible(setter);

        if (!getVisible && !setVisible)
            return null;

        var accessors = new List<string>();
        if (getVisible)
            accessors.Add("get;");
        if (setVisible)
            accessors.Add(IsInitOnly(setter!) ? "init;" : "set;");

        bool isStatic = (getter ?? setter)!.IsStatic;
        string indexer = p.GetIndexParameters() is { Length: > 0 } ix
            ? "[" + string.Join(", ", ix.Select(Parameter)) + "]"
            : "";

        return $"{(isStatic ? "static " : "")}{Name(p.PropertyType)} {p.Name}{indexer} "
            + $"{{ {string.Join(' ', accessors)} }}";
    }

    private static bool IsInitOnly(MethodInfo setter) =>
        setter.ReturnParameter.GetRequiredCustomModifiers().Any(t => t == typeof(IsExternalInit));

    private static string Field(FieldInfo f)
    {
        string modifiers =
            f.IsLiteral ? "const "
            : f.IsStatic ? "static "
            : "";
        string readOnly = f.IsInitOnly ? "readonly " : "";

        return $"{modifiers}{readOnly}{Name(f.FieldType)} {f.Name}";
    }

    private static readonly Dictionary<Type, string> Aliases = new()
    {
        [typeof(void)] = "void",
        [typeof(bool)] = "bool",
        [typeof(byte)] = "byte",
        [typeof(int)] = "int",
        [typeof(long)] = "long",
        [typeof(double)] = "double",
        [typeof(string)] = "string",
        [typeof(object)] = "object",
    };

    private static string Name(Type type)
    {
        if (type.IsByRef)
            return "ref " + Name(type.GetElementType()!);

        if (type.IsArray)
            return Name(type.GetElementType()!) + "[]";

        if (Nullable.GetUnderlyingType(type) is { } underlying)
            return Name(underlying) + "?";

        if (Aliases.TryGetValue(type, out string? alias))
            return alias;

        if (type.IsGenericType)
        {
            string stem = type.Name[..type.Name.IndexOf('`')];
            string arguments = string.Join(", ", type.GetGenericArguments().Select(Name));

            return $"{stem}<{arguments}>";
        }

        return type.Name;
    }
}
