using Koshi.Core.Memory;
using Koshi.Mcp.Internal;

namespace Koshi.Core.Tests;

public class VaultDocumentTests : IDisposable
{
    private readonly string _tmpDir;

    public VaultDocumentTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "koshi-vaultdoc-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, recursive: true); } catch { /* best-effort */ }
    }

    private string TempFile(string name) => Path.Combine(_tmpDir, name);

    private static MemoryRecord SampleRecord(string id = "mem-000001", string subject = "Sample subject", string content = "Sample body content.")
    {
        return new MemoryRecord
        {
            Id = id,
            Type = MemoryType.Decision,
            Content = content,
            Subject = subject,
            Scope = new MemoryScope("*", "opp", null),
            Source = "user",
            Confidence = 0.9f,
            CreatedAt = new DateTimeOffset(2026, 5, 21, 21, 5, 0, TimeSpan.Zero),
            LastAccessedAt = new DateTimeOffset(2026, 5, 21, 21, 5, 0, TimeSpan.Zero),
            AccessCount = 0,
            Tier = MemoryTier.Hot,
        };
    }

    [Fact]
    public void Write_then_Read_roundtrips_core_fields()
    {
        var path = TempFile("sample.md");
        var rec = SampleRecord();

        VaultDocument.Write(path, rec, otherFrontmatterText: "");

        var read = VaultDocument.Read(path);
        Assert.NotNull(read);
        Assert.Equal(rec.Id, read!.Record.Id);
        Assert.Equal(rec.Type, read.Record.Type);
        Assert.Equal(rec.Subject, read.Record.Subject);
        Assert.Equal(rec.Content, read.Record.Content);
        Assert.Equal(rec.Scope.UserId, read.Record.Scope.UserId);
        Assert.Equal(rec.Scope.WorkspaceId, read.Record.Scope.WorkspaceId);
        Assert.Null(read.Record.Scope.ThreadId);
        Assert.Equal(rec.Source, read.Record.Source);
        Assert.Equal(rec.Confidence, read.Record.Confidence, 0.001f);
        Assert.Equal(rec.Tier, read.Record.Tier);
    }

    [Fact]
    public void Wildcard_user_id_is_quoted_and_roundtrips()
    {
        var path = TempFile("wildcard.md");
        var rec = SampleRecord() with { Scope = new MemoryScope("*", "default", null) };

        VaultDocument.Write(path, rec, "");

        var text = File.ReadAllText(path);
        Assert.Contains("user: \"*\"", text); // must be quoted to avoid YAML anchor interpretation

        var read = VaultDocument.Read(path);
        Assert.Equal("*", read!.Record.Scope.UserId);
    }

    [Fact]
    public void Unknown_top_level_frontmatter_is_preserved_verbatim()
    {
        var path = TempFile("with-unknown.md");
        var rec = SampleRecord();
        var other = "tags: [database, decision]\naliases: [my-alias]\ncssclass: callout\n";

        VaultDocument.Write(path, rec, other);

        var text = File.ReadAllText(path);
        Assert.Contains("tags: [database, decision]", text);
        Assert.Contains("aliases: [my-alias]", text);
        Assert.Contains("cssclass: callout", text);

        // Roundtrip: read and rewrite, ensure unknown keys still there.
        var read = VaultDocument.Read(path);
        Assert.NotNull(read);
        Assert.Equal(other.TrimEnd('\n') + "\n", read!.OtherFrontmatterText);

        VaultDocument.Write(path, read.Record, read.OtherFrontmatterText);
        var text2 = File.ReadAllText(path);
        Assert.Contains("tags: [database, decision]", text2);
        Assert.Contains("aliases: [my-alias]", text2);
        Assert.Contains("cssclass: callout", text2);
    }

    [Fact]
    public void Subject_extracted_from_first_H1()
    {
        var path = TempFile("with-h1.md");
        File.WriteAllText(path,
            "---\n" +
            "koshi:\n" +
            "  id: mem-000007\n" +
            "  type: Fact\n" +
            "  scope:\n" +
            "    user: \"*\"\n" +
            "    workspace: default\n" +
            "    thread: null\n" +
            "  source: user\n" +
            "  confidence: 0.80\n" +
            "  created-at: 2026-05-21T21:05:00Z\n" +
            "  last-accessed-at: 2026-05-21T21:05:00Z\n" +
            "  access-count: 0\n" +
            "  tier: Hot\n" +
            "---\n" +
            "# My great subject\n" +
            "\n" +
            "Body text here.\n");

        var read = VaultDocument.Read(path);
        Assert.NotNull(read);
        Assert.Equal("My great subject", read!.Record.Subject);
        Assert.Equal("Body text here.", read.Record.Content);
    }

    [Fact]
    public void Subject_falls_back_to_filename_when_no_H1()
    {
        var path = TempFile("hello-world--mem-000008.md");
        File.WriteAllText(path,
            "---\n" +
            "koshi:\n" +
            "  id: mem-000008\n" +
            "  type: Fact\n" +
            "  scope:\n" +
            "    user: \"*\"\n" +
            "    workspace: default\n" +
            "    thread: null\n" +
            "  source: user\n" +
            "  confidence: 0.80\n" +
            "  created-at: 2026-05-21T21:05:00Z\n" +
            "  last-accessed-at: 2026-05-21T21:05:00Z\n" +
            "  access-count: 0\n" +
            "  tier: Hot\n" +
            "---\n" +
            "No H1 here.\n");

        var read = VaultDocument.Read(path);
        Assert.NotNull(read);
        Assert.Equal("hello world", read!.Record.Subject);
    }

    [Fact]
    public void Malformed_koshi_block_with_unknown_key_returns_null()
    {
        var path = TempFile("malformed.md");
        File.WriteAllText(path,
            "---\n" +
            "koshi:\n" +
            "  id: mem-000001\n" +
            "  type: Fact\n" +
            "  unknown-koshi-key: oops\n" +
            "  scope:\n" +
            "    user: \"*\"\n" +
            "    workspace: default\n" +
            "    thread: null\n" +
            "  source: user\n" +
            "  confidence: 0.80\n" +
            "  created-at: 2026-05-21T21:05:00Z\n" +
            "  last-accessed-at: 2026-05-21T21:05:00Z\n" +
            "  access-count: 0\n" +
            "  tier: Hot\n" +
            "---\n" +
            "# Body\n");

        var read = VaultDocument.Read(path);
        Assert.Null(read);

        // File untouched on disk — we don't partial-rewrite.
        Assert.Contains("unknown-koshi-key: oops", File.ReadAllText(path));
    }

    [Fact]
    public void File_without_frontmatter_returns_null()
    {
        var path = TempFile("plain.md");
        File.WriteAllText(path, "# Just a note\n\nNo frontmatter at all.\n");

        Assert.Null(VaultDocument.Read(path));
    }

    [Fact]
    public void File_with_frontmatter_but_no_koshi_block_returns_null()
    {
        var path = TempFile("user-note.md");
        File.WriteAllText(path,
            "---\n" +
            "tags: [user, note]\n" +
            "---\n" +
            "# A teammate's note\n");

        Assert.Null(VaultDocument.Read(path));
    }

    [Fact]
    public void Missing_required_keys_returns_null()
    {
        var path = TempFile("missing-keys.md");
        File.WriteAllText(path,
            "---\n" +
            "koshi:\n" +
            "  id: mem-000001\n" +
            "  type: Fact\n" +
            "  scope:\n" +
            "    user: \"*\"\n" +
            "    workspace: default\n" +
            "    thread: null\n" +
            // missing: source, confidence, created-at, last-accessed-at, access-count, tier
            "---\n" +
            "# Body\n");

        Assert.Null(VaultDocument.Read(path));
    }

    [Fact]
    public void Quoted_strings_with_special_chars_roundtrip()
    {
        var path = TempFile("quoted.md");
        var rec = SampleRecord() with
        {
            Scope = new MemoryScope("*", "weird:workspace", "thread:with:colons"),
            Source = "tool: my-extractor",
        };

        VaultDocument.Write(path, rec, "");
        var read = VaultDocument.Read(path);

        Assert.NotNull(read);
        Assert.Equal("weird:workspace", read!.Record.Scope.WorkspaceId);
        Assert.Equal("thread:with:colons", read.Record.Scope.ThreadId);
        Assert.Equal("tool: my-extractor", read.Record.Source);
    }

    [Fact]
    public void Multiline_body_preserved()
    {
        var path = TempFile("multiline.md");
        var rec = SampleRecord(content: "Line one.\nLine two.\nLine three.");

        VaultDocument.Write(path, rec, "");
        var read = VaultDocument.Read(path);

        Assert.NotNull(read);
        Assert.Equal("Line one.\nLine two.\nLine three.", read!.Record.Content);
    }

    [Fact]
    public void Confidence_uses_invariant_culture()
    {
        var path = TempFile("conf.md");
        var rec = SampleRecord() with { Confidence = 0.42f };

        VaultDocument.Write(path, rec, "");
        var text = File.ReadAllText(path);
        Assert.Contains("confidence: 0.42", text);
        Assert.DoesNotContain("confidence: 0,42", text); // not comma-decimal
    }

    [Fact]
    public void Optional_fields_only_emitted_when_set()
    {
        var path1 = TempFile("no-optional.md");
        var path2 = TempFile("with-optional.md");

        VaultDocument.Write(path1, SampleRecord(), "");
        VaultDocument.Write(path2, SampleRecord() with
        {
            SupersededBy = "mem-000099",
            ContradictionNote = "older record disagrees",
        }, "");

        var text1 = File.ReadAllText(path1);
        var text2 = File.ReadAllText(path2);

        Assert.DoesNotContain("superseded-by", text1);
        Assert.DoesNotContain("contradiction-note", text1);
        Assert.Contains("superseded-by: mem-000099", text2);
        Assert.Contains("contradiction-note:", text2);
    }
}
