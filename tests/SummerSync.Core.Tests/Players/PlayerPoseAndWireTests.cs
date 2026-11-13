using SummerSync.Core.Net;
using SummerSync.Core.Net.Wire;
using SummerSync.Core.Players;
using SummerSync.Core.Vehicles;

namespace SummerSync.Core.Tests.Players;

public class PlayerPoseTests
{
    [Theory]
    [InlineData(350f, 10f, 0.5, 0f)]
    [InlineData(10f, 350f, 0.5, 0f)]
    [InlineData(350f, 10f, 0.25, 355f)]
    [InlineData(90f, 180f, 0.5, 135f)]
    [InlineData(0f, 180f, 0.0, 0f)]
    [InlineData(20f, 20f, 0.7, 20f)]
    public void The_yaw_turns_the_short_way_round(float from, float to, double t, float expected)
    {
        Assert.Equal(expected, PlayerPose.LerpAngle(from, to, t), 3);
    }

    [Fact]
    public void The_yaw_never_comes_out_as_360()
    {
        for (int i = 0; i <= 1000; i++)
        {
            float angle = PlayerPose.LerpAngle(359.9999f, 0.0001f, i / 1000.0);
            Assert.True(angle >= 0f && angle < 360f, "angle " + angle);
        }
    }

    [Fact]
    public void A_pose_part_way_between_two_others()
    {
        var a = new PlayerPose(0, 0, 0, 0, -10, 1.4f);
        var b = new PlayerPose(10, 2, -4, 90, 30, 0.4f);

        PlayerPose middle = PlayerPose.Lerp(a, b, 0.25);

        Assert.Equal(2.5f, middle.X, 5);
        Assert.Equal(0.5f, middle.Y, 5);
        Assert.Equal(-1f, middle.Z, 5);
        Assert.Equal(22.5f, middle.Yaw, 4);
        Assert.Equal(0f, middle.Pitch, 4);
        Assert.Equal(1.15f, middle.EyeHeight, 5);
        Assert.Equal(Math.Sqrt(100 + 4 + 16), PlayerPose.Distance(a, b), 5);
    }
}

public class PlayerWireTests
{
    private static (uint Time, bool InWorld, PlayerPose Pose, int Bytes) RoundTrip(uint time, bool inWorld, PlayerPose pose)
    {
        var writer = new PacketWriter(64);
        PlayerWire.WriteState(writer, time, 54321, inWorld, pose);
        var reader = new PacketReader(writer.Buffer, 0, writer.Length);
        PlayerWire.ReadState(reader, out uint readTime, out ushort readSequence, out bool readInWorld, out PlayerPose readPose);
        Assert.Equal(0, reader.Remaining);
        Assert.Equal(54321, readSequence);
        return (readTime, readInWorld, readPose, writer.Length);
    }

    [Fact]
    public void A_state_survives_the_wire_within_its_precision()
    {
        var pose = new PlayerPose(-1234.567f, 3.25f, 987.125f, 271.3f, -42.7f, 1.37f);

        var result = RoundTrip(123456789, true, pose);

        Assert.Equal(123456789u, result.Time);
        Assert.True(result.InWorld);
        Assert.Equal(pose.X, result.Pose.X);
        Assert.Equal(pose.Y, result.Pose.Y);
        Assert.Equal(pose.Z, result.Pose.Z);
        Assert.InRange(result.Pose.Yaw, 271.3f - 0.006f, 271.3f + 0.006f);
        Assert.InRange(result.Pose.Pitch, -42.7f - 0.003f, -42.7f + 0.003f);
        Assert.InRange(result.Pose.EyeHeight, 1.365f, 1.375f);
        Assert.Equal(24, result.Bytes);
        Assert.Equal(0, result.Pose.Parent);
        Assert.False(result.Pose.Driving);
    }

    [Fact]
    public void A_passenger_adds_the_vehicle_and_a_driver_adds_the_controls()
    {
        var pose = new PlayerPose(0.4f, 0.9f, -1.2f, 350f, 10f, 1.1f) { Parent = 3 };

        var passenger = RoundTrip(1000, true, pose);

        Assert.Equal(26, passenger.Bytes);
        Assert.Equal(3, passenger.Pose.Parent);
        Assert.False(passenger.Pose.Driving);
        Assert.Equal(0.4f, passenger.Pose.X);

        pose.Driving = true;
        pose.Controls = new VehicleControls { Steering = -0.5f, Throttle = 0.75f, Brake = 0.1f, Clutch = 0.6f, Rpm = 3250f, Gear = 4, Flags = VehicleControls.FlagHandbrake };

        var driver = RoundTrip(1000, true, pose);

        // The largest state is a driver who also drinks or smokes, belted.
        Assert.Equal(PlayerWire.MaxStateBytes - 2, driver.Bytes);
        pose.Activity = PlayerActivity.Smoking;
        Assert.Equal(PlayerWire.MaxStateBytes - 1, RoundTrip(1000, true, pose).Bytes);
        pose.Belted = true;
        Assert.Equal(PlayerWire.MaxStateBytes, RoundTrip(1000, true, pose).Bytes);
        pose.Activity = PlayerActivity.None;
        pose.Belted = false;
        Assert.Equal(3, driver.Pose.Parent);
        Assert.True(driver.Pose.Driving);
        Assert.InRange(driver.Pose.Controls.Steering, -0.505f, -0.495f);
        Assert.InRange(driver.Pose.Controls.Throttle, 0.745f, 0.755f);
        Assert.InRange(driver.Pose.Controls.Brake, 0.095f, 0.105f);
        Assert.InRange(driver.Pose.Controls.Clutch, 0.595f, 0.605f);
        Assert.InRange(driver.Pose.Controls.Rpm, 3249.5f, 3250.5f);
        Assert.Equal(4, driver.Pose.Controls.Gear);
        Assert.True(driver.Pose.Controls.Handbrake);
    }

    [Fact]
    public void Reverse_gear_full_lock_and_nonsense_survive_the_wire()
    {
        var pose = new PlayerPose(0, 0, 0, 0, 0, 1) { Parent = 65535, Driving = true };
        pose.Controls = new VehicleControls { Steering = 7f, Throttle = float.NaN, Brake = -3f, Clutch = 2f, Rpm = 1e9f, Gear = 0 };

        var result = RoundTrip(1, true, pose);

        Assert.Equal(65535, result.Pose.Parent);
        Assert.Equal(1f, result.Pose.Controls.Steering);
        Assert.Equal(0f, result.Pose.Controls.Throttle);
        Assert.Equal(0f, result.Pose.Controls.Brake);
        Assert.Equal(1f, result.Pose.Controls.Clutch);
        Assert.Equal(VehicleControls.MaxRpm, result.Pose.Controls.Rpm);
        Assert.Equal(0, result.Pose.Controls.Gear);
    }

    [Fact]
    public void Hands_out_to_push_survive_the_wire_and_cost_no_bytes()
    {
        var walking = new PlayerPose(1, 2, 3, 40, 5, 1.6f);
        var pushing = walking;
        pushing.Pushing = true;

        var plain = new PacketWriter(64);
        PlayerWire.WriteState(plain, 100, 1, true, walking);
        var flagged = new PacketWriter(64);
        PlayerWire.WriteState(flagged, 100, 1, true, pushing);
        Assert.Equal(plain.Length, flagged.Length);

        Assert.False(RoundTrip(1, true, walking).Pose.Pushing);
        Assert.True(RoundTrip(1, true, pushing).Pose.Pushing);

        // Outside the game world there are no hands to hold out.
        Assert.False(RoundTrip(1, false, pushing).Pose.Pushing);

        // Halfway between a state with the hands out and one without, the nearer one counts.
        Assert.True(PlayerPose.Lerp(pushing, walking, 0.4).Pushing);
        Assert.False(PlayerPose.Lerp(pushing, walking, 0.6).Pushing);

        // Getting on a vehicle with the hands out: nothing lies between the two places, and each keeps its own.
        var aboard = new PlayerPose(0, 0.5f, 0, 0, 0, 1.2f) { Parent = 3 };
        Assert.True(PlayerPose.Lerp(pushing, aboard, 0.4).Pushing);
        Assert.False(PlayerPose.Lerp(pushing, aboard, 0.6).Pushing);
    }

    [Fact]
    public void What_a_player_does_with_their_hands_survives_the_wire_in_one_byte()
    {
        var walking = new PlayerPose(1, 2, 3, 40, 5, 1.6f);
        var smoking = walking;
        smoking.Activity = PlayerActivity.Smoking;
        smoking.ActivityDetail = PlayerPose.SmokeBreathing;
        var drinking = walking;
        drinking.Activity = PlayerActivity.Drinking;
        drinking.ActivityDetail = 15;
        var peeing = walking;
        peeing.Activity = PlayerActivity.Peeing;

        var plain = new PacketWriter(64);
        PlayerWire.WriteState(plain, 100, 1, true, walking);
        var acting = new PacketWriter(64);
        PlayerWire.WriteState(acting, 100, 1, true, smoking);
        Assert.Equal(plain.Length + 1, acting.Length);

        var smoked = RoundTrip(1, true, smoking).Pose;
        Assert.Equal(PlayerActivity.Smoking, smoked.Activity);
        Assert.Equal(PlayerPose.SmokeBreathing, smoked.ActivityDetail);
        var drunk = RoundTrip(1, true, drinking).Pose;
        Assert.Equal(PlayerActivity.Drinking, drunk.Activity);
        Assert.Equal(15, drunk.ActivityDetail);
        Assert.Equal(PlayerActivity.Peeing, RoundTrip(1, true, peeing).Pose.Activity);
        Assert.Equal(PlayerActivity.None, RoundTrip(1, true, walking).Pose.Activity);

        // Riding and driving after it still read right.
        var driving = drinking;
        driving.Parent = 7;
        driving.Driving = true;
        driving.Controls = new VehicleControls { Steering = 0.5f, Gear = 2 };
        var read = RoundTrip(1, true, driving).Pose;
        Assert.Equal(PlayerActivity.Drinking, read.Activity);
        Assert.Equal(7, read.Parent);
        Assert.Equal(2, read.Controls.Gear);

        // Outside the game world nothing is done with the hands.
        Assert.Equal(PlayerActivity.None, RoundTrip(1, false, smoking).Pose.Activity);

        // Halfway between two states, the nearer one counts.
        Assert.Equal(PlayerActivity.Smoking, PlayerPose.Lerp(smoking, walking, 0.4).Activity);
        Assert.Equal(PlayerActivity.None, PlayerPose.Lerp(smoking, walking, 0.6).Activity);

        // An activity this build does not know is refused.
        acting.Buffer[acting.Length - 1] = 0x07;
        var reader = new PacketReader();
        reader.SetBuffer(acting.Buffer, 0, acting.Length);
        Assert.Throws<PacketFormatException>(() => PlayerWire.ReadState(reader, out _, out _, out _, out _));
    }

    [Fact]
    public void A_passenger_says_their_seat_their_belt_and_their_helmet_in_one_byte()
    {
        var standing = new PlayerPose(0.4f, 0.9f, -1.2f, 350f, 10f, 1.1f) { Parent = 3 };
        var seated = standing;
        seated.Seat = 2;
        seated.Belted = true;
        var helmeted = standing;
        helmeted.Helmet = true;

        int plain = RoundTrip(1, true, standing).Bytes;
        var sat = RoundTrip(1, true, seated);
        Assert.Equal(plain + 1, sat.Bytes);
        Assert.Equal(2, sat.Pose.Seat);
        Assert.True(sat.Pose.Belted);
        Assert.False(sat.Pose.Helmet);
        Assert.Equal(3, sat.Pose.Parent);
        var standingWithHelmet = RoundTrip(1, true, helmeted).Pose;
        Assert.Equal(0, standingWithHelmet.Seat);
        Assert.True(standingWithHelmet.Helmet);

        // A driver sits in no passenger seat, but their belt and helmet are said too (the others show the belt on).
        var driving = seated;
        driving.Driving = true;
        driving.Helmet = true;
        var drove = RoundTrip(1, true, driving).Pose;
        Assert.Equal(0, drove.Seat);
        Assert.True(drove.Belted);
        Assert.True(drove.Helmet);
        Assert.True(drove.Driving);
        var walking = seated;
        walking.Parent = 0;
        Assert.Equal(0, RoundTrip(1, true, walking).Pose.Seat);

        // A seat past the last one there can be goes out as standing.
        var odd = standing;
        odd.Seat = 40;
        Assert.Equal(0, RoundTrip(1, true, odd).Pose.Seat);

        // Halfway between sitting down and standing, the nearer one counts.
        Assert.Equal(2, PlayerPose.Lerp(seated, standing, 0.4).Seat);
        Assert.False(PlayerPose.Lerp(seated, standing, 0.6).Belted);

        // A byte with bits this build does not know is refused, and so is the flag without a vehicle.
        var writer = new PacketWriter(64);
        PlayerWire.WriteState(writer, 1, 1, true, seated);
        writer.Buffer[writer.Length - 1] |= 0x40;
        var reader = new PacketReader();
        reader.SetBuffer(writer.Buffer, 0, writer.Length);
        Assert.Throws<PacketFormatException>(() => PlayerWire.ReadState(reader, out _, out _, out _, out _));

        writer.Reset();
        PlayerWire.WriteState(writer, 1, 1, true, new PlayerPose(1, 2, 3, 4, 5, 1));
        writer.Buffer[6] |= PlayerWire.FlagRider;
        reader.SetBuffer(writer.Buffer, 0, writer.Length + 1);
        Assert.Throws<PacketFormatException>(() => PlayerWire.ReadState(reader, out _, out _, out _, out _));
    }

    [Fact]
    public void A_player_on_foot_says_their_helmet_in_a_flag()
    {
        var walking = new PlayerPose(1, 2, 3, 4, 5, 1.65f);
        var helmeted = walking;
        helmeted.Helmet = true;

        var plain = RoundTrip(1, true, walking);
        var worn = RoundTrip(1, true, helmeted);
        Assert.False(plain.Pose.Helmet);
        Assert.True(worn.Pose.Helmet);
        Assert.Equal(plain.Bytes, worn.Bytes);

        // A rider says it in the rider's byte, not in the flag; the flag with a vehicle is refused.
        var riding = helmeted;
        riding.Parent = 3;
        var writer = new PacketWriter(64);
        PlayerWire.WriteState(writer, 1, 1, true, riding);
        Assert.Equal(0, writer.Buffer[6] & PlayerWire.FlagHelmet);
        Assert.True(RoundTrip(1, true, riding).Pose.Helmet);
        writer.Buffer[6] |= PlayerWire.FlagHelmet;
        var reader = new PacketReader();
        reader.SetBuffer(writer.Buffer, 0, writer.Length);
        Assert.Throws<PacketFormatException>(() => PlayerWire.ReadState(reader, out _, out _, out _, out _));
    }

    [Fact]
    public void Driving_without_riding_is_not_written_and_is_refused_when_read()
    {
        // The pose says "driving" but names no vehicle: the state goes out as that of someone on foot.
        var result = RoundTrip(1, true, new PlayerPose(1, 2, 3, 4, 5, 1) { Driving = true });
        Assert.Equal(24, result.Bytes);
        Assert.False(result.Pose.Driving);

        var writer = new PacketWriter(64);
        PlayerWire.WriteState(writer, 1, 1, true, new PlayerPose(1, 2, 3, 4, 5, 1));
        writer.Buffer[6] |= PlayerWire.FlagDriving;
        var reader = new PacketReader();
        reader.SetBuffer(writer.Buffer, 0, writer.Length + VehicleControls.Size);
        Assert.Throws<PacketFormatException>(() => PlayerWire.ReadState(reader, out _, out _, out _, out _));

        writer.Reset();
        PlayerWire.WriteState(writer, 1, 1, true, new PlayerPose(1, 2, 3, 4, 5, 1) { Parent = 2 });
        writer.Buffer[writer.Length - 2] = 0;
        writer.Buffer[writer.Length - 1] = 0;
        reader.SetBuffer(writer.Buffer, 0, writer.Length);
        Assert.Throws<PacketFormatException>(() => PlayerWire.ReadState(reader, out _, out _, out _, out _));
    }

    [Fact]
    public void Getting_into_a_vehicle_is_neither_a_jump_nor_something_to_blend_across()
    {
        var onFoot = new PlayerPose(600, -3, -800, 90, 0, 1.6f);
        var seated = new PlayerPose(-0.4f, 0.9f, 0.8f, 0, 0, 1.0f) { Parent = 1, Driving = true };
        seated.Controls.Rpm = 900f;

        Assert.Equal(0.0, PlayerPose.Distance(onFoot, seated));
        Assert.Equal(600f, PlayerPose.Lerp(onFoot, seated, 0.4).X);
        Assert.Equal(0, PlayerPose.Lerp(onFoot, seated, 0.4).Parent);
        Assert.Equal(-0.4f, PlayerPose.Lerp(onFoot, seated, 0.6).X);
        Assert.Equal(1, PlayerPose.Lerp(onFoot, seated, 0.6).Parent);
        seated.OffsetFrom(onFoot, out double x, out double y, out double z);
        Assert.Equal((0.0, 0.0, 0.0), (x, y, z));

        // Inside the vehicle everything blends as usual, the controls too.
        var later = seated;
        later.X = 0.4f;
        later.Controls.Rpm = 2900f;
        later.Controls.Steering = 1f;
        PlayerPose half = PlayerPose.Lerp(seated, later, 0.5);
        Assert.Equal(0f, half.X, 3);
        Assert.Equal(1, half.Parent);
        Assert.True(half.Driving);
        Assert.Equal(1900f, half.Controls.Rpm, 1);
        Assert.Equal(0.5f, half.Controls.Steering, 3);
    }

    [Fact]
    public void A_player_outside_the_world_costs_seven_bytes_and_carries_no_pose()
    {
        var result = RoundTrip(7, false, new PlayerPose(1, 2, 3, 4, 5, 6));

        Assert.False(result.InWorld);
        Assert.Equal(7, result.Bytes);
        Assert.Equal(0f, result.Pose.X);
        Assert.Equal(0f, result.Pose.EyeHeight);
    }

    [Theory]
    [InlineData(-90f, -90f)]
    [InlineData(90f, 90f)]
    [InlineData(135f, 90f)]
    [InlineData(-400f, -90f)]
    [InlineData(float.NaN, 0f)]
    public void The_pitch_is_clamped(float pitch, float expected)
    {
        var result = RoundTrip(0, true, new PlayerPose(0, 0, 0, 0, pitch, 1));

        Assert.Equal(expected, result.Pose.Pitch, 2);
    }

    [Theory]
    [InlineData(-90f, 270f)]
    [InlineData(720f, 0f)]
    [InlineData(359.999f, 0f)]
    [InlineData(float.PositiveInfinity, 0f)]
    public void The_yaw_wraps_into_one_turn(float yaw, float expected)
    {
        var result = RoundTrip(0, true, new PlayerPose(0, 0, 0, yaw, 0, 1));

        Assert.Equal(expected, result.Pose.Yaw, 2);
        Assert.InRange(result.Pose.Yaw, 0f, 359.999f);
    }

    [Theory]
    [InlineData(-1f, 0f)]
    [InlineData(9f, PlayerWire.MaxEyeHeight)]
    public void The_eye_height_is_clamped(float eyes, float expected)
    {
        var result = RoundTrip(0, true, new PlayerPose(0, 0, 0, 0, 0, eyes));

        Assert.Equal(expected, result.Pose.EyeHeight, 3);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    [InlineData(1e9f)]
    public void A_position_no_player_can_be_at_is_refused(float coordinate)
    {
        var writer = new PacketWriter(64);
        PlayerWire.WriteState(writer, 1, 2, true, new PlayerPose(0, coordinate, 0, 0, 0, 1));
        var reader = new PacketReader(writer.Buffer, 0, writer.Length);

        Assert.Throws<PacketFormatException>(() => PlayerWire.ReadState(reader, out _, out _, out _, out _));
    }

    [Fact]
    public void A_state_cut_short_is_refused()
    {
        var writer = new PacketWriter(64);
        PlayerWire.WriteState(writer, 1, 2, true, new PlayerPose(1, 2, 3, 4, 5, 1));

        for (int length = 0; length < writer.Length; length++)
        {
            var reader = new PacketReader(writer.Buffer, 0, length);
            Assert.Throws<PacketFormatException>(() => PlayerWire.ReadState(reader, out _, out _, out _, out _));
        }
    }

    [Fact]
    public void Player_messages_are_game_messages()
    {
        Assert.True(PlayerWire.MessageState >= Protocol.FirstUserMessage);
        Assert.NotEqual(PlayerWire.MessageState, PlayerWire.MessageStates);
    }
}
