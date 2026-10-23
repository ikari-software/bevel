using System.Collections.Generic;
using Bevel.Core.Components;
using Xunit;

namespace Bevel.Core.Tests;

/// <summary>
/// bevel-aqr7 Task 3: per-instance settings. The SCHEMA belongs to the type; the VALUES belong to
/// the instance, so two instances of one type hold independent values.
/// </summary>
public class ComponentInstanceTests
{
    private static readonly ComponentManifest ClockType = new(
        "run.bevel.clock", 1, "Clock", "", MultiInstance: true, ComponentSizing.Content, null,
        new[]
        {
            new ComponentSettingsField("showSeconds", ComponentFieldKind.Bool, "Show seconds", "false", null, null),
            new ComponentSettingsField("tzOffset", ComponentFieldKind.Int, "Offset", "0", null, (-12, 14)),
        },
        new ComponentPrimitive[] { new LabelPrimitive("time", "Time") });

    private static ComponentInstance Inst(params (string, string)[] kv)
    {
        var d = new Dictionary<string, string>();
        foreach (var (k, v) in kv) d[k] = v;
        return new ComponentInstance(ComponentInstance.NewId(), "run.bevel.clock", d, Visible: true);
    }

    [Fact]
    public void Two_instances_of_one_type_hold_independent_values()
    {
        var a = Inst(("showSeconds", "true"));
        var b = Inst(("showSeconds", "false"));
        Assert.Equal("true", a.ReadSetting(ClockType, "showSeconds"));
        Assert.Equal("false", b.ReadSetting(ClockType, "showSeconds"));
        Assert.NotEqual(a.InstanceId, b.InstanceId);
    }

    [Fact]
    public void A_missing_value_falls_back_to_the_schema_default()
        => Assert.Equal("false", Inst().ReadSetting(ClockType, "showSeconds"));

    // Review Focus 2: a value that no longer parses must not throw or kill the instance.
    [Fact]
    public void An_unparseable_value_falls_back_to_the_default_instead_of_throwing()
        => Assert.Equal("0", Inst(("tzOffset", "abc")).ReadSetting(ClockType, "tzOffset"));

    [Fact]
    public void An_out_of_range_value_falls_back_to_the_default()
        => Assert.Equal("0", Inst(("tzOffset", "99")).ReadSetting(ClockType, "tzOffset"));

    [Fact]
    public void A_key_the_schema_does_not_declare_reads_as_empty_rather_than_throwing()
        => Assert.Equal("", Inst(("bogus", "x")).ReadSetting(ClockType, "bogus"));

    [Fact]
    public void NewId_produces_distinct_ids()
        => Assert.NotEqual(ComponentInstance.NewId(), ComponentInstance.NewId());

    /// <summary>
    /// Whole-branch review Fix 7: <c>{"instanceId":"a","typeId":"x","visible":true}</c> is valid
    /// JSON with no <c>"settings"</c> key, so System.Text.Json constructs this record with
    /// <c>Settings = null</c> and throws no <see cref="System.Text.Json.JsonException"/> — the
    /// corrupt-path guard in <c>SettingsService</c> never fires for it. <see cref="ComponentInstance.Settings"/>
    /// now coalesces null to empty AT INIT TIME, so deserializing this shape must produce an empty
    /// (not null) dictionary, and <see cref="ComponentInstance.DeepClone"/> — whose
    /// <c>new Dictionary&lt;string,string&gt;(Settings)</c> previously threw
    /// <see cref="ArgumentNullException"/> on exactly this input — must not throw.
    /// </summary>
    [Fact]
    public void Deserializing_JSON_missing_the_settings_key_normalizes_to_an_empty_dictionary()
    {
        const string json = """{"instanceId":"a","typeId":"x","visible":true}""";
        var inst = System.Text.Json.JsonSerializer.Deserialize<ComponentInstance>(json)!;

        Assert.NotNull(inst.Settings);
        Assert.Empty(inst.Settings);

        var clone = inst.DeepClone();   // must not throw ArgumentNullException
        Assert.NotNull(clone.Settings);
        Assert.Empty(clone.Settings);
    }

    [Fact]
    public void A_null_settings_argument_passed_directly_also_normalizes_to_empty()
    {
        var inst = new ComponentInstance("a", "x", null!, Visible: true);

        Assert.NotNull(inst.Settings);
        Assert.Empty(inst.Settings);
        Assert.NotNull(inst.DeepClone().Settings);   // must not throw
    }

    // ── ComponentSettingsField.TryRead: the branches ReadSetting above does not reach ──────────
    // Task 2 shipped TryRead with no direct tests. The cases above cover Bool-success, absent,
    // Int-unparseable and Int-out-of-range; the Enum branch was entirely uncovered — including
    // `AllowedValues is null`, which is the one that would throw if the `Contains` were ever
    // refactored. TryRead is the public parsing contract third-party components depend on, so
    // every branch gets a case here.

    private static ComponentSettingsField Field(
        ComponentFieldKind kind, string @default,
        IReadOnlyList<string>? allowed = null, (int Min, int Max)? range = null)
        => new("f", kind, "F", @default, allowed, range);

    [Fact]
    public void TryRead_accepts_a_declared_enum_value()
    {
        var f = Field(ComponentFieldKind.Enum, "small", new[] { "small", "large" });
        Assert.True(f.TryRead("large", out var v));
        Assert.Equal("large", v);
    }

    [Fact]
    public void TryRead_rejects_an_enum_value_outside_AllowedValues()
    {
        var f = Field(ComponentFieldKind.Enum, "small", new[] { "small", "large" });
        Assert.False(f.TryRead("huge", out var v));
        Assert.Equal("small", v);
    }

    [Fact]
    public void TryRead_does_not_throw_when_an_enum_field_declares_no_AllowedValues()
    {
        var f = Field(ComponentFieldKind.Enum, "small", allowed: null);
        Assert.False(f.TryRead("anything", out var v));   // must not NullReference
        Assert.Equal("small", v);
    }

    [Fact]
    public void TryRead_rejects_an_unparseable_bool_and_keeps_the_default()
    {
        var f = Field(ComponentFieldKind.Bool, "true");
        Assert.False(f.TryRead("yes", out var v));
        Assert.Equal("true", v);
    }

    // A comma-decimal locale must not change how an int field reads, and "1.5" must fail rather
    // than silently truncate.
    [Fact]
    public void TryRead_parses_ints_culture_independently()
    {
        var f = Field(ComponentFieldKind.Int, "0");
        Assert.True(f.TryRead("1234", out var v));
        Assert.Equal("1234", v);
        Assert.False(f.TryRead("1.5", out var frac));
        Assert.Equal("0", frac);
    }

    [Theory]
    [InlineData(ComponentFieldKind.String)]
    [InlineData(ComponentFieldKind.Path)]
    public void TryRead_passes_string_and_path_values_through(ComponentFieldKind kind)
    {
        var f = Field(kind, "");
        Assert.True(f.TryRead("/some/value", out var v));
        Assert.Equal("/some/value", v);
    }

    [Theory]
    [InlineData(ComponentFieldKind.Bool)]
    [InlineData(ComponentFieldKind.Int)]
    [InlineData(ComponentFieldKind.Enum)]
    [InlineData(ComponentFieldKind.String)]
    [InlineData(ComponentFieldKind.Path)]
    public void TryRead_leaves_the_default_in_the_out_parameter_on_every_false_return(ComponentFieldKind kind)
    {
        var f = Field(kind, "DEFAULT", allowed: Array.Empty<string>());
        if (!f.TryRead(null, out var v)) Assert.Equal("DEFAULT", v);
    }
}
