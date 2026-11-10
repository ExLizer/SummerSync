SummerSync - multiplayer for My Summer Car
==========================================

Mod version: {MOD_VERSION}

SummerSync lets 2 to 4 friends play My Summer Car together in one world: the
host's. Most of what happens around the players is shared: each player, with
the character they pick; the things you carry; the vehicles (driving, riding as
a passenger with a seat belt, doors, ignition, lights, dashboard, radio, fuel);
the time and the weather; the money, Teimo's shop and the pub; the doors,
lights and appliances of the buildings; the fire and the saunas; the traffic,
the pedestrians and the people of the world; the phone; the jobs; building the
Satsuma; dents and crashes; deaths.

It is a beta: see "Known issues" below.

(Español: LEEME.txt)


What you need
-------------
- My Summer Car on Steam, up to date and without betas. Every player needs the
  same version of the game and the same build of SummerSync: the same zip.
  Two different builds do not connect, even with the same version number.
- MSCLoader, the mod loader, installed in the game. SummerSync does NOT include
  it nor install it: get it and install it yourself from its official source.
  SummerSync was tested with MSCLoader 1.4.2.
- To be Steam friends with the host.


Install
-------
1. Install MSCLoader yourself if you do not have it.
2. Close the game. Copy "2-Mod\SummerSync.dll" from this zip to the folder
   where MSCLoader keeps the mods. If you installed MSCLoader in the game's
   folder: in Steam, right click My Summer Car > Manage > Browse local files,
   and open the "Mods" folder. The other two possible folders are:
      Documents\MySummerCar\Mods
      %USERPROFILE%\AppData\LocalLow\Amistech\My Summer Car\Mods
3. Start the game FROM STEAM (from the library, not from the .exe or a
   shortcut): otherwise Steam does not work inside the game and invites do not
   arrive.
4. For Spanish: Mods > SummerSync > "Language / Idioma".


Playing
-------
- To host: in the main menu, HOST. Pick a save or start a new one, then
  INVITE your friends (Steam overlay) and ENTER GAME.
- To join: accept the host's invite in Steam (Shift+Tab, or the Steam chat)
  while you are in the main menu, wait until the host's world has arrived and
  press ENTER GAME. You play in the host's world; your own save is never
  touched.
- Your character: "Change character" in the main menu, or F7 in the game.
- Chat: press Y, type, and Enter to send it to everyone (Esc closes it). It
  works in the main menu too, once you are connected, and the game does not
  react to what you type. The key can be changed in the mod's keys.
- Passenger seats: stand by a free passenger seat and press the driving mode
  key ("ENTER PASSENGER MODE"). The seat belt works like the driver's. The
  tractor's roof is a seat too: stand beside or behind the tractor.
- Permanent death: on the host's save card, "Permanent death: YES/NO". With it
  off, a player who dies wakes up at home.
- Only the host saves the world. Going back to the main menu ends the session.


Settings (Mods > SummerSync)
----------------------------
- Language: English or Spanish.
- Show connection info (F8): a small text box with the state of the session.
- Import the songs of the game's Radio folder when the game opens.
- Open MSCLoader's console on the mod's warnings and errors: off unless you
  want it. With it off they are still in the console when you open it.
- Invite a friend, and Disconnect.


Updating
--------
The main menu tells you when there is a new version. Get it at
mysummersync.com and replace SummerSync.dll in the Mods folder. All players
must have the same build: the same zip.


Known issues
------------
- The pub's fighter and drunks are still each player's own.
- Saving the game takes you back to the main menu, as it always does, and ends
  the session for everyone. While it saves the game stops answering for about
  10 seconds (Windows may say "Not responding"): wait, do not close it.
- Loading screens: everyone waits in the main menu while the host's world
  arrives; going back to the menu ends the session.
- Compatibility with other mods has not been tested.


If something goes wrong
-----------------------
Send the host (or the mod's page at mysummersync.com) the whole folder
   <Mods folder>\Config\Mod Settings\SummerSync\
compressed (right click > Send to > Compressed folder). It holds the log of the
last time you opened the game (SummerSync.log) and the five before it. A
screenshot with the connection info visible (F8) helps too.


What is shared between players
------------------------------
- The host's world: the host's save is sent to the guests, who play in a copy
  kept in the mod's settings folder.
- The host's radio songs (the files of the Radio folder) are sent to the guests
  so that everyone hears the same.
- Some of the guests' log lines (warnings and part of what happens) are sent
  to the host's log. The logs hold your Steam name, your Steam ID and the folder where
  the game is installed.
- To know if there is a new version, the mod asks which one is the latest
  published (a small file on mysummersync.com, fetched by the Steam client). Nothing else
  leaves your computer.


Uninstall
---------
Delete Mods\SummerSync.dll. What the mod keeps (the copy of the host's world,
the received songs, your settings) is in Mods\Config\Mod Settings\SummerSync\:
it can be deleted. Your own save was never touched. MSCLoader is not part of
SummerSync: uninstall it the way MSCLoader says.
