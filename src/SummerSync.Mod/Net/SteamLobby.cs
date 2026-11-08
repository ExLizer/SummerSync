using System;
using Steamworks;
using SummerSync.Core;
using SummerSync.Core.Logging;

namespace SummerSync.Unity.Net
{
    /// <summary>
    /// A friends-only Steam lobby used only to find each other: the host creates it and invites through the Steam
    /// overlay, the guest accepts, and the lobby tells the guest who the host is. Game traffic does not go
    /// through the lobby.
    ///
    /// Its callbacks belong to one Steam session. The game restarts Steam when it returns to the main menu, so
    /// an instance must be disposed before that and a new one created afterwards.
    /// </summary>
    internal sealed class SteamLobby : IDisposable
    {
        private const string VersionKey = "summersync";

        private readonly Logger log;
        private readonly CallResult<LobbyCreated_t> created;
        private readonly CallResult<LobbyEnter_t> joined;
        private readonly Callback<LobbyEnter_t> entered;
        private readonly Callback<GameLobbyJoinRequested_t> joinRequested;
        private readonly Callback<LobbyChatUpdate_t> membersChanged;

        // Creating and joining are answered later. If the player disconnected in between, the answer finds
        // nobody waiting and the lobby is left again instead of lingering without a session.
        private bool expecting;

        // Steam can announce the same entry twice, as a callback and as the result of the join call.
        private ulong announcedLobby;

        public SteamLobby(Logger log)
        {
            this.log = log;
            created = CallResult<LobbyCreated_t>.Create(OnCreated);
            joined = CallResult<LobbyEnter_t>.Create(OnJoinResult);
            entered = Callback<LobbyEnter_t>.Create(OnEntered);
            joinRequested = Callback<GameLobbyJoinRequested_t>.Create(OnJoinRequested);
            membersChanged = Callback<LobbyChatUpdate_t>.Create(OnMembersChanged);
        }

        /// <summary>The local player accepted an invite or picked "Join game": lobby id to join.</summary>
        public event Action<ulong> JoinRequested;

        /// <summary>Entered a lobby owned by someone else: the owner's Steam ID, who is the host to connect to.</summary>
        public event Action<ulong> JoinedAsGuest;

        /// <summary>Creating the lobby failed: the reason.</summary>
        public event Action<string> CreateFailed;

        /// <summary>Somebody entered or left the lobby we are in.</summary>
        public event Action MembersChanged;

        public ulong LobbyId { get; private set; }

        public bool InLobby
        {
            get { return LobbyId != 0; }
        }

        public bool IsOwner { get; private set; }

        public int MemberCount
        {
            get { return InLobby ? SteamMatchmaking.GetNumLobbyMembers(new CSteamID(LobbyId)) : 0; }
        }

        public void Create(int maxMembers)
        {
            expecting = true;
            SteamAPICall_t call = SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypeFriendsOnly, maxMembers);
            created.Set(call, null);
            log.Info(LogCategory.Net, "Creating a friends-only Steam lobby for " + maxMembers + " players...");
        }

        public void Join(ulong lobbyId)
        {
            expecting = true;

            // The answer is listened for both ways: this old Steamworks documents it as the result of the call,
            // and it is also known to arrive as a plain callback. Whichever comes first is used.
            joined.Set(SteamMatchmaking.JoinLobby(new CSteamID(lobbyId)), null);
            log.Info(LogCategory.Net, "Joining Steam lobby " + lobbyId + "...");
        }

        public void OpenInviteDialog()
        {
            if (InLobby)
            {
                SteamFriends.ActivateGameOverlayInviteDialog(new CSteamID(LobbyId));
            }
        }

        public void Leave()
        {
            expecting = false;
            if (!InLobby)
            {
                return;
            }

            LeaveOnSteam(LobbyId);
            LobbyId = 0;
            IsOwner = false;
            announcedLobby = 0;
        }

        public bool IsMember(ulong steamId)
        {
            if (!InLobby)
            {
                return false;
            }

            var lobby = new CSteamID(LobbyId);
            int count = SteamMatchmaking.GetNumLobbyMembers(lobby);
            for (int i = 0; i < count; i++)
            {
                if (SteamMatchmaking.GetLobbyMemberByIndex(lobby, i).m_SteamID == steamId)
                {
                    return true;
                }
            }

            return false;
        }

        public void Dispose()
        {
            created.Dispose();
            joined.Dispose();
            entered.Dispose();
            joinRequested.Dispose();
            membersChanged.Dispose();
        }

        private void LeaveOnSteam(ulong lobbyId)
        {
            try
            {
                SteamMatchmaking.LeaveLobby(new CSteamID(lobbyId));
                log.Info(LogCategory.Net, "Left Steam lobby " + lobbyId + ".");
            }
            catch (InvalidOperationException)
            {
                // Steam is already shut down, which took us out of the lobby anyway.
            }
        }

        private bool IsStale(ulong lobbyId)
        {
            if (expecting || lobbyId == LobbyId)
            {
                return false;
            }

            log.Info(LogCategory.Net, "Steam lobby " + lobbyId + " became ready after it was no longer wanted.");
            LeaveOnSteam(lobbyId);
            return true;
        }

        private void OnCreated(LobbyCreated_t result, bool ioFailure)
        {
            if (ioFailure || result.m_eResult != EResult.k_EResultOK)
            {
                string reason = ioFailure ? "Steam I/O failure" : result.m_eResult.ToString();
                log.Error(LogCategory.Net, "Could not create the Steam lobby: " + reason + ".");
                if (expecting)
                {
                    expecting = false;
                    Raise(CreateFailed, reason);
                }

                return;
            }

            if (IsStale(result.m_ulSteamIDLobby))
            {
                return;
            }

            expecting = false;
            LobbyId = result.m_ulSteamIDLobby;
            IsOwner = true;
            SteamMatchmaking.SetLobbyData(new CSteamID(LobbyId), VersionKey, BuildInfo.Version);
            log.Info(LogCategory.Net, "Steam lobby " + LobbyId + " created. Invite a friend from the Steam overlay.");
        }

        private void OnJoinResult(LobbyEnter_t result, bool ioFailure)
        {
            if (ioFailure)
            {
                expecting = false;
                log.Error(LogCategory.Net, "Could not enter the Steam lobby: Steam I/O failure.");
                return;
            }

            OnEntered(result);
        }

        private void OnEntered(LobbyEnter_t result)
        {
            if (result.m_ulSteamIDLobby == announcedLobby)
            {
                return;
            }

            if (result.m_EChatRoomEnterResponse != (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess)
            {
                expecting = false;
                log.Error(LogCategory.Net, "Could not enter Steam lobby " + result.m_ulSteamIDLobby + ": " + (EChatRoomEnterResponse)result.m_EChatRoomEnterResponse + ".");
                return;
            }

            if (IsStale(result.m_ulSteamIDLobby))
            {
                return;
            }

            announcedLobby = result.m_ulSteamIDLobby;
            expecting = false;
            LobbyId = result.m_ulSteamIDLobby;
            ulong owner = SteamMatchmaking.GetLobbyOwner(new CSteamID(LobbyId)).m_SteamID;
            IsOwner = owner == SteamUser.GetSteamID().m_SteamID;
            log.Info(LogCategory.Net, "Entered Steam lobby " + LobbyId + " (" + MemberCount + " member(s), owner " + owner + (IsOwner ? ", that is us" : string.Empty) + ").");
            if (IsOwner)
            {
                return;
            }

            // Only a hint for the log: the handshake with the host is what decides, and explains, a mismatch.
            string hostVersion = SteamMatchmaking.GetLobbyData(new CSteamID(LobbyId), VersionKey);
            if (string.IsNullOrEmpty(hostVersion))
            {
                log.Warning(LogCategory.Net, "The lobby carries no SummerSync version. It may belong to another mod.");
            }
            else if (hostVersion != BuildInfo.Version)
            {
                log.Warning(LogCategory.Net, "The lobby was created by SummerSync " + hostVersion + "; this is " + BuildInfo.Version + ".");
            }

            Raise(JoinedAsGuest, owner);
        }

        private void OnJoinRequested(GameLobbyJoinRequested_t request)
        {
            log.Info(LogCategory.Net, "Steam asks to join lobby " + request.m_steamIDLobby.m_SteamID + " (invite from " + request.m_steamIDFriend.m_SteamID + ").");
            Raise(JoinRequested, request.m_steamIDLobby.m_SteamID);
        }

        private void OnMembersChanged(LobbyChatUpdate_t update)
        {
            if (update.m_ulSteamIDLobby != LobbyId)
            {
                return;
            }

            bool joined = (update.m_rgfChatMemberStateChange & (uint)EChatMemberStateChange.k_EChatMemberStateChangeEntered) != 0;
            log.Info(
                LogCategory.Net,
                "Steam lobby: " + SteamFriends.GetFriendPersonaName(new CSteamID(update.m_ulSteamIDUserChanged)) + " (" + update.m_ulSteamIDUserChanged + ") "
                + (joined ? "joined" : "left") + ". Members now: " + MemberCount + ".");

            Action handler = MembersChanged;
            if (handler != null)
            {
                Raise<object>(ignored => handler(), null);
            }
        }

        // Called from inside Steam's callback dispatch: a throwing subscriber must not disturb it.
        private void Raise<T>(Action<T> handler, T argument)
        {
            if (handler == null)
            {
                return;
            }

            try
            {
                handler(argument);
            }
            catch (Exception exception)
            {
                log.Error(LogCategory.Net, "A lobby event handler failed: " + exception);
            }
        }
    }
}
