using System.Globalization;
using SummerSync.Core.Players;

namespace SummerSync.Core.Tests.Players;

public class PoseSettingsTests
{
    [Fact]
    public void Without_a_file_every_value_is_the_standard_one()
    {
        var settings = PoseSettings.Parse(null!);

        Assert.Equal(PoseSettings.CrouchHips.Standard, settings[PoseSettings.CrouchHips]);
        Assert.Equal(0f, settings.Head(3, 0));
        Assert.Equal("# SummerSync: how the people move and where their heads look (character window, F7).\n", settings.Write());
    }

    [Fact]
    public void What_was_tuned_is_read_back_the_same()
    {
        var settings = new PoseSettings();
        settings.Set(PoseSettings.RunLean, 17.5f);
        settings.Set(PoseSettings.CrouchHips, 0.4f);
        settings.SetHead(9, 0, -12f);
        settings.SetHead(9, 2, 3.25f);

        var read = PoseSettings.Parse(settings.Write());

        Assert.Equal(17.5f, read[PoseSettings.RunLean]);
        Assert.Equal(0.4f, read[PoseSettings.CrouchHips]);
        Assert.Equal(-12f, read.Head(9, 0));
        Assert.Equal(0f, read.Head(9, 1));
        Assert.Equal(3.25f, read.Head(9, 2));
        Assert.Equal(0, read.Serial);
    }

    [Fact]
    public void A_value_out_of_range_is_kept_within_it_and_junk_is_left_out()
    {
        var read = PoseSettings.Parse("crouch.hips=9\r\nrun.lean=-4\nhead.2.yaw=200\nhead.x.yaw=3\nhead.2.tilt=3\nrun.knee=abc\nnothing.here=1\n# a comment\n=4\nrun.arms = 50 \n");

        Assert.Equal(PoseSettings.CrouchHips.Most, read[PoseSettings.CrouchHips]);
        Assert.Equal(PoseSettings.RunLean.Least, read[PoseSettings.RunLean]);
        Assert.Equal(PoseSettings.HeadTune, read.Head(2, 0));
        Assert.Equal(PoseSettings.RunKnee.Standard, read[PoseSettings.RunKnee]);
        Assert.Equal(50f, read[PoseSettings.RunArms]);
        Assert.DoesNotContain("nothing", read.Write());
    }

    [Fact]
    public void Changes_are_counted_and_a_reset_brings_the_standard_back()
    {
        var settings = new PoseSettings();
        settings.Set(PoseSettings.RunArms, 60f);
        settings.Set(PoseSettings.RunArms, 60f);
        Assert.Equal(1, settings.Serial);

        settings.Reset(PoseSettings.Groups[2]);
        Assert.Equal(PoseSettings.RunArms.Standard, settings[PoseSettings.RunArms]);
        Assert.Equal(2, settings.Serial);

        settings.SetHead(4, 1, 5f);
        settings.ResetHead(4);
        Assert.Equal(0f, settings.Head(4, 1));
    }

    [Fact]
    public void Numbers_are_written_the_same_whatever_the_culture()
    {
        CultureInfo before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("es-AR");
            var settings = new PoseSettings();
            settings.Set(PoseSettings.CrouchFeet, 0.25f);
            Assert.Contains("crouch.feet=0.25", settings.Write());
            Assert.Equal(0.25f, PoseSettings.Parse(settings.Write())[PoseSettings.CrouchFeet]);
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    [Fact]
    public void Every_value_has_a_standard_within_its_range_and_a_key_of_its_own()
    {
        var keys = new HashSet<string>();
        foreach (PoseValue[] group in PoseSettings.Groups)
        {
            foreach (PoseValue value in group)
            {
                Assert.InRange(value.Standard, value.Least, value.Most);
                Assert.True(keys.Add(value.Key), value.Key);
                Assert.Same(value, PoseSettings.Find(value.Key));
            }
        }
    }
}
