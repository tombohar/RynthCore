# Contributing to RynthCore

RynthCore is the hook layer: it injects into the client, reads the game, runs plugins and hosts their windows. Features belong to plugins (RynthSuite), and features for one game server belong to that server's plugin.

## What goes here

- Hooks into the client and readers of game state (objects, inventory, vitals, positions, chat, spells).
- The plugin API (`src/RynthCore.PluginSdk`) and its versions: a new capability is a generic function every plugin can use.
- The window host: the ImGui shell, panels and pop-outs, themes, fonts, the sections rail.
- Services every plugin can use: settings files, status export, logging, hotkeys, the launcher and updater.

## What doesn't

- A feature of one plugin, including its screens: plugins draw their own windows with the engine's ImGui.
- Anything for one game server: that server's plugin in RynthSuite.

When a plugin needs something the engine can't do yet, add the generic hook here and build the feature in the plugin.

## Pull requests

- One purpose per pull request; a change that spans RynthCore and RynthSuite comes as a pull request to each, the hook first.
- Plugin API numbers and versions are assigned when a change is merged.
- Add or update the offline checks under `tools/` where they exist.
- Say in the description what you tested in game and on which server.
