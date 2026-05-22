using Koshi.Mcp.Internal;

namespace Koshi.Core.Tests;

public class SlugTests
{
    [Theory]
    [InlineData("Hello World", "hello-world")]
    [InlineData("  Padded  Spaces  ", "padded-spaces")]
    [InlineData("MixedCASE-with-Dashes", "mixedcase-with-dashes")]
    [InlineData("ALLCAPS", "allcaps")]
    [InlineData("snake_case_name", "snake-case-name")]
    [InlineData("with.dots.everywhere", "with-dots-everywhere")]
    [InlineData("numbers 123 stay 456", "numbers-123-stay-456")]
    public void Make_basic_kebab_case(string input, string expected)
    {
        Assert.Equal(expected, Slug.Make(input));
    }

    [Fact]
    public void Make_empty_returns_untitled()
    {
        Assert.Equal("untitled", Slug.Make(""));
        Assert.Equal("untitled", Slug.Make("   "));
        Assert.Equal("untitled", Slug.Make(null));
    }

    [Fact]
    public void Make_only_special_chars_returns_untitled()
    {
        Assert.Equal("untitled", Slug.Make("!@#$%^&*()"));
        Assert.Equal("untitled", Slug.Make("---"));
        Assert.Equal("untitled", Slug.Make("///"));
    }

    [Theory]
    [InlineData("file<name>:with*illegal?chars", "file-name-with-illegal-chars")]
    [InlineData("path/with\\slashes", "path-with-slashes")]
    [InlineData("pipe|in|name", "pipe-in-name")]
    [InlineData("quote\"test", "quote-test")]
    public void Make_strips_windows_illegal_chars(string input, string expected)
    {
        Assert.Equal(expected, Slug.Make(input));
    }

    [Theory]
    [InlineData("CON", "con-note")]
    [InlineData("PRN", "prn-note")]
    [InlineData("aux", "aux-note")]
    [InlineData("NUL", "nul-note")]
    [InlineData("com1", "com1-note")]
    [InlineData("LPT9", "lpt9-note")]
    public void Make_appends_note_to_reserved_windows_names(string input, string expected)
    {
        Assert.Equal(expected, Slug.Make(input));
    }

    [Fact]
    public void Make_caps_at_64_chars()
    {
        var input = new string('a', 100);
        var slug = Slug.Make(input);
        Assert.True(slug.Length <= 64, $"slug length {slug.Length} > 64");
    }

    [Fact]
    public void Make_collapses_dash_runs()
    {
        Assert.Equal("a-b-c", Slug.Make("a    b    c"));
        Assert.Equal("a-b-c", Slug.Make("a---b---c"));
        Assert.Equal("a-b-c", Slug.Make("a!@b#$c"));
    }

    [Fact]
    public void Make_unicode_becomes_dashes_then_collapses()
    {
        // Non-ASCII letters become '-' (we ASCII-only the slug — no ICU dependency for AOT).
        // 'ï' and 'é' are dropped to '-', then collapsed; consecutive '-' become single '-'.
        Assert.Equal("na-ve-title-with", Slug.Make("naïve title with é"));
    }

    [Fact]
    public void Make_trims_leading_and_trailing_dashes()
    {
        Assert.Equal("middle", Slug.Make("---middle---"));
        Assert.Equal("middle", Slug.Make("!!!middle!!!"));
    }

    [Fact]
    public void Make_length_cap_does_not_leave_trailing_dash()
    {
        // 64th char is the start of a dash-run; cap must trim back to letters.
        var input = new string('a', 60) + "----extra";
        var slug = Slug.Make(input);
        Assert.True(slug.Length <= 64);
        Assert.False(slug.EndsWith('-'), $"slug '{slug}' has trailing dash");
    }
}
