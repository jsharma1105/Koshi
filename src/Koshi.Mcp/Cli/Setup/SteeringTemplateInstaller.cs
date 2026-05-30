namespace Koshi.Mcp.Cli.Setup;

internal enum TemplateInstallOutcome
{
    Written,        // file did not exist; whole template body written
    Appended,       // file existed without the Koshi marker; template body appended
    AlreadyPresent, // file existed AND already contained the Koshi marker
    Overwrote,      // file existed and was overwritten (force=true)
    Error,
}

internal sealed record TemplateInstallResult(
    TemplateInstallOutcome Outcome,
    string TargetPath,
    string TemplateName,
    string? ErrorMessage = null);

/// <summary>
/// Installs the embedded Layer-3 steering templates (#77 Layer 4) into a
/// project's auto-loaded rule files. Project-level (not per-client) because
/// the same project may host multiple AI clients in parallel.
///
/// <para>Append-with-marker semantics:</para>
/// <list type="bullet">
///   <item>If the target file does not exist, the full template body is written
///         (<see cref="TemplateInstallOutcome.Written"/>).</item>
///   <item>If the file exists and already contains
///         <see cref="KoshiMarker"/>, the install is skipped
///         (<see cref="TemplateInstallOutcome.AlreadyPresent"/>) — the
///         user's existing snippet is preserved verbatim.</item>
///   <item>If the file exists but does NOT contain the marker, the template
///         body is appended with a leading newline gap
///         (<see cref="TemplateInstallOutcome.Appended"/>). The user's
///         existing rules are preserved.</item>
///   <item>If <paramref name="force"/> is true, the existing file is replaced
///         outright (<see cref="TemplateInstallOutcome.Overwrote"/>).</item>
/// </list>
///
/// <para>
/// Every shipped template contains the literal install marker
/// <c>"koshi-mcp:steering-template:v1"</c> (see <see cref="KoshiMarker"/>),
/// which keeps subsequent <c>koshi-mcp init</c> runs idempotent without
/// depending on file hashes.
/// </para>
/// </summary>
internal static class SteeringTemplateInstaller
{
    /// <summary>
    /// Marker text the installer looks for to decide whether the file already
    /// contains the Koshi steering. Versioned (`:v1`) so future evolutions of
    /// the templates can detect-and-upgrade rather than blindly re-appending.
    /// Embedded in every shipped template as a comment near the top of the
    /// block; do not change lightly — switching the wording silently breaks
    /// idempotency for every existing project.
    /// </summary>
    public const string KoshiMarker = "koshi-mcp:steering-template:v1";

    public static IReadOnlyList<TemplateInstallResult> InstallAll(
        string projectRoot,
        bool force)
    {
        return InstallMatching(projectRoot, force, t => true);
    }

    /// <summary>
    /// Installs only those templates whose logical name matches
    /// <paramref name="predicate"/>. Lets callers gate per detected MCP
    /// client / per existing rule-file so we don't pollute a project with
    /// rule files for clients nobody on the team uses.
    /// </summary>
    public static IReadOnlyList<TemplateInstallResult> InstallMatching(
        string projectRoot,
        bool force,
        Func<SteeringTemplate, bool> predicate)
    {
        var results = new List<TemplateInstallResult>();
        foreach (var tpl in SteeringTemplateCatalog.Discover())
        {
            if (!predicate(tpl)) continue;
            results.Add(InstallOne(tpl, projectRoot, force));
        }
        return results;
    }

    public static TemplateInstallResult InstallOne(
        SteeringTemplate template,
        string projectRoot,
        bool force)
    {
        var target = Path.GetFullPath(Path.Join(projectRoot, template.DestinationRelative));
        var body = template.Read();

        try
        {
            var dir = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            if (!File.Exists(target))
            {
                File.WriteAllText(target, body);
                return new TemplateInstallResult(
                    TemplateInstallOutcome.Written, target, template.Name);
            }

            if (force)
            {
                File.WriteAllText(target, body);
                return new TemplateInstallResult(
                    TemplateInstallOutcome.Overwrote, target, template.Name);
            }

            var existing = File.ReadAllText(target);
            if (existing.Contains(KoshiMarker, StringComparison.Ordinal))
            {
                return new TemplateInstallResult(
                    TemplateInstallOutcome.AlreadyPresent, target, template.Name);
            }

            // Append with a sensible gap so we don't fuse the snippet into a
            // previous block. Preserve any final newline already in the file.
            var needsLeadingNewline = !existing.EndsWith('\n');
            var prefix = needsLeadingNewline ? "\n\n" : "\n";
            File.AppendAllText(target, prefix + body);
            return new TemplateInstallResult(
                TemplateInstallOutcome.Appended, target, template.Name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new TemplateInstallResult(
                TemplateInstallOutcome.Error, target, template.Name, ex.Message);
        }
    }
}
