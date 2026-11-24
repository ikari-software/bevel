using Bevel.App;
using Bevel.App.Supervision;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// bevel-f4g5: health alerts read as a sentence with a way forward — <see cref="BuildStamp.Describe"/>
/// humanizes stamps, <see cref="ShellHealthAlert.Format"/> leads with a plain sentence and keeps the raw
/// detail under "Details:", and a Hold dialog reports the user's Restart/Quit choice.
/// </summary>
public class ShellHealthAlertTests
{
    private const string StampA = "1|1.0.0+1f4cc8f49bf72ef73af5e83fe8984b444c39554c|639262990342066720";
    private const string StampB = "1|1.0.0+32528975eafcc3cb3b5c1bc33eb5869b7f1409ba|639262990342066720";

    [Fact]
    public void Describe_turns_a_stamp_into_version_short_sha_and_build_time()
    {
        var built = new DateTime(639262990342066720L, DateTimeKind.Utc).ToLocalTime()
            .ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal($"1.0.0 (1f4cc8f, built {built})", BuildStamp.Describe(StampA));
    }

    [Fact]
    public void Describe_returns_garbage_unchanged()
    {
        Assert.Equal("not a stamp", BuildStamp.Describe("not a stamp"));
        Assert.Equal("", BuildStamp.Describe(""));
        Assert.Equal("1|1.0.0|noticks", BuildStamp.Describe("1|1.0.0|noticks"));
    }

    [Fact]
    public void Format_for_skew_leads_with_a_sentence_and_hides_the_raw_stamp_below_Details()
    {
        var v = new HealthVerdict(ShellRole.Taskbar, HealthAction.Hold, HealthFault.VersionSkew,
            $"Taskbar stamp '{StampA}' != launcher '{StampB}' after 3 restarts");

        var text = ShellHealthAlert.Format(v);

        // Leads with the plain sentence, not the raw fault/stamp.
        Assert.StartsWith("Bevel was updated while it was running", text);
        // The two builds appear in described form (short SHA, no 40-char hex)...
        Assert.Contains("1.0.0 (1f4cc8f,", text);
        Assert.Contains("1.0.0 (3252897,", text);
        Assert.DoesNotContain("1f4cc8f49bf72ef73af5e83fe8984b444c39554c", text.Split("Details:")[0]);
        // ...and the full raw detail stays under the Details: line for the status file / logs.
        var details = text.Split("Details:\n")[1];
        Assert.Contains(StampA, details);
        Assert.Contains(StampB, details);
    }

    [Fact]
    public async Task A_Hold_reports_the_users_choice()
    {
        var v = new HealthVerdict(ShellRole.Taskbar, HealthAction.Hold, HealthFault.StartupStuck, "stuck");
        var picked = new TaskCompletionSource<HealthAlertChoice>(TaskCreationOptions.RunContinuationsAsynchronously);

        ShellHealthAlert.Show(v,
            onChoice: c => picked.TrySetResult(c),
            native: (_, _) => HealthAlertChoice.Restart);

        var choice = await picked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(HealthAlertChoice.Restart, choice);
    }
}
