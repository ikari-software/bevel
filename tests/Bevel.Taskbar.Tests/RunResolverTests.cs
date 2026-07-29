using System;
using System.IO;
using Bevel.Taskbar;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>bevel-x6pv: the Run dialog resolves its input to /usr/bin/open arguments — a URL opens
/// verbatim, an existing path opens directly, anything else is an app name. Pure logic, no launching.</summary>
public class RunResolverTests
{
    [Fact]
    public void Empty_or_whitespace_input_resolves_to_null()
    {
        Assert.Null(RunResolver.Resolve(null));
        Assert.Null(RunResolver.Resolve("   "));
    }

    [Fact]
    public void A_url_opens_verbatim()
    {
        Assert.Equal(new[] { "https://example.com" }, RunResolver.Resolve("https://example.com"));
        Assert.Equal(new[] { "x-apple.systempreferences:com.apple.Network-Settings.extension" },
            RunResolver.Resolve("x-apple.systempreferences:com.apple.Network-Settings.extension"));
    }

    [Fact]
    public void A_scheme_without_slashes_is_treated_as_a_url_not_an_app()
    {
        Assert.Equal(new[] { "mailto:hi@bevel.test" }, RunResolver.Resolve("mailto:hi@bevel.test"));
    }

    [Fact]
    public void An_existing_path_opens_directly()
    {
        // "/" always exists; it must open as a path, not be treated as an app name.
        Assert.Equal(new[] { "/" }, RunResolver.Resolve("/"));
    }

    [Fact]
    public void A_leading_tilde_expands_to_home()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(new[] { home }, RunResolver.Resolve("~"));
    }

    [Fact]
    public void An_unknown_bare_name_is_launched_as_an_app()
    {
        Assert.Equal(new[] { "-a", "Calculator" }, RunResolver.Resolve("Calculator"));
    }
}
