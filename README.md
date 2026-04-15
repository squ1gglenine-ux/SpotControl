# SpotCont 2

SpotCont 2 — быстрый лаунчер для Windows на WPF (.NET 8).
Открывается по глобальному хоткею `Alt+Space` и позволяет быстро находить приложения, файлы, папки, web-цели, alias-команды, результаты калькулятора и системные действия.

## Содержание

- [Ключевые возможности](#ключевые-возможности)
- [Как работает поиск](#как-работает-поиск)
- [Управление с клавиатуры и мыши](#управление-с-клавиатуры-и-мыши)
- [Панель Preview](#панель-preview)
- [Контекстное меню результатов](#контекстное-меню-результатов)
- [Динамическая тема](#динамическая-тема)
- [Архитектура](#архитектура)
- [Файлы данных и конфигов](#файлы-данных-и-конфигов)
- [Сборка и запуск](#сборка-и-запуск)
- [Система плагинов](#система-плагинов)
- [Структура проекта](#структура-проекта)
- [Текущие ограничения](#текущие-ограничения)

## Ключевые возможности

- Глобальный хоткей `Alt+Space` для показа/скрытия launcher.
- Поиск приложений из Start Menu, Desktop и App Paths в реестре.
- Поиск по индексируемым каталогам: `Desktop`, `Documents`, `Downloads`.
- Распознавание URL и быстрый переход на известные сайты.
- Web-поиск для любого запроса.
- Встроенный калькулятор (`calc ...` или `= ...`) с поддержкой `+`, `-`, `*`, `/`, `%`, скобок и `sqrt(...)`.
- Системные команды: shutdown, restart, lock.
- Action aliases (например: `g`, `yt`, `gh`, `w`, `maps`, `mail`).
- Inline-автодополнение и применение через `Tab`.
- Быстрый запуск видимых результатов по `Alt+1..Alt+9`.
- Правая панель Preview для выбранного файла/папки.
- Learning-rank: ранжирование с учетом частоты, давности и контекста запросов.
- Автоподстройка light/dark/accent темы под Windows.
- Поддержка внешних плагинов из папки `Extensions`.

## Как работает поиск

SpotCont запускает несколько поисковых плагинов параллельно, объединяет дубликаты, затем ранжирует финальный список.

Порядок встроенных плагинов:

1. `Autocomplete` (order 10)
2. `Action Aliases` (15)
3. `System Commands + Calculator` (20)
4. `Applications` (30)
5. `Files` (40)
6. `Web` (50)

Формула итогового `Score`:

- `MatchQuality * 0.64`
- `FrequencyScore * 0.14`
- `RecencyScore * 0.08`
- `LearningScore * 0.14`

Также применяется приоритет бакетов (калькулятор и системные команды выше web-результатов).

## Управление с клавиатуры и мыши

- `Alt+Space`: показать/скрыть launcher.
- `Up/Down`: перемещение по результатам.
- `Enter`: выполнить выбранный результат.
- `Tab`: применить автодополнение.
- `Esc`: скрыть launcher.
- `Alt+1..Alt+9`: выполнить результат по видимому индексу.
- `ПКМ` по результату: открыть контекстное меню.
- Клик вне окна или переключение фокуса на другое окно: launcher автоматически скрывается.

## Панель Preview

При выборе файла/папки отображается:

- превью изображения/иконки,
- имя и полный путь,
- тип, размер, дата изменения,
- текстовый сниппет для поддерживаемых текстовых файлов,
- сводка по папке (кол-во папок/файлов + часть содержимого).

Особенности:

- фото-форматы (`jpg`, `jpeg`, `png`, `bmp`, `gif`, `webp`, `tif`, `tiff`) показываются в большом режиме,
- иконки/системные значки показываются в compact-режиме без растягивания,
- web-иконки подгружаются через `favicon.ico` и fallback Google favicon,
- колесо мыши в Preview изолировано от скролла списка результатов.

## Контекстное меню результатов

В зависимости от типа результата доступны действия:

- Open,
- Open as administrator,
- Open file location / Open parent folder,
- Find on the internet,
- Copy value/path/link.

## Динамическая тема

`ThemeService` отслеживает:

- light/dark режим из реестра,
- accent color из DWM/Explorer,
- изменения в реальном времени (events + polling).

Палитра интерфейса обновляется на лету: окно, текст, иконки, hover/selected, контекстное меню.

## Архитектура

- UI: WPF (`MainWindow.xaml`) с кастомными стилями, анимациями, контекстным меню и Preview.
- ViewModel: `LauncherViewModel` управляет запросом, выбором, Preview, debounce и async-обновлением результатов.
- Поисковый слой: плагины (`ISearchPlugin`) через `SearchPluginHost` + `SearchEngine`.
- Выполнение действий: `ResultLauncherService` (запуск, copy, системные команды).
- Индексация приложений: `ApplicationIndexService`.
- Индексация файлов/папок с фоновыми обновлениями: `FileIndexService` + `FileSystemWatcher`.
- Иконки и медиа превью: `IconCacheService` (shell icons, thumbnails, fallback).
- Обучение выдачи: `UsageHistoryService` (история запусков и связи запрос -> результат).

## Файлы данных и конфигов

Хранятся в `%AppData%\SpotCont`:

- `usage-history.json` — счетчики запусков, recency, learning state.
- `action-aliases.json` — редактируемые alias-правила.
- `runtime.log` — журнал runtime-ошибок.

Рядом с `SpotCont.exe`:

- `Extensions\` — внешние плагины (`*.dll`) и их зависимости.

## Сборка и запуск

Требования:

- Windows от Windows 10 1803 до Windows 11 last Version
- .NET SDK 8.0+

Команды:

```powershell
dotnet restore
dotnet build SpotCont2.csproj -c Debug
```

Сборка в целевые папки:

```powershell
dotnet build SpotCont2.csproj -c Debug -o Build\Debug
dotnet build SpotCont2.csproj -c Release -o Build\Release
```

Основной исполняемый файл:

- `Build\Debug\SpotCont.exe`
- `Build\Release\SpotCont.exe`

## Система плагинов

SpotCont поддерживает внешние плагины, которые загружаются из `Extensions`.

Поддерживаемые сценарии расширения:

1. Реализовать `ISearchPlugin` с публичным конструктором без параметров.
2. Реализовать `ISearchPluginFactory` и создавать плагины через `SearchPluginContext`.

`SearchPluginContext` предоставляет доступ к:

- `ApplicationIndexService`
- `FileIndexService`
- `UsageHistoryService`
- `ExtensionsDirectory`

Подробная инструкция и минимальный пример: [Extensions/README.md](Extensions/README.md).

## Структура проекта

- `App.xaml`, `App.xaml.cs` — композиция приложения и запуск.
- `MainWindow.xaml`, `MainWindow.xaml.cs` — окно launcher, ввод, фокус, контекстное меню, анимации.
- `ViewModels/LauncherViewModel.cs` — основная UI-логика.
- `Services/` — поиск, индексация, ранжирование, запуск, тема, иконки, aliases, external loader.
- `Plugins/` — встроенные поисковые плагины.
- `Models/SearchModels.cs` — модели и enum-структуры.
- `Infrastructure/` — hotkey hook, позиционирование окна, анимации, base observable.
- `ServiceImage/` — fallback-иконки.
- `Extensions/` — документация по плагинам и runtime-папка расширений.

## Текущие ограничения

- Индексация файлов ограничена корнями: `Desktop`, `Documents`, `Downloads`.
- Web search action использует Bing URL.
- Системные power-команды выполняются сразу.
- Приложение Windows-only (`net8.0-windows`, WPF).
