using Koshi.Mcp.Cli.Setup;

namespace Koshi.Core.Tests;

/// <summary>
/// Tests for <see cref="PersonaInstaller"/> — the AOT-safe, Spectre-free
/// extraction of the persona-write loop reused by the <c>koshi-mcp init</c>
/// wizard.
/// </summary>
public sealed class PersonaInstallerTests
{
    [Fact]
    public void InstallAll_writes_every_persona_for_client()
    {
        var dir = NewTempDir();
        try
        {
            var results = PersonaInstaller.InstallAll(PersonaClient.Copilot, dir, force: false);
            Assert.NotEmpty(results);
            Assert.All(results, r => Assert.Equal(PersonaInstallOutcome.Written, r.Outcome));

            // Every file actually lives on disk under the requested dir.
            foreach (var r in results)
            {
                Assert.True(File.Exists(r.TargetPath));
                Assert.StartsWith(Path.GetFullPath(dir), Path.GetFullPath(r.TargetPath),
                    StringComparison.OrdinalIgnoreCase);
            }
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void InstallAll_skips_existing_files_without_force()
    {
        var dir = NewTempDir();
        try
        {
            var first = PersonaInstaller.InstallAll(PersonaClient.Copilot, dir, force: false);
            Assert.NotEmpty(first);

            // Pre-modify one file to confirm it's not silently overwritten.
            var sentinel = first[0].TargetPath;
            File.WriteAllText(sentinel, "DO_NOT_OVERWRITE");

            var second = PersonaInstaller.InstallAll(PersonaClient.Copilot, dir, force: false);
            Assert.All(second, r => Assert.Equal(PersonaInstallOutcome.SkippedExists, r.Outcome));
            Assert.Equal("DO_NOT_OVERWRITE", File.ReadAllText(sentinel));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void InstallAll_overwrites_existing_files_with_force()
    {
        var dir = NewTempDir();
        try
        {
            var first = PersonaInstaller.InstallAll(PersonaClient.Copilot, dir, force: false);
            var sentinel = first[0].TargetPath;
            File.WriteAllText(sentinel, "PLACEHOLDER");

            var second = PersonaInstaller.InstallAll(PersonaClient.Copilot, dir, force: true);
            Assert.All(second, r =>
                Assert.True(r.Outcome is PersonaInstallOutcome.Written
                    or PersonaInstallOutcome.Overwrote));
            // The pre-existing sentinel must have been overwritten with the real content.
            Assert.NotEqual("PLACEHOLDER", File.ReadAllText(sentinel));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void InstallAll_creates_target_directory_if_missing()
    {
        var dir = Path.Combine(Path.GetTempPath(),
            "koshi-personas-" + Guid.NewGuid().ToString("N"), "deep", "nested");
        try
        {
            Assert.False(Directory.Exists(dir));
            var results = PersonaInstaller.InstallAll(PersonaClient.Claude, dir, force: false);
            Assert.NotEmpty(results);
            Assert.True(Directory.Exists(dir));
        }
        finally
        {
            // Walk up two levels to delete the root we created.
            var root = Path.GetDirectoryName(Path.GetDirectoryName(dir));
            if (root is not null && Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static string NewTempDir()
    {
        var p = Path.Combine(Path.GetTempPath(), "koshi-personas-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(p);
        return p;
    }
}
