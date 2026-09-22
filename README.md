# Opus Converter

Конвертирует любой аудио/видео файл или прямую ссылку в `.rvoice` — формат, который плагин **OpusVoice** проигрывает
в голосовом чате Rust через NPC, посаженного на невидимый стул за спиной игрока.

## Состав репозитория

| Папка | Что это |
|---|---|
| [`OpusConverter.Gui`](OpusConverter.Gui) | Оконное приложение (WPF) — перетаскивание файлов, ссылки, прогресс, прослушивание результата |
| [`OpusConverter`](OpusConverter) | Консольная версия того же конвертера (для скриптов/автоматизации) |
| [`OpusConverter.Core`](OpusConverter.Core) | Общая библиотека конвертации (ffmpeg → Opus → `.rvoice`) |
| [`plugin/OpusVoice.cs`](plugin/OpusVoice.cs) | Плагин Carbon для Rust-сервера |
| [`OpusVoice.PluginCheck`](OpusVoice.PluginCheck) | Проверка компиляции плагина против настоящих сборок Rust/Carbon (только для разработки) |
| [`OpusVoice.Tests`](OpusVoice.Tests) | Тесты конвертера и формата пакетов голосового чата |

## Установка (готовые сборки)

Скачайте архив со страницы [Releases](../../releases):

- `OpusConverterGui-*-win-x64.zip` — окно, запускать `OpusConverterGui.exe`.
- `OpusConverterCli-*-win-x64.zip` — консольная версия.
- `OpusVoice.cs` — положить в `carbon/plugins/` на сервере.

Оба `.exe` — self-contained, .NET ставить не нужно. Отдельно нужен **ffmpeg** (программа сама подскажет, если его нет):

```bash
winget install Gyan.FFmpeg
```

## Использование

1. В программе добавьте файлы, папку или прямую ссылку на аудио/видео.
2. Настройте битрейт/частоту/громкость при необходимости.
3. Нажмите «Конвертировать» — результат попадёт в выбранную папку.
4. Скопируйте `.rvoice` файлы в `carbon/data/OpusVoice/` на сервере.
5. В игре: `/vplay <имя> [me|all|near|ник]`, `/vstop [id|all]`, `/vlist`.

Консольный вариант:

```bash
OpusConverter.exe song.mp3 -o C:\server\carbon\data\OpusVoice
OpusConverter.exe --help
```

## Сборка из исходников

Нужен .NET 8 SDK (Windows, для GUI — WPF).

```bash
dotnet build OpusConverter.Gui
dotnet build OpusConverter
```

### Тесты

`OpusVoice.Tests` компилирует плагин против настоящих сборок Rust/Carbon и прогоняет end-to-end конвертацию через
ffmpeg. Эти сборки — часть установленной игры/сервера и не входят в репозиторий (лицензионные ограничения Facepunch),
поэтому укажите путь к папке с `Assembly-CSharp.dll`, `Carbon.Common.dll` и т.д.:

```bash
dotnet test OpusVoice.Tests -p:RustDlls="C:\path\to\managed\dlls"
```

Для самих тестов конвертации (без плагина) нужен только ffmpeg в `PATH`.

## Известное ограничение

**Используйте частоту 24 кГц.** Это подтверждено на практике: `.rvoice`, сконвертированный на 48 кГц, — валидный
файл (проходит и собственную проверку конвертера, и повторное декодирование), но в самой игре NPC при воспроизведении
остаётся немым. 24 кГц — это частота, которую использует родной голосовой чат Rust, и она гарантированно работает.
GUI показывает предупреждение при выборе 48 кГц; 12 и 16 кГц не тестировались отдельно.

## Релизы

Публикуются автоматически: пуш тега вида `v1.2.3` запускает [`.github/workflows/release.yml`](.github/workflows/release.yml),
который собирает self-contained `win-x64` сборки GUI и CLI и прикладывает их вместе с `OpusVoice.cs` к релизу на GitHub.
