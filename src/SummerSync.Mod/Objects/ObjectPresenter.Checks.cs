using System.Collections.Generic;
using SummerSync.Core.Logging;
using SummerSync.Core.Net;
using SummerSync.Core.Objects;
using SummerSync.Core.World;
using UnityEngine;

namespace SummerSync.Unity.Objects
{
    /// <summary>
    /// The loose objects' sections of the checksums (Fase 8): where each object the host simulates rests, as each game
    /// shows it, and what each thing holds. A copy at rest is not touched again until its owner moves it, so a copy moved
    /// here by anything else (the game, or a forced desync) stays where it was moved; the check puts it back.
    /// </summary>
    internal sealed partial class ObjectPresenter
    {
        /// <summary>How near a resting copy must be to the host's place to be the same.</summary>
        private const float RestTolerance = 0.3f;

        // Food spoils in every game by itself: compared in steps of this much.
        private const float ConditionStep = 5f;

        private PlacesSection places;
        private ContentsSection contents;

        /// <summary>The section of where the host's objects rest.</summary>
        public ICheckSection PlacesCheck
        {
            get { return places ?? (places = new PlacesSection(this)); }
        }

        /// <summary>The section of what each thing holds.</summary>
        public ICheckSection ContentsCheck
        {
            get { return contents ?? (contents = new ContentsSection(this)); }
        }

        private bool CheckReady
        {
            get { return current != null && firstScanDone && (manager.Mode == NetworkMode.Host ? current.WorldReady : current.ListComplete); }
        }

        private void ReadPlaces(List<CheckEntry> entries)
        {
            bool host = manager.Mode == NetworkMode.Host;
            for (int i = 0; i < tracked.Count; i++)
            {
                Tracked item = tracked[i];
                SharedBody shared = item.Shared;

                // Unity's null check is also true for a destroyed object.
                if (!shared.HasId || shared.Owner != Protocol.HostPlayerId || shared.Held || item.Transform == null || item.Body == null || item.Hidden)
                {
                    continue;
                }

                bool resting = host
                    ? shared.Role == BodyRole.Simulate && !shared.LocalAwake && item.Body.IsSleeping()
                    : shared.Role == BodyRole.Follow && shared.HasTarget && shared.Target.Parent == 0 && shared.AtRest && item.AtRest;
                if (!resting)
                {
                    continue;
                }

                Vector3 at = item.Transform.position;
                entries.Add(new CheckEntry(shared.Id, CheckPlace.Pack(at.x, at.y, at.z)));
            }
        }

        // A client: the copies that rest somewhere else than the host has them go back to where it said they rest.
        private void PutBack(IList<ulong> keys)
        {
            int put = 0;
            for (int k = 0; k < keys.Count; k++)
            {
                Tracked item = FindTracked((ushort)keys[k]);
                if (item != null && item.Shared.Role == BodyRole.Follow)
                {
                    item.AtRest = false;
                    put++;
                }
            }

            if (put > 0)
            {
                log.Info(LogCategory.Authority, put + " object(s) rested elsewhere than in the host's game: put back where the host has them.");
            }
        }

        private void ReadContents(List<CheckEntry> entries)
        {
            for (int i = 0; i < tracked.Count; i++)
            {
                Tracked item = tracked[i];

                // Unity's null check is also true for a destroyed object.
                if (!item.Shared.HasId || item.Body == null || item.Transform == null)
                {
                    continue;
                }

                string signature = ReadItem(item, null);
                if (signature == null)
                {
                    continue;
                }

                PlayMakerFSM use = UseOf(item);
                HutongGames.PlayMaker.FsmFloat condition = use == null ? null : use.FsmVariables.FindFsmFloat("Condition");
                int spoiled = condition == null ? 0 : Mathf.RoundToInt(condition.Value / ConditionStep);
                entries.Add(new CheckEntry(item.Shared.Id, ChecksumReplication.KeyOf(signature) ^ (ulong)(uint)spoiled));
            }
        }

        // The host: what the things a client has otherwise are, told to it again.
        private void TellContents(byte player, IList<ulong> keys)
        {
            if (items == null)
            {
                return;
            }

            for (int k = 0; k < keys.Count; k++)
            {
                Tracked item = FindTracked((ushort)keys[k]);
                if (item != null && item.Transform != null && ReadItem(item, itemValues, true) != null)
                {
                    items.TellStateTo(player, item.Shared.Id, item.Transform.gameObject.name, itemValues);
                }
            }
        }

        private sealed class PlacesSection : ICheckSection
        {
            private readonly ObjectPresenter presenter;

            public PlacesSection(ObjectPresenter presenter)
            {
                this.presenter = presenter;
            }

            public byte Id
            {
                get { return CheckSections.Places; }
            }

            public string Name
            {
                get { return "objects at rest"; }
            }

            public bool Ready
            {
                get { return presenter.CheckReady; }
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
                presenter.ReadPlaces(entries);
            }

            public ulong Coarse(ulong value)
            {
                return value;
            }

            public bool Same(ulong host, ulong here)
            {
                return CheckPlace.Near(host, here, RestTolerance);
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

        private sealed class ContentsSection : ICheckSection
        {
            private readonly ObjectPresenter presenter;

            public ContentsSection(ObjectPresenter presenter)
            {
                this.presenter = presenter;
            }

            public byte Id
            {
                get { return CheckSections.Contents; }
            }

            public string Name
            {
                get { return "what things hold"; }
            }

            public bool Ready
            {
                get { return presenter.CheckReady; }
            }

            public bool CountsMissing
            {
                get { return false; }
            }

            public bool SendsEntries
            {
                get { return false; }
            }

            public void Read(List<CheckEntry> entries)
            {
                presenter.ReadContents(entries);
            }

            public ulong Coarse(ulong value)
            {
                return value;
            }

            public bool Same(ulong host, ulong here)
            {
                return host == here;
            }

            public void FixHere(IList<ulong> different, IList<ulong> onlyHost, IList<ulong> onlyHere)
            {
            }

            public void FixFor(byte player, IList<ulong> keys)
            {
                presenter.TellContents(player, keys);
            }
        }
    }
}
