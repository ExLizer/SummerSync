using System.Collections.Generic;
using SummerSync.Core.Logging;
using SummerSync.Core.Net;
using SummerSync.Core.Objects;
using SummerSync.Core.World;
using UnityEngine;

namespace SummerSync.Unity.Vehicles
{
    /// <summary>
    /// The vehicles' section of the checksums (Fase 8): where each vehicle the host simulates is parked, as each game
    /// shows it. A followed vehicle at rest keeps its body where it is drawn; one drawn somewhere else than the host has
    /// it is put back.
    /// </summary>
    internal sealed partial class VehiclePresenter
    {
        private const float ParkedTolerance = 0.3f;

        private ParkedSection parked;

        /// <summary>The section of where the host's vehicles are parked.</summary>
        public ICheckSection ParkedCheck
        {
            get { return parked ?? (parked = new ParkedSection(this)); }
        }

        private void ReadParked(List<CheckEntry> entries)
        {
            bool host = manager.Mode == NetworkMode.Host;
            for (int i = 0; i < tracked.Count; i++)
            {
                Tracked vehicle = tracked[i];
                SharedBody shared = vehicle.Shared;

                // Unity's null check is also true for a destroyed object.
                if (shared == null || !shared.HasId || shared.Owner != Protocol.HostPlayerId || shared.Held || vehicle.Root == null || vehicle.Body == null)
                {
                    continue;
                }

                bool parkedHere = host
                    ? shared.Role == BodyRole.Simulate && vehicle.Body.IsSleeping()
                    : shared.Role == BodyRole.Follow && shared.HasTarget && shared.AtRest && vehicle.AtRest;
                if (!parkedHere)
                {
                    continue;
                }

                Vector3 at = vehicle.Root.position;
                entries.Add(new CheckEntry(shared.Id, CheckPlace.Pack(at.x, at.y, at.z)));
            }
        }

        private void PutBack(IList<ulong> keys)
        {
            int put = 0;
            for (int k = 0; k < keys.Count; k++)
            {
                for (int i = 0; i < tracked.Count; i++)
                {
                    Tracked vehicle = tracked[i];
                    if (vehicle.Shared != null && vehicle.Shared.HasId && vehicle.Shared.Id == (ushort)keys[k] && vehicle.Shared.Role == BodyRole.Follow)
                    {
                        vehicle.AtRest = false;
                        put++;
                    }
                }
            }

            if (put > 0)
            {
                log.Info(LogCategory.Vehicle, put + " vehicle(s) were parked elsewhere than in the host's game: put back where the host has them.");
            }
        }

        private sealed class ParkedSection : ICheckSection
        {
            private readonly VehiclePresenter presenter;

            public ParkedSection(VehiclePresenter presenter)
            {
                this.presenter = presenter;
            }

            public byte Id
            {
                get { return CheckSections.Vehicles; }
            }

            public string Name
            {
                get { return "parked vehicles"; }
            }

            public bool Ready
            {
                get { return presenter.current != null && presenter.tracked.Count > 0; }
            }

            public bool CountsMissing
            {
                get { return false; }
            }

            public bool SendsEntries
            {
                get { return true; }
            }

            public void Read(List<CheckEntry> entries)
            {
                presenter.ReadParked(entries);
            }

            public ulong Coarse(ulong value)
            {
                return value;
            }

            public bool Same(ulong host, ulong here)
            {
                return CheckPlace.Near(host, here, ParkedTolerance);
            }

            public void FixHere(IList<ulong> different, IList<ulong> onlyHost, IList<ulong> onlyHere)
            {
                presenter.PutBack(different);
            }

            public void FixFor(byte player, IList<ulong> keys)
            {
                for (int i = 0; i < keys.Count; i++)
                {
                    presenter.current.ResendRest(player, (ushort)keys[i]);
                }
            }
        }
    }
}
