namespace Koshi.Mcp.Cli.Setup;

/// <summary>
/// Outcome enum for a single persona-write attempt.
/// </summary>
internal enum PersonaInstallOutcome
{
    Written,
    Overwrote,
    SkippedExists,
    Error,
}

internal sealed record PersonaInstallResult(
    PersonaInstallOutcome Outcome,
    string TargetPath,
    string PersonaName,
    string? ErrorMessage = null);

/// <summary>
/// Pure helper that writes the embedded persona files to a client's agents
/// directory. Mirrors the behaviour of <c>Koshi.Agents.Commands.InstallCommand</c>
/// but with no Spectre / no <c>AnsiConsole.Prompt</c> dependencies — the
/// caller decides skip-vs-overwrite. The <c>koshi-mcp init</c> wizard uses it
/// from <c>InitCommand</c>; the Agents <c>install</c> command keeps its
/// interactive prompts.
///
/// <para>Overwrite policy:</para>
/// <list type="bullet">
///   <item><c>force = true</c> → existing files are silently overwritten.</item>
///   <item><c>force = false</c> + file exists → <see cref="PersonaInstallOutcome.SkippedExists"/>
///         is returned and the caller can decide to re-invoke per-file with
///         <c>force = true</c> after prompting the user.</item>
/// </list>
/// </summary>
internal static class PersonaInstaller
{
    /// <summary>
    /// Install every persona for <paramref name="client"/> into
    /// <paramref name="agentsDir"/>. Creates the directory if needed.
    /// </summary>
    public static IReadOnlyList<PersonaInstallResult> InstallAll(
        PersonaClient client,
        string agentsDir,
        bool force)
    {
        var results = new List<PersonaInstallResult>();
        try
        {
            Directory.CreateDirectory(agentsDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            results.Add(new PersonaInstallResult(
                PersonaInstallOutcome.Error, agentsDir, PersonaName: "(directory)", ex.Message));
            return results;
        }

        foreach (var p in PersonaCatalog.Discover().Where(x => x.Client == client))
        {
            results.Add(InstallOne(p, agentsDir, force));
        }
        return results;
    }

    /// <summary>
    /// Install a single persona. Public so callers can re-invoke with
    /// <c>force=true</c> after prompting the user about an existing file.
    /// </summary>
    public static PersonaInstallResult InstallOne(Persona persona, string agentsDir, bool force)
    {
        var target = Path.Combine(agentsDir, persona.FileName);
        var exists = File.Exists(target);
        if (exists && !force)
            return new PersonaInstallResult(PersonaInstallOutcome.SkippedExists, target, persona.Name);

        try
        {
            File.WriteAllText(target, persona.Read());
            return new PersonaInstallResult(
                exists ? PersonaInstallOutcome.Overwrote : PersonaInstallOutcome.Written,
                target, persona.Name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new PersonaInstallResult(
                PersonaInstallOutcome.Error, target, persona.Name, ex.Message);
        }
    }
}
