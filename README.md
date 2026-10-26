# megarohas deck

Личный плагин для стрим-деки **fifine AmpliGame D6** (софт fifine Control Deck). Подойдёт и к другим
декам на движке Mirabox StreamDock. В списке действий софта все кнопки лежат в категории **megarohas**.

## Экраны

Кнопки режима проецирования, то же, что **Win+P**:

| Кнопка | Режим |
|---|---|
| ![](com.megarohas.deck.sdPlugin/images/internal_on.png) **Только монитор** | только экран компьютера |
| ![](com.megarohas.deck.sdPlugin/images/external_on.png) **Только ТВ** | только второй экран |
| ![](com.megarohas.deck.sdPlugin/images/extend_on.png) **Расширить** | рабочий стол на оба экрана |
| ![](com.megarohas.deck.sdPlugin/images/clone_on.png) **Дублировать** | одна картинка на обоих |

Кнопка текущего режима горит, остальные тусклые. Режим проверяется раз в секунду, так что
подсветка следует и за Win+P. Если переключиться не вышло (например, ТВ отключён), на кнопке мигает значок ошибки.

## Громкость

| Кнопка | Что делает |
|---|---|
| ![](com.megarohas.deck.sdPlugin/images/mute_on.png) **Без звука** | выключает / включает звук; горит красным, пока звук выключен |
| ![](com.megarohas.deck.sdPlugin/images/volume_down.png) **Тише** | −2% (удержание — непрерывно) |
| ![](com.megarohas.deck.sdPlugin/images/volume_up.png) **Громче** | +2% (удержание — непрерывно) |

Работают с устройством вывода по умолчанию (ТВ, наушники — какое выбрано сейчас). На ± кнопках полоска
текущего уровня; пока звук выключен, она серая. Изменение громкости заодно включает звук, как у клавиш Windows.

## Файлы

- `com.megarohas.deck.sdPlugin/` — сам плагин: `manifest.json`, `ru.json`, картинки; сюда же собирается `MegarohasDeck.exe`.
- `src/` — исходники exe: `Program.cs` (WebSocket-протокол StreamDock), `Displays.cs` (режимы экранов), `Audio.cs` (громкость).
- `build.ps1` — сборка exe (csc из .NET Framework 4, ничего ставить не надо).
- `make_icons.py` — рисует картинки кнопок (SVG → PNG через Edge в headless-режиме, только стандартная библиотека Python).
- `install.ps1` — копирует плагин в `%APPDATA%\HotSpot\StreamDock\plugins` и перезапускает софт деки.

Отладка: пустой файл `debug` рядом с установленным exe — после перезапуска софта плагин пишет все входящие
сообщения и уровень громкости в `MegarohasDeck.log`; ошибки пишутся туда всегда.
