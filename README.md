# DiskPulse

Кроссплатформенный анализатор и очиститель дисков для Windows и Linux (.NET 10 + Avalonia 12.1.6). Работает как самостоятельное desktop-приложение без постоянного запроса прав администратора/root при запуске и как подключаемый модуль (`diskpulse`) для экосистемы **PCPulse**.

## Архитектура

- `src/DiskPulse.Core` (`net10.0`) — бизнес-логика, модели, настройки, сервисы и платформенные реализации для Windows (`Win32`, реестр, Shell Recycle Bin) и Linux (`/proc/mounts`, FreeDesktop Trash, `dpkg`/`rpm`/`pacman`/`flatpak` + `.desktop`).
- `src/DiskPulse.Helper` (`net10.0`) — отдельный консольный/headless помощник (`DiskPulse.Helper.exe` / `DiskPulse.Helper`), вызываемый только по требованию через UAC (`runas`) или `pkexec`. Валидирует аргументы, пути обмена JSON, разрешённые корни и отклоняет traversal/symlink/reparse-point.
- `src/DiskPulse.PulseModule` (`net10.0`) — модуль PCPulse, реализующий `PCPulse.Contracts.IPulseModule`, корневой `DiskPulseRootView` и `module.json`. Все диалоги и подтверждения идут через `IPulseHostContext.Dialogs`.
- `src/DiskPulse.App` (`net10.0`) — standalone-оболочка на Avalonia (`ThemeVariant.Dark`), оборачивающая `DiskPulseModule` и `StandaloneHostContext`. Манифест Windows: `asInvoker` (без UAC при старте).
- `tests/DiskPulse.Tests` (`net10.0`) — модульные тесты сканирования, политики возраста, исключений, безопасности helper-протокола, Linux-парсеров и жизненного цикла `IPulseModule`.

## Возможности

- **Очистка и кэши**:
  - Windows: пользовательский `%TEMP%`, кэши браузеров (Chrome, Edge, Firefox, Brave, Vivaldi, Opera, Яндекс Браузер), приложений (VS Code, Discord, Slack), `CrashDumps`, `Minidump`, `D3DSCache`, `INetCache`, `thumbcache_*.db`, системные временные каталоги/WER/`SoftwareDistribution\Download`, корзина и кэши разработки (`NuGet`, `npm`, `Yarn`, `pnpm`, `pip`).
  - Linux: пользовательские `~/.cache`, кэши браузеров/приложений, `FreeDesktop.org Trash`, `/tmp` и `/var/tmp` (с защитой системных сокетов `systemd-private-*`, `.X11-unix`, `pulse-*`, `dbus-*`, `ssh-*`, `snap-private-tmp` и др.), `/var/crash`, кэши `apt`/`pacman`/`dnf`, кэши разработки.
- **Программы**:
  - Windows: чтение ключей `Uninstall` (64/32-бит), давность использования (`UserAssist` / `Prefetch`), штатный деинсталлятор, поиск и очистка остаточных файлов и записей реестра.
  - Linux: чтение `.desktop`-файлов и пакетов `dpkg` / `rpm` / `pacman` / `flatpak`, сопоставление с давностью использования (`recently-used.xbel`), открытие каталогов (`xdg-open`), поиск пользовательских остатков (`~/.config`, `~/.local/share`, `~/.cache`, `~/.var/app`), удаление через `pkexec` или подсказка пакетного менеджера.
- **Прочее**: диаграмма занятого места на выбранных томах, предварительный просмотр и фильтрация файлов с постоянным списком исключений, история очисток, экспорт отчётов в CSV/JSON.

## Сборка и тесты

Перед первым восстановлением локально загрузите пакет `PCPulse.Contracts` 1.0.0 в `.packages/`:

```powershell
# Windows (PowerShell)
.\scripts\restore-contracts.ps1
dotnet restore DiskPulse.sln --locked-mode
dotnet test DiskPulse.sln -c Release --no-restore
```

```bash
# Linux (bash)
bash scripts/restore-contracts.sh
dotnet restore DiskPulse.sln --locked-mode
dotnet test DiskPulse.sln -c Release --no-restore
```

## Упаковка standalone-версий и модуля PCPulse

- **Windows standalone (`win-x64`)**:
  - `.\scripts\publish-windows.ps1` — собирает single-file `publish\win-x64\DiskPulse.exe` и `DiskPulse.Helper.exe`.
  - `.\scripts\build-installer.ps1` — собирает per-user установщик Inno Setup `publish\installer\DiskPulse-Setup.exe` (`PrivilegesRequired=lowest`, каталог `{localappdata}\Programs\DiskPulse`).
- **Linux standalone (`linux-x64` / `linux-arm64`)**:
  - `bash scripts/publish-linux.sh linux-x64` — собирает архив `publish/DiskPulse-<version>-linux-x64.tar.gz` со скриптом пользовательской установки `install.sh` (`~/.local/share/diskpulse`).
- **Модуль PCPulse**:
  - `.\scripts\package-module.ps1 -Runtime win-x64` (или `linux-x64` / `linux-arm64`) и `bash scripts/package-module.sh <rid>` создают в `publish/modules/`:
    - `diskpulse-module-<version>.manifest.json`
    - `diskpulse-module-<version>-<rid>.zip` (без дубликатов `PCPulse.Contracts.dll` и runtime `Avalonia.*`)
    - `diskpulse-module-<version>-<rid>.zip.sha256`

## Версия и данные пользователя

- Единый источник версии — `<Version>` в `Directory.Build.props`.
- В standalone-режиме данные хранятся в `%LOCALAPPDATA%\Cleaner` (Windows) или `~/.local/share/DiskPulse` (Linux). В режиме модуля PCPulse используется переданный хостом `IPulseHostContext.ModuleDataDirectory`.
