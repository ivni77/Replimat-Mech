# Replimat Mech

English · [Русский](#русский)

A RimWorld 1.6 mod (Core + Biotech): a copier for your colony's production — an unofficial fork of [Replimat](https://github.com/sumghai/Replimat) and Replimat Universal.

The matter splitter splits items and corpses into matter (mass and value) and records their patterns.
Replicators print from those patterns, paying time, mass and value.
A mechanitor sets up the print queues through a comms console.
Mechanoids are printed like in a gestator.
Nothing living is printed.

Requires [Harmony](https://steamcommunity.com/sharedfiles/filedetails/?id=2009463077) and Biotech. Works with [Multiplayer](https://steamcommunity.com/sharedfiles/filedetails/?id=2606448745). Incompatible with Replimat and Replimat Universal. Languages: English, Russian.

## Credits and license

- **Replimat** — original mod, art and sounds: **Robin "sumghai" Chang and Dubwise56** ([GitHub](https://github.com/sumghai/Replimat), [Steam Workshop](https://steamcommunity.com/sharedfiles/filedetails/?id=1715402900))
- **Replimat Universal** — changes and art: **Omnot** ([Steam Workshop](https://steamcommunity.com/sharedfiles/filedetails/?id=3806650178))

Licensed under [Creative Commons Attribution-ShareAlike 4.0 International (CC BY-SA 4.0)](https://creativecommons.org/licenses/by-sa/4.0/), like the original Replimat; see [LICENSE.md](LICENSE.md) for the attribution and list of changes. Not endorsed by the original authors.

## Building

`dotnet build Source -c Release` builds `Assemblies/Replimat Mech.dll` (.NET Framework 4.8). The game and Harmony assemblies are referenced from the default Steam paths on macOS; elsewhere pass `-p:RimWorld=<RimWorld Managed folder> -p:Harmony=<Harmony Assemblies folder> -p:Multiplayer=<Multiplayer 1.6/Assemblies folder>`. The Multiplayer API (`0MultiplayerAPI.dll`, MIT) is copied into `Assemblies` and ships with the mod.

`Tools/release.sh` makes a clean copy of the mod in `Release/Replimat Mech` for upload to the Steam Workshop. The Workshop description is kept in [Workshop/Description.bbcode](Workshop/Description.bbcode).

---

## Русский

Мод для RimWorld 1.6 (Core + Biotech): копировальная машина для производства колонии — неофициальный форк [Replimat](https://github.com/sumghai/Replimat) и Replimat Universal.

Расщепитель материи расщепляет предметы и трупы на материю (массу и стоимость) и записывает их шаблоны.
Репликаторы печатают по шаблонам за время, массу и стоимость.
Механитор настраивает очереди печати через консоль связи.
Механоиды печатаются как в гестаторе.
Живое не печатается.

Нужны [Harmony](https://steamcommunity.com/sharedfiles/filedetails/?id=2009463077) и Biotech. Работает в [Multiplayer](https://steamcommunity.com/sharedfiles/filedetails/?id=2606448745). Несовместим с Replimat и Replimat Universal. Языки: английский, русский.

### Авторы и лицензия

- **Replimat** — исходный мод, графика и звуки: **Robin "sumghai" Chang и Dubwise56** ([GitHub](https://github.com/sumghai/Replimat), [Мастерская Steam](https://steamcommunity.com/sharedfiles/filedetails/?id=1715402900))
- **Replimat Universal** — изменения и графика: **Omnot** ([Мастерская Steam](https://steamcommunity.com/sharedfiles/filedetails/?id=3806650178))

Лицензия [CC BY-SA 4.0](https://creativecommons.org/licenses/by-sa/4.0/deed.ru), как у оригинального Replimat; атрибуция и список изменений — в [LICENSE.md](LICENSE.md). Мод неофициальный: авторы оригинала его не одобряли.

### Сборка

`dotnet build Source -c Release` собирает `Assemblies/Replimat Mech.dll` (.NET Framework 4.8). Сборки игры и Harmony берутся из стандартных путей Steam на macOS; в других системах передайте `-p:RimWorld=<папка Managed игры> -p:Harmony=<папка Assemblies мода Harmony> -p:Multiplayer=<папка 1.6/Assemblies мода Multiplayer>`. API Multiplayer (`0MultiplayerAPI.dll`, MIT) копируется в `Assemblies` и едет с модом.

`Tools/release.sh` делает чистую копию мода в `Release/Replimat Mech` для выгрузки в Мастерскую Steam. Описание для Мастерской — в [Workshop/Description.bbcode](Workshop/Description.bbcode).
