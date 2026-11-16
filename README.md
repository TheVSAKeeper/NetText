# NetText

Текстовый редактор и просмотрщик больших логов для Windows x64 на .NET 11 (NativeAOT), Win32/GDI и Scintilla + Lexilla (DirectWrite).

> [!NOTE]
> **Дисклеймер: «перепишем на C#» в эпоху «перепишем всё на Rust»**
>
> Этот проект – эксперимент и оммаж оригинальному редактору [RustText](https://github.com/Illson/RustText). Он появился из спортивного интереса на фоне моды «перепишем весь мир на Rust»: хотелось посмотреть, что выйдет, если пойти против течения и перенести свежий Rust-проект на .NET NativeAOT.
>
> - Архитектура, sparse-индекс логов, безопасная обрезка, сессионный журнал и UX – заслуга **RustText**. `RustText` использует GPUI Kit и `syntect`, а `NetText` берёт готовую `Scintilla.dll` + `Lexilla.dll`, рисует логи через GDI и приходит хвастаться памятью и размером поставки. Цифры относятся к этим двум реализациям и ничего не доказывают о преимуществе .NET над Rust или наоборот.
> - Полного паритета с оригиналом нет и не планируется: как минимум в `NetText` отсутствует встроенное автообновление из `RustText v0.1.1`, а сам `RustText` активно развивается автором и уже уходит дальше.
> - Для замеров зафиксирован конкретный исторический срез – **`RustText v0.1.2`** (`00b273a`), поверх которого на ветке `perf/bench-probes` наложен локальный патч телеметрии, E2E-проб и `core_bench`. В апстриме автора этих подпорок нет, поэтому отчёт – лишь снимок одного коммита, а не вечный вердикт.

## Результаты сравнения

Полная методика, версии инструментов, зависимости, матрица функционального соответствия, парные алгоритмические бенчмарки ядра (`CoreBench` vs `core_bench`) и сырые данные вынесены в [docs/comparison.md](docs/comparison.md) и [docs/comparison-raw.json](docs/comparison-raw.json).

### Запуск и первичная готовность (Median / P95 по 30 прогонам)

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

### Полная нагрузка E2E и профиль памяти во времени (Median / P95 по 5 прогонам)*

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

Подробнее по разработке, сборкам и алгоритмическим бенчмаркам ядра – в [полном отчёте сравнения](docs/comparison.md).

## Запуск и сборка

Нужен .NET 11 SDK (`net11.0-windows`, `win-x64`), Desktop Development with C++ (Visual Studio) для NativeAOT-линковки и нативные библиотеки `Scintilla.dll` (5.6.9) + `Lexilla.dll` (5.5.5) для `win-x64` в каталоге `native\win-x64\` (сами `.dll` не хранятся в Git).

```powershell
dotnet test tests\NetText.Tests\NetText.Tests.csproj -c Release
dotnet publish src\NetText\NetText.csproj -c Release -r win-x64
pwsh -NoProfile -File tools\Package-NetText.ps1 -OutputDirectory dist\release
pwsh -NoProfile -File tools\Compare-WithRustText.ps1 -Iterations 30 -E2EIterations 5 -IncludeBuildBench -IncludeCleanBuild -Include10GiB -Include20GiB -IncludeE2E -IncludeCoreBench -WriteReport
```
