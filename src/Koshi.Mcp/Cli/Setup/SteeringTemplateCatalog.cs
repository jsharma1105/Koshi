using System.Reflection;

namespace Koshi.Mcp.Cli.Setup;

/// <summary>
/// One of the per-ecosystem Layer-3 steering templates shipped with the tool
/// (#77 Layer 3 / 4). The body is loaded from an embedded resource the
/// `koshi-mcp init` wizard installs into a project's auto-loaded rule
/// file (`.cursorrules`, `.windsurfrules`, `AGENTS.md`,
/// `.github/copilot-instructions.md`).
/// </summary>
internal sealed record SteeringTemplate(
    string Name,                 // logical name, e.g. "AGENTS.md"
    string ResourceName,         // embedded manifest name
    string DestinationRelative)  // path *relative to projectRoot* to install into
{
    public string Read()
    {
        var asm = typeof(SteeringTemplate).Assembly;
        using var stream = asm.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource not found: {ResourceName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

/// <summary>
/// Maps each embedded `templates/koshi.<format>` file to the project-relative
/// path it should be installed into. The list is the single source of truth
/// for the `koshi-mcp init` Layer-4 auto-install step.
///
/// <para>
/// Path conventions (all relative to the resolved project root):
/// </para>
/// <list type="table">
///   <item><term>koshi.AGENTS.md</term>              <description>AGENTS.md</description></item>
///   <item><term>koshi.copilot-instructions.md</term><description>.github/copilot-instructions.md</description></item>
///   <item><term>koshi.cursorrules</term>            <description>.cursorrules</description></item>
///   <item><term>koshi.windsurfrules</term>          <description>.windsurfrules</description></item>
/// </list>
/// </summary>
internal static class SteeringTemplateCatalog
{
    public static IReadOnlyList<SteeringTemplate> Discover()
    {
        var asm = typeof(SteeringTemplateCatalog).Assembly;
        var resources = asm.GetManifestResourceNames();
        var list = new List<SteeringTemplate>(capacity: 4);

        foreach (var resource in resources)
        {
            if (!resource.Contains(".Templates.koshi", StringComparison.Ordinal))
                continue;

            var (logical, relativePath) = MapResourceToDestination(resource);
            if (logical is null || relativePath is null) continue;
            list.Add(new SteeringTemplate(logical, resource, relativePath));
        }

        return list
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static (string? Logical, string? RelativeDest) MapResourceToDestination(string resource)
    {
        // Resource names embed a "Koshi.Mcp.Templates.koshi.<X>" prefix; we
        // only need to look at the last 1-2 dot segments to distinguish them.
        // The .NET resource generator replaces filename dots with '.', so
        //   templates/koshi.AGENTS.md            → ...Templates.koshi.AGENTS.md
        //   templates/koshi.copilot-instructions.md → ...Templates.koshi.copilot-instructions.md
        //   templates/koshi.cursorrules          → ...Templates.koshi.cursorrules
        //   templates/koshi.windsurfrules        → ...Templates.koshi.windsurfrules
        if (resource.EndsWith(".koshi.AGENTS.md", StringComparison.Ordinal))
            return ("AGENTS.md", "AGENTS.md");
        if (resource.EndsWith(".koshi.copilot-instructions.md", StringComparison.Ordinal))
            return (".github/copilot-instructions.md",
                    Path.Combine(".github", "copilot-instructions.md"));
        if (resource.EndsWith(".koshi.cursorrules", StringComparison.Ordinal))
            return (".cursorrules", ".cursorrules");
        if (resource.EndsWith(".koshi.windsurfrules", StringComparison.Ordinal))
            return (".windsurfrules", ".windsurfrules");
        return (null, null);
    }
}
