# Changelog

## 1.1.0

Needs SPT 4.1.x. Install over 1.0.0; your rooms and the places you moved areas to carry over.

### Fixed

- **Stutter every 2 seconds in the menus** (stash, traders and the rest), reported with 1.0.0. The hideout stays loaded behind the menus, and two jobs kept searching it:
  - The lamps hung where a moved area used to stand: the check for whether every moved area had its lamps never came out as done, and it searched the whole hideout scene again every 2 seconds while some areas weren't set up (as they aren't in the menus). It now looks the areas up once a visit, stops once every moved area is lit, and otherwise checks less and less often.
  - Setting up the rooms: the search through the hideout for what rooms are made of now happens once per hideout. If something else removes the rooms while the hideout is still loaded, they're set up again when you next open the hideout instead of every 2 seconds (the log says so once), and a setup that keeps failing is tried less and less often.

If you still see a regular stutter in the menus with 1.1.0, please report it with your `BepInEx/LogOutput.log`.

## 1.0.0

First release.
