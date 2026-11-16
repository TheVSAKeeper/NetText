# Сравнение NetText (.NET 11 NativeAOT) и RustText (Rust + GPUI Kit)

Дата замера: 2026-11-17 02:31:12 | Повторов старта: 30 (чередующийся порядок, Median / P95) | Прогонов E2E: 5

## 1. Среда, исходники и воспроизводимость

- **ОС**: Майкрософт Windows 11 Pro (10.0.26200, 64-разрядная).
- **Инструментарий**: .NET SDK `11.0.100` (NativeAOT + VC++), `rustc 1.99.1 (58cf8f9c3 2026-10-13)`, `cargo 1.99.1 (5f94df478 2026-08-27)`, `Scintilla.dll` 5.6.9 / `Lexilla.dll` 5.5.5.
- **Состояние репозиториев и бинарников**: `RustText` ветка `perf/bench-probes` (коммит `a97680f`); `NetText` ветка `main` (коммит `2956022`); SHA-256 измеренных бинарников и сырые данные каждого запуска сохранены в [comparison-raw.json](comparison-raw.json).

## 2. Код, зависимости, тесты и сборка

| Параметр                                       |                                              RustText |                                                                   NetText |
| ---------------------------------------------- | ----------------------------------------------------: | ------------------------------------------------------------------------: |
| Файлы кода (`src`)                             |                                       35 (9279 строк) |                                                          24 (11353 строк) |
| Файлы тестов (`tests`)                         |                                       17 (1971 строк) |                                                             4 (911 строк) |
| Прямые зависимости манифеста                   |                  12 runtime (+0 build) в `Cargo.toml` |                0 NuGet + `Scintilla.dll` / `Lexilla.dll` + Win32 P/Invoke |
| Транзитивный граф (Windows x64)                |          469 runtime / 492 всего (828 в `Cargo.lock`) |   Стандартная библиотека .NET 11 BCL (ILC NativeAOT) + prebuilt Win32 DLL |
| Покрытие модульными тестами                    | 96 тестов (`rusttext`, всего 125 по таргетам; 1.45 с) |                                       49 тестов (`NetText.Tests`; 2.29 с) |
| Инкрементальная сборка (после правки кода)     |                                                9.30 с |                                                                    4.08 с |
| Чистая сборка (`cargo clean` / `dotnet clean`) |                                              107.37 с |                                                                    3.84 с |
| Релизная поставка на диске                     |                     28.25 MiB (1 файл `rusttext.exe`) | 8.35 MiB (`NetText.exe` 5.92 + `Scintilla.dll` 1.08 + `Lexilla.dll` 1.35) |

- Длительность прогона тестов отражает только время выполнения разных наборов проверок и не измеряет качество или полноту тестирования.

## 3. Матрица функционального соответствия и границы вывода

| Подсистема                                                            | RustText                          | NetText                                              |
| --------------------------------------------------------------------- | --------------------------------- | ---------------------------------------------------- |
| Текстовый редактор (вкладки, UTF-8/UTF-16/Win-1251, подсветка)        | Есть (GPUI EditorState + syntect) | Есть (Win32 Scintilla + Lexilla)                     |
| Поиск и замена (обычный/regex, по вкладкам, `$1`)                     | Есть (`search.rs`)                | Есть (`EditorSearch.cs`, UTF-8 и string пути)        |
| Форматирование JSON (`Ctrl+Shift+J`, строгий парсер)                  | Есть (`serde_json`)               | Есть (`Utf8JsonReader` + строгий суррогат-валидатор) |
| Большие логи 10–20+ GiB (индекс, jump %, scroll, search/cancel, trim) | Есть (`log_io` + GPUI)            | Есть (`LogIo` + Win32 GDI `LogViewport` + LRU cache) |
| Сессия, недавние, SingleInstance IPC                                  | Есть (GPUI / Named Pipe)          | Есть (Named Pipe v2 + журнал сессии)                 |
| Автообновление из GitHub Releases + SHA-256                           | Есть (`updater.rs`)               | Нет (не портировалось)                               |

- **Границы вывода**: замеры относятся к этим двум конкретным реализациям (Win32/GDI + Scintilla/DirectWrite в `NetText` против GPUI Kit / DirectX в `RustText`) и ничего не доказывают о преимуществе .NET над Rust или наоборот.

## 4. Запуск и первичная готовность (Median / P95 по 30 прогонам)

| Сценарий   | Метрика             |            RustText |             NetText | Преимущество            |
| ---------- | ------------------- | ------------------: | ------------------: | ----------------------- |
| empty      | Окно, мс            |        303 / 600 мс |        126 / 297 мс | NetText в 2.40×         |
| empty      | 1-й кадр, мс        |        238 / 278 мс |          31 / 39 мс | NetText в 7.69×         |
| empty      | Working Set, MiB    |   81.81 / 82.83 MiB |   62.55 / 62.82 MiB | NetText: −23.5% (1.31×) |
| empty      | Peak WS, MiB        |   81.81 / 82.83 MiB |   62.55 / 62.83 MiB | NetText: −23.5% (1.31×) |
| empty      | Private Memory, MiB |   95.92 / 96.96 MiB |   66.64 / 67.67 MiB | NetText: −30.5% (1.44×) |
| 40MB .txt  | Окно, мс            |        240 / 426 мс |         27 / 232 мс | NetText в 8.87×         |
| 40MB .txt  | 1-й кадр, мс        |        223 / 267 мс |          19 / 24 мс | NetText в 11.74×        |
| 40MB .txt  | Готовность, мс      |        424 / 472 мс |        199 / 232 мс | NetText в 2.13×         |
| 40MB .txt  | Working Set, MiB    | 191.45 / 192.12 MiB | 145.34 / 145.70 MiB | NetText: −24.1% (1.32×) |
| 40MB .txt  | Peak WS, MiB        | 209.23 / 209.94 MiB | 153.06 / 154.35 MiB | NetText: −26.8% (1.37×) |
| 40MB .txt  | Private Memory, MiB | 205.77 / 206.98 MiB | 149.02 / 149.87 MiB | NetText: −27.6% (1.38×) |
| 40MB .json | Окно, мс            |        272 / 384 мс |          30 / 91 мс | NetText в 9.07×         |
| 40MB .json | 1-й кадр, мс        |        238 / 311 мс |          22 / 27 мс | NetText в 10.60×        |
| 40MB .json | Готовность, мс      |        442 / 523 мс |        527 / 671 мс | RustText в 1.19×        |
| 40MB .json | Working Set, MiB    | 234.81 / 235.49 MiB | 141.56 / 141.90 MiB | NetText: −39.7% (1.66×) |
| 40MB .json | Peak WS, MiB        | 260.42 / 260.96 MiB | 148.77 / 149.22 MiB | NetText: −42.9% (1.75×) |
| 40MB .json | Private Memory, MiB | 274.80 / 275.66 MiB | 145.25 / 146.03 MiB | NetText: −47.1% (1.89×) |
| 10GiB .txt | Окно, мс            |        274 / 444 мс |         36 / 266 мс | NetText в 7.70×         |
| 10GiB .txt | 1-й кадр, мс        |        256 / 294 мс |          23 / 28 мс | NetText в 11.15×        |
| 10GiB .txt | Готовность, мс      |        262 / 299 мс |          34 / 50 мс | NetText в 7.72×         |
| 10GiB .txt | Working Set, MiB    |   87.28 / 88.06 MiB |   33.17 / 33.35 MiB | NetText: −62.0% (2.63×) |
| 10GiB .txt | Peak WS, MiB        |   87.28 / 88.06 MiB |   33.17 / 33.35 MiB | NetText: −62.0% (2.63×) |
| 10GiB .txt | Private Memory, MiB | 100.87 / 102.08 MiB |   10.77 / 11.01 MiB | NetText: −89.3% (9.37×) |
| 20GiB .txt | Окно, мс            |        225 / 247 мс |          24 / 32 мс | NetText в 9.38×         |
| 20GiB .txt | 1-й кадр, мс        |        221 / 239 мс |          19 / 21 мс | NetText в 11.63×        |
| 20GiB .txt | Готовность, мс      |        225 / 243 мс |          27 / 35 мс | NetText в 8.33×         |
| 20GiB .txt | Working Set, MiB    |   87.31 / 87.79 MiB |   33.20 / 33.39 MiB | NetText: −62.0% (2.63×) |
| 20GiB .txt | Peak WS, MiB        |   87.31 / 87.79 MiB |   33.20 / 33.39 MiB | NetText: −62.0% (2.63×) |
| 20GiB .txt | Private Memory, MiB | 100.94 / 101.39 MiB |   10.83 / 11.02 MiB | NetText: −89.3% (9.32×) |

- Значения указаны в формате `Median / P95`; «Паритет» определяется эвристическими порогами скрипта (`|Δ| ≤ 2 мс` или `< 1.05×` по медиане времени; `< 1.02×` по памяти) без формального теста статистической эквивалентности.
- `Готовность, мс` измеряет завершение первичной отрисовки нативными для каждого стека событиями: в `NetText` – синхронный возврат `WM_PAINT`/`BitBlt` и `Scintilla SCN_PAINTED`; в `RustText` – вложенный GPUI `window.on_next_frame` (выполняется строго после `window.draw()` и `window.present()` на следующем тике кадра и потому включает дополнительный цикл кадра/презентации порядка одного или нескольких интервалов VSync под нагрузкой).

## 5. Полная нагрузка E2E и профиль памяти во времени (Median / P95 по 5 прогонам)*

| Сценарий         | Метрика                      |            RustText |             NetText | Преимущество            |
| ---------------- | ---------------------------- | ------------------: | ------------------: | ----------------------- |
| 10GiB E2E        | 1-й кадр лога, мс            |        212 / 227 мс |          25 / 39 мс | NetText в 8.48×         |
| 10GiB E2E        | Полный индекс, мс            |      5210 / 6163 мс |      1975 / 2122 мс | NetText в 2.64×         |
| 10GiB E2E        | Jump 50%, мс                 |           6 / 10 мс |            3 / 4 мс | NetText в 2.00×         |
| 10GiB E2E        | Jump EOF, мс                 |          12 / 13 мс |            2 / 8 мс | NetText в 6.00×         |
| 10GiB E2E        | Scroll Prev, мс              |          11 / 12 мс |            2 / 8 мс | NetText в 5.50×         |
| 10GiB E2E        | Search EOF, мс               |    21644 / 23047 мс |      2475 / 2987 мс | NetText в 8.75×         |
| 10GiB E2E        | Cancel поиска, мс            |            0 / 0 мс |            0 / 0 мс | Паритет                 |
| 10GiB E2E        | Private (1-й кадр), MiB      |   97.05 / 97.60 MiB |   10.64 / 10.87 MiB | NetText: −89.0% (9.12×) |
| 10GiB E2E        | Private (индекс), MiB        | 100.91 / 100.97 MiB |   17.59 / 17.66 MiB | NetText: −82.6% (5.74×) |
| 10GiB E2E        | Private (после E2E), MiB     | 100.15 / 100.35 MiB |   25.67 / 25.79 MiB | NetText: −74.4% (3.90×) |
| 10GiB E2E        | Working Set (после E2E), MiB |   88.25 / 88.47 MiB |   48.75 / 48.88 MiB | NetText: −44.8% (1.81×) |
| 10GiB E2E        | Peak WS (за E2E), MiB        |   92.08 / 92.45 MiB |   48.75 / 48.88 MiB | NetText: −47.1% (1.89×) |
| Mixed 40MB+10GiB | 1-й кадр лога, мс            |        424 / 430 мс |          70 / 75 мс | NetText в 6.06×         |
| Mixed 40MB+10GiB | Полный индекс, мс            |      6337 / 6594 мс |      2006 / 2018 мс | NetText в 3.16×         |
| Mixed 40MB+10GiB | Jump 50%, мс                 |          10 / 13 мс |            3 / 3 мс | NetText в 3.33×         |
| Mixed 40MB+10GiB | Jump EOF, мс                 |          12 / 12 мс |            2 / 2 мс | NetText в 6.00×         |
| Mixed 40MB+10GiB | Scroll Prev, мс              |          13 / 16 мс |            6 / 8 мс | NetText в 2.17×         |
| Mixed 40MB+10GiB | Search EOF, мс               |    23144 / 24992 мс |      2468 / 2501 мс | NetText в 9.38×         |
| Mixed 40MB+10GiB | Cancel поиска, мс            |            0 / 0 мс |            0 / 0 мс | Паритет                 |
| Mixed 40MB+10GiB | Private (1-й кадр), MiB      | 204.97 / 206.29 MiB |   96.45 / 97.03 MiB | NetText: −52.9% (2.13×) |
| Mixed 40MB+10GiB | Private (индекс), MiB        | 205.65 / 206.63 MiB | 100.62 / 100.71 MiB | NetText: −51.1% (2.04×) |
| Mixed 40MB+10GiB | Private (после E2E), MiB     | 206.61 / 207.17 MiB | 108.79 / 108.95 MiB | NetText: −47.3% (1.90×) |
| Mixed 40MB+10GiB | Working Set (после E2E), MiB | 194.16 / 194.59 MiB | 133.19 / 133.27 MiB | NetText: −31.4% (1.46×) |
| Mixed 40MB+10GiB | Peak WS (за E2E), MiB        | 207.74 / 208.93 MiB | 152.93 / 153.08 MiB | NetText: −26.4% (1.36×) |
| 20GiB E2E        | 1-й кадр лога, мс            |        233 / 259 мс |          31 / 38 мс | NetText в 7.52×         |
| 20GiB E2E        | Полный индекс, мс            |    12662 / 13084 мс |      4780 / 6127 мс | NetText в 2.65×         |
| 20GiB E2E        | Jump 50%, мс                 |           6 / 13 мс |            4 / 5 мс | Паритет                 |
| 20GiB E2E        | Jump EOF, мс                 |          12 / 14 мс |            3 / 8 мс | NetText в 4.00×         |
| 20GiB E2E        | Scroll Prev, мс              |          13 / 13 мс |            4 / 6 мс | NetText в 3.25×         |
| 20GiB E2E        | Search EOF, мс               |    55312 / 65628 мс |      6334 / 7469 мс | NetText в 8.73×         |
| 20GiB E2E        | Cancel поиска, мс            |            0 / 0 мс |            0 / 1 мс | Паритет                 |
| 20GiB E2E        | Private (1-й кадр), MiB      |   96.69 / 97.88 MiB |   10.40 / 10.60 MiB | NetText: −89.2% (9.30×) |
| 20GiB E2E        | Private (индекс), MiB        | 100.11 / 101.06 MiB |   17.73 / 18.10 MiB | NetText: −82.3% (5.65×) |
| 20GiB E2E        | Private (после E2E), MiB     |  99.76 / 100.15 MiB |   32.77 / 33.45 MiB | NetText: −67.2% (3.04×) |
| 20GiB E2E        | Working Set (после E2E), MiB |   88.11 / 88.71 MiB |   55.95 / 55.98 MiB | NetText: −36.5% (1.57×) |
| 20GiB E2E        | Peak WS (за E2E), MiB        |   90.18 / 90.61 MiB |   55.96 / 56.51 MiB | NetText: −37.9% (1.61×) |

- Сценарий `Mixed 40MB+10GiB` открывает одновременно вкладку редактора на 40 MB (`Scintilla` / GPUI `EditorState`) и лог на 10 GiB; P95 по 5 прогонам приведён как описательная оценка. Во всех прогонах поиска `E2E_EOF_SENTINEL` (10 GiB и 20 GiB) для обеих программ по сырым метрикам проверены `search_eof_complete=1` и `search_eof_hits=1`.
- `*` Заново прогнаны только 5-кратные E2E-серии NetText и CoreBench; стартовые серии (1-й кадр при открытии не индексирует весь файл) и базовые замеры RustText (`a97680f`) сохранены с предыдущего полного прогона, так как полный цикл длится 30–40 минут, а автор хотел уже спать.

## 6. Эквивалентные алгоритмические бенчмарки ядра (`CoreBench` vs `core_bench`)*

| Операция                     | Метрика          | RustText (`core_bench`)                          | NetText (`CoreBench`)                                | Преимущество                                          |
| ---------------------------- | ---------------- | ------------------------------------------------ | ---------------------------------------------------- | ----------------------------------------------------- |
| LineIndex(512MiB)            | Время выполнения | 309.6 ms (1.62 GiB/s), lines=6391297             | 77.9 ms (6.42 GiB/s), lines=6391297, alloc=0.25 MiB  | NetText в 3.97×                                       |
| LogSearchLiteralMiss(512MiB) | Время выполнения | 1076.8 ms (475.5 MiB/s)                          | 102.8 ms (4979.3 MiB/s), alloc=0.00 MiB              | NetText в 10.47×                                      |
| LogSearchRegexMiss(512MiB)   | Время выполнения | 1415.8 ms (361.6 MiB/s)                          | 104.2 ms (4911.9 MiB/s), alloc=0.00 MiB              | NetText в 13.59×                                      |
| EditorSearch(40MiB)          | Время выполнения | 55.9 ms, hits=450000                             | 76.8 ms, alloc=0.01 MiB, hits=450000                 | RustText в 1.37×                                      |
| JsonFormat(40MiB)            | Время выполнения | 74.2 ms, ok=True, outLen=44100023                | 320.1 ms, ok=True, outLen=44100023, alloc=168.62 MiB | RustText в 4.31×                                      |
| JsonFormatUtf8(40MiB)        | Время выполнения | –                                                | 244.6 ms, ok=True, outLen=44100023, alloc=42.06 MiB  | Только NetText (UTF-8, managed LOH)                   |
| JsonFormatUtf8Native(40MiB)  | Время выполнения | –                                                | 224.4 ms, ok=True, outLen=44100023, alloc=0.00 MiB   | Только NetText (UTF-8 NativeMemory, 0.00 MiB managed) |
| LogJumpAndScroll(512MiB)     | Время выполнения | 0.29 ms (50%=60 rows, EOF=60 rows, next=60 rows) | 0.87 ms (50%=60 rows, EOF=60 rows, next=60 rows)     | RustText в 3.00×                                      |
| LogSearchCancel              | Время выполнения | 0.10 ms                                          | 0.50 ms                                              | Паритет (<1 мс)                                       |
| EditorReplaceAll(40MiB)      | Время выполнения | 41.6 ms, count=450000                            | 104.3 ms, count=450000                               | RustText в 2.51×                                      |
| EditorSearchUtf8(40MiB)      | Время выполнения | –                                                | 13.5 ms, alloc=0.00 MiB, hits=450000                 | Только NetText (UTF-8, 0.00 MiB alloc)                |
| EditorReplaceAllUtf8(40MiB)  | Время выполнения | –                                                | 9.1 ms, alloc=0.00 MiB, count=450000                 | Только NetText (UTF-8, ArrayPool byte[])              |

- `CoreBench` / `core_bench` выполняются однократно на прогретом файловом кэше; порог паритета – `|Δ| ≤ 0.5 мс`.

Повторный запуск замеров: `pwsh -NoProfile -File tools\Compare-WithRustText.ps1 -Iterations 30 -E2EIterations 5 -IncludeBuildBench -IncludeCleanBuild -Include10GiB -Include20GiB -IncludeE2E -IncludeCoreBench -WriteReport`.
