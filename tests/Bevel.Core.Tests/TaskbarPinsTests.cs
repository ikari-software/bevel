using Bevel.Core;
using Xunit;

namespace Bevel.Core.Tests;

/// <summary>
/// bevel-be86 review: <see cref="TaskbarPins"/> key math — pins are bundle ids only (never paths),
/// an <c>app:</c> prefix is stripped when keying, and the list is capped (count + per-key length).
/// </summary>
public class TaskbarPinsTests
{
    [Theory]
    [InlineData("com.apple.Safari", true)]
    [InlineData("a", true)]
    [InlineData("com.my-app.v2", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("/Applications/Evil.app", false)]   // rooted + .app
    [InlineData("~/evil", false)]                   // home-relative
    [InlineData("C:\\evil", false)]                  // windows rooted + separator
    [InlineData("rel/evil", false)]                  // separator
    [InlineData("evil.app", false)]                  // .app suffix
    [InlineData("..", false)]                        // dot-led
    [InlineData("has space", false)]                 // outside the bundle-id charset
    [InlineData("com.ev-il\u00e9", false)]           // non-ASCII refused
    public void IsValidBundleId_accepts_only_bundle_ids(string? key, bool expected)
        => Assert.Equal(expected, TaskbarPins.IsValidBundleId(key));

    [Fact]
    public void IsValidBundleId_enforces_the_length_cap()
    {
        Assert.True(TaskbarPins.IsValidBundleId(new string('a', TaskbarPins.MaxIdLength)));
        Assert.False(TaskbarPins.IsValidBundleId(new string('a', TaskbarPins.MaxIdLength + 1)));
    }

    [Theory]
    [InlineData("app:com.foo", "com.foo")]   // window-presence id prefix stripped
    [InlineData("  com.foo  ", "com.foo")]   // trimmed
    [InlineData("app:", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void NormalizeKey_strips_the_app_prefix(string? raw, string? expected)
        => Assert.Equal(expected, TaskbarPins.NormalizeKey(raw));

    [Fact]
    public void Sanitize_drops_invalid_keys_dedupes_preserves_order_and_caps_the_count()
    {
        var raw = new[]
        {
            "/evil.app", "com.b", "app:com.a", "com.b", "", "has space",
            new string('x', 600), "com.c",
        };
        Assert.Equal(new[] { "com.b", "com.a", "com.c" }, TaskbarPins.Sanitize(raw));

        var many = Enumerable.Range(0, 200).Select(i => $"com.app{i:D3}");
        var capped = TaskbarPins.Sanitize(many);
        Assert.Equal(TaskbarPins.MaxCount, capped.Length);
        Assert.Equal("com.app127", capped[^1]);

        Assert.Empty(TaskbarPins.Sanitize(null));
    }
}
