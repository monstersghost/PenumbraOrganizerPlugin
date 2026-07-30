using System.Reflection;
using PenumbraOrganizer.Plugin.LibraryWork.Pure;

namespace PenumbraOrganizer.Plugin.Tests.LibraryWork;

/// <summary>
/// The background phase runs off the framework thread, where touching Dalamud or Penumbra is
/// undefined behaviour at best. This pins that rule structurally instead of leaving it as a comment
/// somebody edits past later.
///
/// Checks type signatures (fields, properties, constructor and method parameters, return types),
/// not method bodies - catching a static call buried in a body needs IL inspection, which is
/// disproportionate here because every helper the phase calls was already free of both assemblies
/// before this work started. What this does catch is the realistic regression: someone adding an
/// adapter or IDalamudPluginInterface as a field or parameter.
/// </summary>
public class LibraryWorkPurityTests
{
    private const string PureNamespace = "PenumbraOrganizer.Plugin.LibraryWork.Pure";

    [Fact]
    public void PureTypesAndCrossThreadDtos_DoNotReferenceDalamudOrPenumbra()
    {
        var assembly = typeof(ScanProcessor).Assembly;

        var roots = assembly.GetTypes()
            .Where(t => t.Namespace is { } ns
                && (ns == PureNamespace || ns.StartsWith(PureNamespace + ".", StringComparison.Ordinal)))
            .ToList();

        // Guards against the check silently passing because the namespace was renamed or emptied.
        Assert.NotEmpty(roots);

        // The DTOs that cross the thread boundary but live OUTSIDE the Pure namespace. Without
        // these as explicit roots, a Penumbra-typed field on OrganizerModRow or IndexedMod would
        // violate the rule and still pass - which is exactly the regression the rule exists to stop.
        roots.Add(typeof(PenumbraOrganizer.Plugin.Organizer.OrganizerModRow));
        roots.Add(typeof(PenumbraOrganizer.Plugin.LibrarySearch.IndexedMod));

        var violations = new List<string>();
        var visited = new HashSet<Type>();
        var queue = new Queue<Type>(roots);

        while (queue.Count > 0)
        {
            var type = queue.Dequeue();
            if (!visited.Add(type))
                continue;

            foreach (var referenced in SignatureTypes(type))
            {
                if (IsForbidden(referenced))
                {
                    violations.Add($"{type.FullName} references {referenced.FullName} "
                        + $"from {referenced.Assembly.GetName().Name}");
                    continue;
                }

                // Recurse only into our own types; stop at BCL and third-party boundaries so the
                // walk terminates and stays meaningful.
                if (referenced.Assembly == assembly && !visited.Contains(referenced))
                    queue.Enqueue(referenced);
            }
        }

        Assert.Empty(violations.Distinct());
    }

    // Dalamud ships several assembly names beyond the main "Dalamud" one (e.g.
    // Dalamud.Bindings.ImGui, Dalamud.Bindings.ImGuizmo, Dalamud.Bindings.ImPlot, Dalamud.Common),
    // and matching by exact equality misses all of them - a Pure type could gain an ImGuiCol or
    // ImVec2 member and this guard would still pass. StartsWith("Penumbra.Api.") plus the exact
    // "Penumbra.Api" name catches Penumbra's own sub-assemblies the same way, without matching our
    // own "PenumbraOrganizer.Plugin" assembly (which starts with "Penumbra" but not "Penumbra.Api").
    private static bool IsForbidden(Type type)
    {
        var assemblyName = type.Assembly.GetName().Name;
        if (assemblyName is null)
            return false;

        return assemblyName.StartsWith("Dalamud", StringComparison.Ordinal)
            || assemblyName == "Penumbra.Api"
            || assemblyName.StartsWith("Penumbra.Api.", StringComparison.Ordinal);
    }

    private static IEnumerable<Type> SignatureTypes(Type type)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        foreach (var field in type.GetFields(all))
            foreach (var t in Expand(field.FieldType))
                yield return t;

        foreach (var property in type.GetProperties(all))
        {
            foreach (var t in Expand(property.PropertyType))
                yield return t;
            foreach (var indexParameter in property.GetIndexParameters())
                foreach (var t in Expand(indexParameter.ParameterType))
                    yield return t;
        }

        foreach (var constructor in type.GetConstructors(all))
            foreach (var parameter in constructor.GetParameters())
                foreach (var t in Expand(parameter.ParameterType))
                    yield return t;

        foreach (var method in type.GetMethods(all))
        {
            foreach (var t in Expand(method.ReturnType))
                yield return t;
            foreach (var parameter in method.GetParameters())
                foreach (var t in Expand(parameter.ParameterType))
                    yield return t;
        }
    }

    // Unwraps arrays, by-ref, and generic arguments so IReadOnlyList<SomeDalamudType> is caught.
    private static IEnumerable<Type> Expand(Type type)
    {
        if (type.HasElementType && type.GetElementType() is { } element)
        {
            foreach (var t in Expand(element))
                yield return t;
            yield break;
        }

        yield return type;

        if (!type.IsGenericType)
            yield break;

        foreach (var argument in type.GetGenericArguments())
            foreach (var t in Expand(argument))
                yield return t;
    }
}
