# Разработка и ограничения MyFences

## Особенности

Используется полупрозрачный тёмный стиль без Acrylic/Mica.
Контейнеры остаются обычными прозрачными WPF-окнами с владельцем Progman/WorkerW, размещёнными внизу Z-order над Explorer. Cross-process SetParent не используется: на целевой системе он создавал невидимые поверхности.
Эта структура Explorer не является публичным API; Win+D, сторонние оболочки и нестандартные рабочие столы требуют проверки на конкретной сборке Windows.
При перезапуске Explorer программа повторно ищет рабочий стол каждые 2 секунды и пересоздаёт потерянные окна.
Quick-Hide использует WH_MOUSE_LL и UI Automation с отказом от переключения при недоступном провайдере.
Приложение не включает автозапуск и не меняет настройки Explorer.
Права на перенос системных или занятых файлов определяются Windows; ошибки показываются пользователю.

## Сборка из исходников

Требуется .NET 8 SDK x64. В корне проекта:

```powershell
dotnet build -c Release
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o artifacts\MyFences
```

`Build.ps1` использует локальный SDK `.sdk`, если он установлен, иначе системный dotnet.
Для сборки MSI запустите `Build-Msi.ps1`: скрипт установит WiX 6.0.2 в локальную папку `.tools` и соберёт пакет из опубликованного EXE.

## Проверки

`MyFences.exe --self-test` проверяет JSON, резервную копию, восстановление, перенос, конфликты имён и иконки на изолированных временных файлах.
Результат — `self-test-results.txt` рядом с EXE.

`MyFences.exe --smoke-test` открывает два тестовых блока, проверяет HWND, сворачивание, перенос и обновление сетки, скрытие/показ и JSON, после чего закрывается.
Результат — `smoke-results.txt`, снимок — `smoke-window.png`. Тест использует отдельное временное хранилище.

GUI-проверка также открывает настоящее shell-меню и проверяет создание блока из выделенного прямоугольника. Комбинации клавиш рабочему столу не отправляются.
Дополнительная проверка реальных пикселей и аппаратного жеста доступна отдельно: `--smoke-test --desktop-visual-test`; только этот явно выбранный режим переключает Win+D.
Ручные проверки на целевом компьютере: кнопка «Показать рабочий стол», перетаскивание настоящего ярлыка, разные DPI и мониторы, перезапуск Explorer.

## Технические источники

- https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-setparent
- https://learn.microsoft.com/windows/win32/api/shellapi/nf-shellapi-shgetimagelist
- https://learn.microsoft.com/windows/win32/winmsg/lowlevelmouseproc
- https://learn.microsoft.com/windows/win32/api/shobjidl_core/nn-shobjidl_core-icontextmenu3
