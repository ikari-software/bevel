using Bevel.Interop;
using Bevel.Interop.Cli;
using Xunit;

namespace Bevel.Core.Tests.Interop;

public class BevelCtlParserTests
{
    private static ParsedCommand Ok(params string[] args)
    {
        var (cmd, err) = BevelCtlParser.Parse(args);
        Assert.Null(err);
        return cmd!;
    }

    private static string Err(params string[] args)
    {
        var (cmd, err) = BevelCtlParser.Parse(args);
        Assert.Null(cmd);
        return err!;
    }

    [Fact]
    public void Reveal_parses_paths_absolutely()
    {
        var cmd = Ok("reveal", "/tmp/a", "/tmp/b");
        Assert.Equal(BevelVerb.Reveal, cmd.Verb);
        Assert.Equal(new[] { "/tmp/a", "/tmp/b" }, cmd.Paths.Select(p => p.Value));
        Assert.False(cmd.NewWindow);
    }

    [Fact]
    public void Reveal_new_window_flag() => Assert.True(Ok("reveal", "/x", "--new-window").NewWindow);

    [Fact]
    public void Open_view_flag_parses() => Assert.Equal(ViewMode.Details, Ok("open", "/x", "--view", "details").View);

    [Fact]
    public void Open_rejects_bad_view() => Assert.Contains("icons|list|details", Err("open", "/x", "--view", "nope"));

    [Fact]
    public void Delete_permanent_flag() => Assert.True(Ok("delete", "/x", "--permanent").Permanent);

    [Fact]
    public void Duplicate_to_target() => Assert.Equal("/dst", Ok("duplicate", "/x", "--to", "/dst").Target!.Value.Value);

    [Fact]
    public void Move_parses_with_to() => Assert.Equal("/dst", Ok("move", "/x", "--to", "/dst").Target!.Value.Value);

    [Fact]
    public void Move_requires_to() => Assert.Contains("--to", Err("move", "/x"));

    [Fact]
    public void Query_windows() => Assert.Equal(QueryKind.Windows, Ok("query", "windows").Query);

    [Fact]
    public void Query_rejects_unknown_target() => Assert.Contains("windows|selection|version", Err("query", "nope"));

    [Fact]
    public void Json_flag_sets_json() => Assert.True(Ok("query", "version", "--json").Json);

    [Fact]
    public void No_args_prints_usage() => Assert.Contains("usage: bevelctl", Err());

    [Fact]
    public void Unknown_command_errors() => Assert.Contains("unknown command 'frobnicate'", Err("frobnicate", "/x"));

    [Fact]
    public void Reveal_without_path_errors() => Assert.Contains("needs at least one path", Err("reveal"));

    [Fact]
    public void Unknown_option_errors() => Assert.Contains("unknown option --wat", Err("reveal", "/x", "--wat"));
}

public class BevelUrlParserTests
{
    private static ParsedCommand Ok(string url)
    {
        var (cmd, err) = BevelUrlParser.Parse(url);
        Assert.Null(err);
        return cmd!;
    }

    private static string Err(string url)
    {
        var (cmd, err) = BevelUrlParser.Parse(url);
        Assert.Null(cmd);
        return err!;
    }

    [Fact]
    public void Reveal_single_path()
    {
        var cmd = Ok("bevel://reveal?path=/tmp/a");
        Assert.Equal(BevelVerb.Reveal, cmd.Verb);
        Assert.Equal("/tmp/a", Assert.Single(cmd.Paths).Value);
    }

    [Fact]
    public void Reveal_multiple_paths()
        => Assert.Equal(new[] { "/a", "/b" }, Ok("bevel://reveal?path=/a&path=/b").Paths.Select(p => p.Value));

    [Fact]
    public void Path_is_percent_decoded()
        => Assert.Equal("/tmp/a b", Assert.Single(Ok("bevel://reveal?path=/tmp/a%20b").Paths).Value);

    [Fact]
    public void Open_with_view()
    {
        var cmd = Ok("bevel://open?path=/x&view=details");
        Assert.Equal(BevelVerb.Open, cmd.Verb);
        Assert.Equal(ViewMode.Details, cmd.View);
    }

    [Fact]
    public void Non_bevel_scheme_errors() => Assert.Contains("not a bevel", Err("https://example.com"));

    [Fact]
    public void Reveal_without_path_errors() => Assert.Contains("needs a path", Err("bevel://reveal"));

    [Fact]
    public void Settings_reports_not_wired() => Assert.Contains("not wired", Err("bevel://settings"));

    [Fact]
    public void Unknown_verb_errors() => Assert.Contains("unknown bevel:// verb", Err("bevel://frobnicate?x=1"));
}
