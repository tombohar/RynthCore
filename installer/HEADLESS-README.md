# Headless mode (experimental)

Headless runs an account with **no game window**. Instead of starting the Asheron's Call
client, the launcher starts `rynth-headless.exe`, RynthCore's own client. It logs the
character in and runs RynthAi, the way RynthAi runs inside the game, but nothing is drawn
on screen. Use it for a bot character you don't need to watch.

It is experimental: it may stop, get stuck or do something the game client wouldn't. Try it
on a character you can afford to lose time with, and check on it.

## What it can and can't do

- **ACE servers only.** The launcher refuses to start a Headless account on a GDLE server.
- **No game window and no in-game screens.** There is no RynthCore bar, no RynthAi window
  and no chat window. You give RynthAi its orders with commands (see below).
- **Which plugins run:** only 64-bit plugins. By default that is the RynthAi that comes with
  RynthCore (in `Headless\plugins\RynthAi` in the RynthCore folder). The plugins in the
  launcher's Plugins tab, Decal and Decal plugins (VTank, UtilityBelt and the rest) do not
  run in a headless client.
- **It walks on the game's own maps** (it reads your AC client's files), but it can't jump
  or swim.
- **One character per account.** As in the game, an account can have only one character in
  the world. Don't start the same account in a game client as well: the server drops one.
- **64-bit Windows only.**

## Setting it up

1. Install or update RynthCore as usual. The headless client comes with it.
2. In the launcher, set the account's injection method to **Headless (experimental)**.
3. Pick the character the same way as for any account. With none set, the first character
   on the account logs in.
4. Click the account's **OnLogin** button and add the commands RynthAi should get once the
   character is in the world, for example `/ra start` to start the macro. RynthAi uses the
   profiles, metas and loot profiles in its headless folder (below), not the ones your game
   clients use, so copy the ones you want there first.
5. Launch the account as usual.

## Where its files are

- **RynthAi's files for headless characters:** `%LOCALAPPDATA%\RynthCore\headless\RynthAi\`
  (settings and loot profiles, metas, navs). Copy profiles from `C:\Games\RynthSuite\RynthAi\`
  into the same subfolders to use them headless.
- **Log:** `%LOCALAPPDATA%\RynthCore\headless\headless-<account>.log`, one per account (what
  the character and its plugins did, and what the server refused). RynthAi's own logs are
  in `RynthAi\Logs` there.
- **Status files** (for status tools that read them): `C:\Games\RynthCore\Logs\status\`.

## Seeing it and stopping it

- The account shows in the launcher like any running client.
- To stop a headless client, use **Runtime > Headless client > Log out headless clients**. It
  asks every headless client to log its character out and close (it is closed after 20 seconds
  if it doesn't). With AutoLaunch on, uncheck the account first, or the launcher starts it again.

## Settings (Runtime tab, Headless client)

- **rynth-headless.exe:** leave it empty to use the one that comes with RynthCore.
- **Headless plugins:** leave it empty to use the RynthAi that comes with RynthCore. To list
  your own, give one 64-bit plugin DLL per line; never the ones from the Plugins tab, which
  are 32-bit and can't load in it.

To go back, set the account's injection method to **RynthCore** again.
