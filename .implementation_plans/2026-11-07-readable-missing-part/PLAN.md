# Readable "car incomplete" warning

**Goal:** Delivering a car showed "Car is incomplete. There is !rolka_gladka missing from car". Make the missing part understandable.

**Findings:** `JobHooks.EndJobHook` shows `GUI_SamochodNiezlozony` with the game's localized value of `GetMissingPartID()`. Parts without a translation show the raw (Polish) internal id. `rolka_gladka` = smooth idler pulley (belt roller) in the engine bay.

**Approach:** `FriendlyPartName` maps known ids, else uses the localization, else humanizes the id. Drops the stray `!`. Logs the raw id at Info.

**Files:** `ClientSide/Data/Garage/Campaign/JobHooks.cs`, `MainMod.cs`.

**Testing:** build + tests pass. In game: deliver a car missing a part; the window names it.

**Status:** done in 0.5.0-rc46. Follow-up: extend `PartNames` as more raw ids are reported.
