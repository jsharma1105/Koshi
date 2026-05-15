using System.Reflection;

namespace Koshi.Agents.Internal;

/// <summary>
/// Represents a single persona shipped by the tool.
/// The bytes are loaded from an embedded resource compiled into the assembly.
/// </summary>
internal sealed record Persona(
    string Name,            // e.g. "koshi-librarian"
    PersonaClient Client,   // Claude or Copilot
    string ResourceName,    // full manifest resource name
    string FileName)        // file name to write to disk (with extension)
{
    public string Read()
    {
        var asm = typeof(Persona).Assembly;
        using var stream = asm.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource not found: {ResourceName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

internal enum PersonaClient
{
    Claude,
    Copilot,
}

internal static class PersonaCatalog
{
    /// <summary>
    /// Discovers all personas embedded in the assembly. Resource names follow
    /// the pattern produced by &lt;EmbeddedResource ... LinkBase="Personas\&lt;client&gt;"&gt;:
    ///   Koshi.Agents.Personas.claude.koshi-librarian.md
    ///   Koshi.Agents.Personas.copilot.koshi-librarian.agent.md
    /// </summary>
    public static IReadOnlyList<Persona> Discover()
    {
        var asm = typeof(PersonaCatalog).Assembly;
        var resources = asm.GetManifestResourceNames();
        var list = new List<Persona>(capacity: resources.Length);

        foreach (var resource in resources)
        {
            // We only care about files under Personas\
            if (!resource.Contains(".Personas.", StringComparison.Ordinal))
            {
                continue;
            }

            PersonaClient client;
            if (resource.Contains(".Personas.claude.", StringComparison.Ordinal))
            {
                client = PersonaClient.Claude;
            }
            else if (resource.Contains(".Personas.copilot.", StringComparison.Ordinal))
            {
                client = PersonaClient.Copilot;
            }
            else
            {
                continue;
            }

            var fileName = ExtractFileName(resource, client);
            var name = ExtractPersonaName(fileName);
            list.Add(new Persona(name, client, resource, fileName));
        }

        return list
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Client)
            .ToArray();
    }

    public static IEnumerable<Persona> ForClient(PersonaClient client) =>
        Discover().Where(p => p.Client == client);

    public static IEnumerable<string> Names() =>
        Discover().Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase);

    // Resource names use dots between path segments but ALSO between filename
    // and extension, so we can't naively split on dots. We work backwards from
    // the known suffixes (.agent.md for copilot, .md for claude) and from the
    // known prefix segment (.Personas.<client>.).
    private static string ExtractFileName(string resource, PersonaClient client)
    {
        var prefixToken = client switch
        {
            PersonaClient.Claude => ".Personas.claude.",
            PersonaClient.Copilot => ".Personas.copilot.",
            _ => throw new ArgumentOutOfRangeException(nameof(client)),
        };

        var idx = resource.IndexOf(prefixToken, StringComparison.Ordinal);
        if (idx < 0)
        {
            throw new InvalidOperationException($"Malformed resource name: {resource}");
        }

        // Everything after the prefix is "<persona>.agent.md" or "<persona>.md".
        // ResourceManager replaces path separators with '.', so a resource like
        //   Koshi.Agents.Personas.copilot.koshi-librarian.agent.md
        // has trailing segments ["koshi-librarian", "agent", "md"]. We collapse
        // those back into a real filename.
        var trailing = resource[(idx + prefixToken.Length)..];

        return client switch
        {
            PersonaClient.Copilot when trailing.EndsWith(".agent.md", StringComparison.Ordinal)
                => trailing,  // already filename-shaped
            PersonaClient.Claude when trailing.EndsWith(".md", StringComparison.Ordinal)
                => trailing,
            _ => trailing,
        };
    }

    private static string ExtractPersonaName(string fileName)
    {
        // Strip extensions (.agent.md or .md) to get the persona name.
        if (fileName.EndsWith(".agent.md", StringComparison.Ordinal))
        {
            return fileName[..^".agent.md".Length];
        }
        if (fileName.EndsWith(".md", StringComparison.Ordinal))
        {
            return fileName[..^".md".Length];
        }
        return fileName;
    }
}
