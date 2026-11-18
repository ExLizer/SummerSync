using System.Globalization;
using System.Text;
using SummerSync.Core.Logging;
using SummerSync.Core.Objects;
using SummerSync.Core.Vehicles;
using UnityEngine;

namespace SummerSync.Unity.Vehicles
{
    /// <summary>
    /// Whether a vehicle that another machine runs is drawn where that machine moves it, written down when it is not: what is
    /// drawn (the vehicle's transform, which also carries its driver), its physics body (which its doors, bonnet and boot lid
    /// hang from by their joints) and the place its owner's states give, over the last second, with what could hold the one
    /// that stays behind.
    ///
    /// Why (docs/GAME_NOTES.md, "El Satsuma que se queda mientras sus puertas se van", 2026-11-17): a guest saw the Satsuma
    /// the host drove stay where it was parked, with the host at its wheel, while its doors, boot lid and bonnet went on, and
    /// all of it snap into place when the guest came near. Two copies of the game on one PC, at 40 m to 1.9 km, did not show
    /// it, and the log had nothing to say: every check of the mod on a followed vehicle reads its body, none what is drawn.
    /// </summary>
    internal sealed partial class VehiclePresenter
    {
        // Lines about vehicles not drawn where they belong, for the whole session.
        private const int DrawnLogged = 16;

        private int drawnSaid;

        // A vehicle simulated here although another player is seen at its wheel for this long is said.
        private const double DrivenElsewhereSeconds = 2.0;

        // Once a frame, after the vehicles are driven and Unity has drawn them: each kinematic copy against its body and its
        // owner's states; and each vehicle another player is seen driving, against who runs it here and whether its states
        // still arrive.
        private void NoteDrawn(double now)
        {
            for (int i = 0; i < tracked.Count; i++)
            {
                Tracked vehicle = tracked[i];
                SharedBody shared = vehicle.Shared;
                if (vehicle.Body != null && shared.HasId)
                {
                    NoteDriver(vehicle, now);
                }

                if (vehicle.Body == null || !shared.HasId || shared.Role != BodyRole.Follow || !vehicle.Following || vehicle.InContact || !shared.HasTarget)
                {
                    if (vehicle.Drawn.Stop() == DrawnVerdict.Ended)
                    {
                        SayDrawn(vehicle, DrawnVerdict.Ended, now);
                    }

                    continue;
                }

                Vector3 solid = vehicle.Body.position;
                DrawnVerdict verdict = vehicle.Drawn.Note(
                    now, PoseOf(vehicle.Root), BodyPose.At(solid.x, solid.y, solid.z), shared.Target, vehicle.LastStepLength);
                if (verdict != DrawnVerdict.None)
                {
                    SayDrawn(vehicle, verdict, now);
                }
            }
        }

        // A vehicle another player is seen driving: run here all the same (two machines that both take it for theirs), or
        // followed with no states coming.
        private void NoteDriver(Tracked vehicle, double now)
        {
            SharedBody shared = vehicle.Shared;
            VehicleControls controls;
            byte driver;
            bool remote = DriverOf(shared.Id, out driver, out controls);
            bool here = remote && shared.Role == BodyRole.Simulate;
            if (!here)
            {
                if (vehicle.DrivenElsewhereSaid)
                {
                    SayDriven(vehicle, driver, now, true);
                }

                vehicle.DrivenElsewhereSince = double.NaN;
                vehicle.DrivenElsewhereSaid = false;
            }
            else if (double.IsNaN(vehicle.DrivenElsewhereSince))
            {
                vehicle.DrivenElsewhereSince = now;
            }
            else if (!vehicle.DrivenElsewhereSaid && now - vehicle.DrivenElsewhereSince >= DrivenElsewhereSeconds)
            {
                vehicle.DrivenElsewhereSaid = true;
                SayDriven(vehicle, driver, now, false);
            }

            long received = shared.Buffer == null ? -1L : shared.Buffer.Received;
            DrawnVerdict silent = vehicle.Silent.Note(now, received, remote && shared.Role == BodyRole.Follow);
            if (silent != DrawnVerdict.None && drawnSaid < DrawnLogged)
            {
                drawnSaid++;
                var text = new StringBuilder(400);
                text.Append("Drawn: '").Append(vehicle.Root.name).Append("', which player ").Append(driver).Append(" is seen driving and player ").Append(shared.Owner)
                    .Append(" runs");
                if (silent == DrawnVerdict.Started)
                {
                    text.Append(", has had no new state from its owner here for ").Append(Seconds(now - vehicle.Silent.SilentSince))
                        .Append(shared.Buffer == null ? " (none at all since its owner last changed)" : " (" + shared.Buffer.Received + " so far)")
                        .Append("; its copy stands at ").Append(Point(vehicle.Root.position)).Append(", ")
                        .Append(Metres(player == null ? float.NaN : (vehicle.Root.position - player.position).magnitude)).Append(" from this player.");
                    log.Warning(LogCategory.Vehicle, text.ToString());
                }
                else
                {
                    text.Append(": its states arrive here again, or nobody drives it now.");
                    log.Info(LogCategory.Vehicle, text.ToString());
                }
            }
        }

        // Who another player at the wheel of that vehicle is, as this machine shows them.
        private bool DriverOf(ushort vehicleId, out byte driver, out VehicleControls controls)
        {
            Core.Players.PlayerReplication players = manager.Players;
            if (players != null)
            {
                System.Collections.Generic.IList<Core.Players.RemotePlayer> remotes = players.Remotes;
                for (int i = 0; i < remotes.Count; i++)
                {
                    Core.Players.RemotePlayer remote = remotes[i];
                    if (remote.Visible && remote.Shown.Driving && remote.Shown.Parent == vehicleId)
                    {
                        driver = remote.Id;
                        controls = remote.Shown.Controls;
                        return true;
                    }
                }
            }

            driver = 0;
            controls = default(VehicleControls);
            return false;
        }

        private void SayDriven(Tracked vehicle, byte driver, double now, bool over)
        {
            if (drawnSaid >= DrawnLogged)
            {
                return;
            }

            drawnSaid++;
            SharedBody shared = vehicle.Shared;
            if (over)
            {
                log.Info(
                    LogCategory.Vehicle,
                    "Drawn: '" + vehicle.Root.name + "' is no longer run here while another player drives it, after "
                    + Seconds(now - vehicle.DrivenElsewhereSince) + " (player " + shared.Owner + " runs it now).");
                return;
            }

            log.Warning(
                LogCategory.Vehicle,
                "Drawn: '" + vehicle.Root.name + "' is run here (this machine takes it for its own" + (shared.Held ? ", held here" : string.Empty)
                + (shared.PendingClaim ? ", asked for and not answered yet" : string.Empty) + ") although player " + driver + " is seen at its wheel, for "
                + Seconds(now - vehicle.DrivenElsewhereSince) + ": that player's states for it are not taken here. It stands at " + Point(vehicle.Root.position)
                + ", " + Metres(player == null ? float.NaN : (vehicle.Root.position - player.position).magnitude) + " from this player; its body "
                + (vehicle.Body.isKinematic ? "kinematic." : "with its own physics."));
        }

        private void SayDrawn(Tracked vehicle, DrawnVerdict verdict, double now)
        {
            if (drawnSaid >= DrawnLogged)
            {
                return;
            }

            drawnSaid++;
            DrawnWatch watch = vehicle.Drawn;
            SharedBody shared = vehicle.Shared;
            var text = new StringBuilder(900);
            text.Append("Drawn: '").Append(vehicle.Root.name).Append("', run by player ").Append(shared.Owner)
                .Append(shared.Held ? " (held there: at the wheel or in hand)" : string.Empty);
            if (verdict == DrawnVerdict.Ended)
            {
                text.Append(", is drawn where its owner has it again, ").Append(Seconds(now - watch.WrongSince)).Append(" after it was not");
                if (!vehicle.Following || vehicle.InContact || shared.Role != BodyRole.Follow)
                {
                    text.Append(" (it is ").Append(shared.Role != BodyRole.Follow ? "simulated here" : vehicle.InContact ? "a copy with its own physics" : "not followed")
                        .Append(" now)");
                }

                text.Append(". ");
            }
            else
            {
                text.Append(verdict == DrawnVerdict.Started ? ", is not drawn where its owner moves it" : ", is still not drawn where its owner moves it")
                    .Append(" (for ").Append(Seconds(now - watch.WrongSince)).Append("): in the last second its owner's states went ")
                    .Append(Metres(watch.TargetMoved)).Append(", its physics body ").Append(Metres(watch.BodyMoved)).Append(" and what is drawn ")
                    .Append(Metres(watch.DrawnMoved)).Append("; drawn up to ").Append(Metres(watch.WorstGap)).Append(" off its body beyond interpolation, and more than ")
                    .Append(Metres(DrawnWatch.FarFromOwner)).Append(" from its owner's place in ").Append((watch.FarFrames * 100.0).ToString("0", CultureInfo.InvariantCulture))
                    .Append("% of the frames (").Append(Metres(watch.OwnerGap)).Append(" at the end). ");
            }

            Vector3 drawn = vehicle.Root.position;
            Vector3 solid = vehicle.Body.position;
            BodyPose target = shared.Target;
            text.Append("Drawn at ").Append(Point(drawn)).Append(", its body at ").Append(Point(solid)).Append(", its owner's place ")
                .Append(Point(new Vector3(target.X, target.Y, target.Z))).Append(" (the session ").Append(shared.AtRest ? "at rest" : "moving")
                .Append(", here ").Append(vehicle.AtRest ? "at rest" : "moving").Append(vehicle.Placed ? string.Empty : ", not placed yet").Append("). ");
            Rigidbody body = vehicle.Body;
            text.Append("Its body ").Append(body.isKinematic ? "kinematic" : "NOT kinematic").Append(", interpolation ").Append(body.interpolation)
                .Append(body.IsSleeping() ? ", asleep" : string.Empty).Append(body.detectCollisions ? string.Empty : ", no collisions")
                .Append(body.constraints == RigidbodyConstraints.None ? string.Empty : ", constraints " + body.constraints).Append("; ");
            vehicle.DescribeLastPut(text);
            text.Append("; its parts: ");
            vehicle.DescribeHinges(text);
            float mine = player == null ? float.NaN : (drawn - player.position).magnitude;
            Camera view = Camera.main;
            text.Append(" This player is ").Append(Metres(mine)).Append(" from it");
            if (view != null)
            {
                text.Append(", the camera ").Append(Metres((drawn - view.transform.position).magnitude));
            }

            text.Append(vehicle.Ridden ? ", aboard" : string.Empty);
            PlayMakerFSM machine = vehicle.RescueMachine;
            // Unity's null check is also true for a destroyed object.
            if (machine != null)
            {
                text.Append("; the game's '").Append(machine.FsmName).Append("' of the vehicle in '").Append(machine.ActiveStateName).Append('\'')
                    .Append(machine.enabled ? string.Empty : " (off)");
            }

            text.Append(drawnSaid == DrawnLogged ? ". (No more of these lines this session.)" : ".");
            if (verdict == DrawnVerdict.Started)
            {
                log.Warning(LogCategory.Vehicle, text.ToString());
            }
            else
            {
                log.Info(LogCategory.Vehicle, text.ToString());
            }
        }

        private static string Seconds(double seconds)
        {
            return seconds.ToString("0.0", CultureInfo.InvariantCulture) + " s";
        }

        private static string Metres(double metres)
        {
            return double.IsNaN(metres) ? "?" : metres.ToString(metres < 10.0 ? "0.00" : "0", CultureInfo.InvariantCulture) + " m";
        }

        private sealed partial class Tracked
        {
            /// <summary>Whether it is drawn where its owner moves it, a second at a time.</summary>
            public DrawnWatch Drawn { get; } = new DrawnWatch();

            /// <summary>Whether its states keep arriving while another player is seen driving it.</summary>
            public SilentStream Silent { get; } = new SilentStream();

            /// <summary>Since when it is run here although another player is seen at its wheel (NaN if it is not).</summary>
            public double DrivenElsewhereSince { get; set; } = double.NaN;

            public bool DrivenElsewhereSaid { get; set; }

            /// <summary>The game's machine that rescues the vehicle (its "Setup" for the Satsuma), if it has one: its state goes to the log.</summary>
            public PlayMakerFSM RescueMachine { get; set; }

            // The last time the mod put the vehicle somewhere by its transform while following it, and why.
            private string lastPut;
            private float lastPutAt;

            /// <summary>Notes that the mod has just put the vehicle somewhere by its transform (a constant text: no garbage).</summary>
            public void NotePut(string why)
            {
                lastPut = why;
                lastPutAt = Time.time;
            }

            public void DescribeLastPut(StringBuilder text)
            {
                if (lastPut == null)
                {
                    text.Append("never put by its transform since it is followed");
                    return;
                }

                text.Append("last put by its transform ").Append((Time.time - lastPutAt).ToString("0.0", CultureInfo.InvariantCulture)).Append(" s ago (")
                    .Append(lastPut).Append("), ").Append(astrayPut).Append(" part(s) put back on it so far");
            }

            /// <summary>
            /// For the first few parts that swing on a hinge to the body (doors, bonnet, boot lid): how far the point of the part
            /// on its hinge is from that point on the drawn vehicle and on its body, which tells which one it follows.
            /// </summary>
            public void DescribeHinges(StringBuilder text)
            {
                int said = 0;
                Vector3 solid = Body.position;
                Quaternion solidTurn = Body.rotation;
                for (int i = 0; i < partHinge.Length && i < parts.Length && said < 3; i++)
                {
                    Rigidbody part = parts[i];

                    // Unity's null check is also true for a destroyed object.
                    if (partHinge[i] == null || part == null || !part.gameObject.activeInHierarchy)
                    {
                        continue;
                    }

                    Vector3 hingeAt = part.transform.TransformPoint(partHinge[i].anchor);
                    float toDrawn = (hingeAt - Root.TransformPoint(partAnchor[i])).magnitude;
                    float toBody = (hingeAt - (solid + (solidTurn * partAnchor[i]))).magnitude;
                    text.Append(said == 0 ? string.Empty : ", ").Append('\'').Append(part.name).Append("' ").Append(Metres(toDrawn)).Append(" from its hinge on what is drawn and ")
                        .Append(Metres(toBody)).Append(" from it on the body").Append(part.isKinematic ? " (kinematic)" : string.Empty);
                    said++;
                }

                if (said == 0)
                {
                    text.Append("none on a hinge");
                }

                text.Append('.');
            }
        }
    }
}
