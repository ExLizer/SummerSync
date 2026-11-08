using System;
using HutongGames.PlayMaker;
using SummerSync.Core.Logging;
using SummerSync.Core.Net;
using SummerSync.Core.World;
using UnityEngine;

namespace SummerSync.Unity.World
{
    /// <summary>
    /// The money, as far as the game is concerned: one wallet for every player, the host's. Each game goes on
    /// earning and spending by itself; <see cref="WalletReplication"/> says, frame by frame, what the money
    /// should be, and this writes it.
    ///
    /// What is assumed about the game, from the dump of 2026-10-12 (docs/GAME_NOTES.md):
    ///
    /// The money is the global float "PlayerMoney". The tills, the slot machine, the bus ticket and the rest
    /// add to it or take from it. The money on the screen ("GUI/HUD/Money/HUDValue", state machine "Text")
    /// is redrawn whenever that number changes, so writing it is enough. The save is read into it by the
    /// state machine "Simulation" of "Database/PlayerDatabase", in its state "Load", which it passes through
    /// when the world starts and again right after each save; nothing is looked at until it has left it once.
    ///
    /// Offline and in the loopback test nothing here touches the game.
    /// </summary>
    internal sealed class MoneySync
    {
        private const string MoneyVariable = "PlayerMoney";
        private const string DatabasePath = "Database/PlayerDatabase";
        private const string DatabaseMachine = "Simulation";

        // The states in which the save is read into the money, or about to be.
        private static readonly string[] LoadingStates = { "State 3", "Double check 2", "Save data", "Load" };

        private const double SearchInterval = 2.0;

        // Without the database to say when the save has been read, the money is trusted after this long.
        private const double SettleSeconds = 5.0;

        private readonly Logger log;
        private readonly NetworkManager manager;
        private WalletReplication current;
        private double enteredAt;
        private double nextSearch;
        private bool searchReported;
        private FsmFloat money;
        private PlayMakerFSM database;
        private bool ready;
        private bool sharedReported;
        private bool counting;

        public MoneySync(Logger log, NetworkManager manager)
        {
            this.log = log;
            this.manager = manager;
        }

        /// <summary>
        /// Once per frame, after the game has moved everything. <paramref name="localPlayer"/> is the player's
        /// root object, or null while there is no game world.
        /// </summary>
        public void LateUpdate(double now, Transform localPlayer)
        {
            WalletReplication wallet = localPlayer == null ? null : manager.Wallet;
            if (wallet != current)
            {
                if (current != null)
                {
                    current.ClientChanged -= OnClientChanged;
                    current.ClearWorld();
                }

                current = wallet;
                if (current != null)
                {
                    current.ClientChanged += OnClientChanged;
                }

                money = null;
                database = null;
                ready = false;
                sharedReported = false;
                counting = false;
                searchReported = false;
                enteredAt = now;
                nextSearch = now;
            }

            if (current == null)
            {
                return;
            }

            if (money == null && now >= nextSearch)
            {
                nextSearch = now + SearchInterval;
                Search();
            }

            if (money == null || !Settled(now))
            {
                return;
            }

            float here = money.Value;
            float before = current.Balance;
            long told = current.Changes;
            bool host = manager.Mode == NetworkMode.Host;
            float wanted = current.Sync(here);
            if (host)
            {
                if (counting && Math.Abs(here - before) >= WalletReplication.Smallest)
                {
                    log.Info(LogCategory.Fsm, "Money: this game " + Spent(here - before) + "; " + WalletReplication.Text(wanted) + " now, for everyone.");
                }

                counting = true;
            }
            else if (current.Changes != told)
            {
                log.Info(LogCategory.Fsm, "Money: this game " + Spent(here - before) + "; the host is told.");
            }

            // The host adds in its own order: a few millionths apart is the same money, and not worth a redraw.
            if (Math.Abs(wanted - here) < WalletReplication.Smallest)
            {
                return;
            }

            money.Value = wanted;
            if (!host)
            {
                if (!sharedReported)
                {
                    sharedReported = true;
                    log.Info(LogCategory.Fsm, "Money: the host's from now on, " + WalletReplication.Text(wanted) + " (this game had " + WalletReplication.Text(here) + ").");
                }
                else
                {
                    log.Info(LogCategory.Fsm, "Money: " + WalletReplication.Text(wanted) + " as the host says (" + WalletReplication.Text(here) + " here).");
                }
            }
        }

        // Whether the save has been read into the money, and is not being read again right now.
        private bool Settled(double now)
        {
            if (database == null)
            {
                // Unity's null check is also true for a destroyed object: then the time is what tells.
                ready = ready || now - enteredAt >= SettleSeconds;
                return ready;
            }

            string state = database.ActiveStateName;
            bool loading = Array.IndexOf(LoadingStates, state) >= 0;
            if (!ready && !loading)
            {
                ready = true;
                log.Info(LogCategory.Fsm, "Money: the save has been read (database in state '" + state + "'), " + WalletReplication.Text(money.Value) + " here.");
            }

            return ready && !loading;
        }

        private void Search()
        {
            money = FsmVariables.GlobalVariables.FindFsmFloat(MoneyVariable);
            GameObject owner = GameObject.Find(DatabasePath);
            database = owner == null ? null : Machine(owner, DatabaseMachine);
            if (money != null && database != null)
            {
                return;
            }

            if (!searchReported)
            {
                searchReported = true;
                log.Warning(
                    LogCategory.Fsm,
                    (money == null ? "The game's money was not found as this build expects it (the global '" + MoneyVariable + "'): the money is not shared for now. " : string.Empty)
                    + (database == null ? "The game's player database was not found ('" + DatabasePath + "', state machine '" + DatabaseMachine + "'): the money is trusted "
                        + SettleSeconds + " s after the world loads." : string.Empty));
            }
        }

        private void OnClientChanged(byte player, float amount, float balance)
        {
            log.Info(LogCategory.Fsm, "Money: player " + player + " " + Spent(amount) + "; " + WalletReplication.Text(balance) + " now.");
        }

        // "spent 12.95" or "earned 300.00".
        private static string Spent(float amount)
        {
            return (amount < 0f ? "spent " : "earned ") + WalletReplication.Text(Math.Abs(amount));
        }

        private static PlayMakerFSM Machine(GameObject owner, string name)
        {
            PlayMakerFSM[] machines = owner.GetComponents<PlayMakerFSM>();
            for (int i = 0; i < machines.Length; i++)
            {
                if (machines[i].FsmName == name)
                {
                    return machines[i];
                }
            }

            return null;
        }
    }
}
