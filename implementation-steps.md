# Execution tracker для IQ channelizer

Цей файл містить операційну послідовність реалізації для [`implementation-plan.md`](implementation-plan.md). Головний документ, особливо його розділи 1–16, є authoritative source для DSP mathematics, API contracts, memory layout, validation та acceptance criteria. Цей tracker не може послаблювати або перевизначати ті вимоги.

## Обов’язкове правило актуалізації

Після **кожного** implementation increment виконавець зобов’язаний до завершення роботи оновити цей файл:

1. змінити статус відповідного кроку;
2. додати стислий evidence: implementation files, test fixtures та важливі design decisions;
3. записати commit, на якому стан перевірено, або явно вказати `uncommitted working tree`;
4. записати результат повного `dotnet test IqChannelizer.sln -c Release`;
5. актуалізувати найближчий рекомендований крок і відомі blockers.

Не можна оголошувати крок виконаним лише за наявністю коду: усі його `Done`-критерії мають бути підтверджені тестами або явно перевірюваною документацією. Якщо цей tracker конфліктує з головним планом, виправити tracker і керуватися головним планом.

## Остання перевірка

- Дата: **2026-08-18**
- Verified baseline commit: `58ec6a5`
- Tracker update: `uncommitted Step 6 working tree` поверх `58ec6a5`
- Release tests: **122 passed, 0 failed, 0 skipped** (повний restore + test)
- Найближчий implementation step: **Крок 7 — generalized PFB algebra `P > 1`**
- Окремий housekeeping blocker: **Крок 0** ще не виконано, бо немає acceptance owner/fixture map і `artifacts/signal-validation/README.md`

## Зведена таблиця

| Крок | Статус | Evidence / примітка |
| --- | --- | --- |
| 0. Baseline та acceptance map | не виконано | Release baseline відтворюється, але manifest і owner/fixture mapping відсутні |
| 1. Contracts, validation, timing | виконано | Commit `ced2747`; повторно перевірено на `5b7492d` + Step 3 working tree, 90/90 tests |
| 2. FFTW runtime | виконано | Commit `3eb2c62`; повторно перевірено на `5b7492d` + Step 3 working tree, 90/90 tests |
| 3. Scalar filter-design foundation | виконано | Commit history до `5bae632`; повторно перевірено зі Step 5 working tree, 117/117 tests |
| 4. Independent reference DDC/toolkit | виконано | Commit `170dfe4`; 20 dedicated tests; повторно перевірено зі Step 5 working tree |
| 5. FDC overlap-save MVP | виконано | Commit `58ec6a5`; full-frame FFT, Kaiser window, exact discard, DDC/signal acceptance |
| 6. FDC planner/multiple-D | виконано | Uncommitted working tree поверх `58ec6a5`; per-channel D planner, shared N/forward FFT, grouped inverse plans; 122/122 tests |
| 7. Generalized PFB algebra `P > 1` | наступний | Кроки 3–6 завершено; Conservative production prototype ще не реалізовано |
| 8. Scalar PFB production flow | не почато | Залежить від Кроків 4 і 7 |
| 9. Generalized PFB planner | не почато | FoldAware лишається disabled |
| 10. Correctness/integration suite | не почато | Розширюється після production flows |
| 11. Diagnostics/observability | не почато | Не логувати в hot path |
| 12. BenchmarkDotNet suite | не почато | Використати наявний третій project |
| 13. SIMD gate | відкладено | Потрібен явний дозвіл власника repository |
| 14. FoldAware/selected-bin experiments | відкладено | Потрібні correctness і benchmark data |
| 15. Facade/docs/Auto | не почато | `Auto` реалізувати останнім і лише за profile data |

## Детальні кроки

### Крок 0. Зафіксувати baseline та acceptance map

**Статус: не виконано.**

- Запустити Release tests і записати кількість passed tests у робочий звіт.
- Зіставити кожен новий test fixture з конкретним acceptance criterion розділів 6–10 головного плану.
- Додати короткий `artifacts/signal-validation/README.md` або інший малий manifest, але не комітити великі raw IQ artifacts.

**Done:** baseline відтворюється на clean checkout з bundled FFTW DLL; список acceptance tests має owner/fixture mapping.

### Крок 1. Завершити contracts, validation і timing metadata

**Статус: виконано.** Commit `ced2747`; повторний аудит на `44d2372`, 60/60 Release tests.

- `ResolvedChannelizerPlan` і `ResolvedChannelPlan` містять metadata розділу 4.3 без per-callback metadata.
- Валідовано attenuation, ripple, minimum/preferred output rates, overflow, finite values і forced hints.
- Визначено й протестовано `Reset(nextFirstNewSampleIndex)` та discontinuity semantics; README узгоджено.
- Протестовано original channel ordering, exact one-write-per-channel, opaque IDs, deterministic output count і disposed engine.

**Evidence:** `Plans.cs`, `RequestValidator.cs`, `StreamingEngineBase.cs`, `ContractTests.cs`, `StreamingFlowTests.cs`.

**Done підтверджено:** Phase 1 contract tests зелені; timing fields відображають фактичні length-one fixtures, а warnings не дозволяють сприймати їх як production filters.

### Крок 2. Завершити базовий FFTW runtime module

**Статус: виконано.** Commit `3eb2c62`; повторний аудит на `44d2372`, 60/60 Release tests.

- Додано platform/architecture/export/version diagnostics для bundled Windows x64 DLL.
- Native buffers винесено в reusable ownership abstraction з address/alignment-class assertions.
- Реалізовано serialized plan cache із ref-counted leases; протестовано `plan_many`, різні lengths/batches, smooth composite lengths, in-place mode та repeated create/dispose stress.
- Wisdom import/export серіалізовано з planning; runtime policy явно single-thread, а `ThreadCount > 1` відхиляється до появи threads DLL та benchmark policy.
- Planning залишається поза `Process`; repeated execute та engine steady-state allocation tests зелені.
- Build/version, official archive hashes, distribution source й GPL/commercial obligations описані в [`docs/fftw-runtime.md`](docs/fftw-runtime.md).

**Evidence:** `FftwRuntime.cs`, `FftwAlignedBuffer.cs`, `FftwPlanCache.cs`, `FftwComplexPlan.cs`, `FftwTests.cs`, `docs/fftw-runtime.md`.

**Done підтверджено:** Phase 2 tests зелені, dependency failures actionable, repeated execution має zero managed allocations.

### Крок 3. Реалізувати scalar filter-design foundation

**Статус: виконано.** Commit history до `5bae632`; повторно перевірено зі Step 4 working tree, 110/110 Release tests.

- Додати `LowPassFilterSpec`, deterministic Kaiser-windowed sinc designer у `double`, normalized `float` taps і metadata.
- Додати standalone complex frequency-response evaluator.
- Додати conservative `AliasedResponseEvaluator` для decimation/hop folding.
- Додати scalar FIR, scalar fine power-of-two decimator і standalone scalar `SpectralSliceExtractor`.
- Для кожного primitive додати tiny hand-checkable, edge, invalid-input і response tests.

**Evidence:** `FilterDesign.cs`, `FrequencyResponseEvaluator.cs`, `AliasedResponseEvaluator.cs`, `ScalarFir.cs`, `ScalarPowerOfTwoDecimator.cs`, `SpectralSliceExtractor.cs`, `FilterDesignTests.cs`, `ScalarPrimitiveTests.cs`.

**Design decisions:** Kaiser design виконується в `double`, нормалізує odd-length symmetric `float` taps, детерміновано refinement-ить attenuation і кешується за normalized spec. Folded evaluator сумує magnitudes усіх alias images без phase-cancellation assumption. Scalar FIR, decimator та extractor є span-based, не залежать від engines і не алокують у repeated calls.

**Done підтверджено:** standalone ripple/attenuation пройдені для кількох specs; dense/folded response, tiny hand mappings, invalid/edge inputs і zero-allocation calls протестовані.

### Крок 4. Додати незалежний reference DDC і signal toolkit

**Статус: виконано.** Uncommitted working tree поверх `5bae632`; 110/110 Release tests.

- Реалізувати double-precision input-rate NCO, FIR і decimator без reuse критичної FDC/PFB математики.
- Додати deterministic generators: bin-centered/off-bin tones, two-tone, blocker, chirp, AM, seeded noise, impulse і zero input.
- Реалізувати rational timing alignment та metrics: RMS, max complex error, amplitude, phase, drift, leakage.

**Evidence:** `ReferenceDdc.cs`, `DeterministicSignals.cs`, `SignalAnalysis.cs`, `ReferenceDdcTests.cs`, `SignalToolkitTests.cs`.

**Design decisions:** reference path використовує `System.Numerics.Complex`, input-rate absolute-index NCO, власну double-precision causal convolution і general integer decimation; він не викликає production `ScalarFir`, rotator, FDC або PFB math. FIR timestamps задаються rational center-of-support offsets. Timing aligner порівнює exact rational coordinates без floating-point rounding. Metrics містять direct RMS/max complex error, best-fit complex gain amplitude/phase, unwrapped phase drift і normalized residual leakage. Генератори покривають bin/off-bin tone через довільну frequency, two-tone/blocker, chirp, AM, seeded complex Gaussian noise, impulse і zero input.

**Done підтверджено:** 20 unit tests перевіряють absolute-origin mixing, hand-checkable FIR/decimation/phase, rational half-sample alignment, generator determinism/continuity/validation і відомі amplitude/phase/drift/leakage metrics; module придатний як oracle для коротких deterministic streams обох engines.

### Крок 5. Перетворити FDC skeleton на справжній overlap-save MVP

**Статус: виконано.** Commit `58ec6a5`; 117/117 Release tests на момент коміту.

- Спроєктувати anti-alias FIR/window для одного forced `D` і визначити `HistorySize = filterLength - 1`.
- Гарантувати `N = HistorySize + ChunkSize`, divisibility/alignment і `ChunkSize <= MaxChunkSize`.
- Копіювати повний `[history | chunk]` у FFTW forward input, застосовувати validated spectral window, backward transform, `1/N`, discard рівно `HistorySize/D`.
- Винести wrap-safe extraction у scalar-tested module без modulo в inner loop.
- Вивести block/residual phase з absolute frame start і порівняти з reference DDC.

**Done:** один-D FDC проходить amplitude, phase, history-discard, positive/negative/wrap center, blocker/alias і split-stream tests.

**Evidence:** `FdcFilterDesign.cs`, `FftwFdcEngine.cs`, `ChannelizerFactory.cs`, complex-window overload у `SpectralSliceExtractor.cs`, `FdcOverlapSaveTests.cs`, оновлені `ContractTests.cs` і `README.md`.

**Design decisions:** для кожного каналу створюється causal symmetric Kaiser FIR; symmetric zero-padding робить engine-wide `HistorySize` кратним forced `D` без зміни magnitude response. `N = HistorySize + ChunkSize`, весь frame надходить у shared forward FFT, short length дорівнює `N/D`, complex response семплується на FFT grid з урахуванням residual offset, а conservative folded response перевіряється до створення engine. Block phase походить від `firstNewSampleIndex - HistorySize`; після backward FFT застосовується explicit `1/N`, discard рівно `HistorySize/D`, а residual rotator стартує з absolute `firstNewSampleIndex`. Group delay і first-output offset походять від фактичного padded FIR order.

**Done підтверджено:** 7 нових tests покривають amplitude/phase проти незалежного double-precision DDC, positive/negative/off-bin/wrap centers, exact history discard, split-stream continuity, blocker/alias rejection, full-frame dimensions і divisibility. Steady-state no-allocation та попередні routing/contract tests залишаються зеленими.

### Крок 6. Додати FDC planner і multiple-D groups

**Статус: виконано.** Uncommitted working tree поверх `58ec6a5`; 122/122 Release tests.

- Enumerate power-of-two `D` candidates та bounded/smooth `N` candidates.
- Узгодити engine-wide history/alignment для різних `D`.
- Групувати batched inverse plans за short length; не дублювати forward FFT.
- Заповнювати resolved plan реальними `D`, short lengths, filters, residuals, group delays і counts.

**Done:** один request із кількома `D` groups має один forward FFT, deterministic outputs і збігається з independent DDC.

**Evidence:** `FdcPlanner.cs`, multi-group runtime у `FftwFdcEngine.cs`, planner integration у `ChannelizerFactory.cs`, alias-budget refinement у `FdcFilterDesign.cs`, `FdcPlannerTests.cs`, оновлений `README.md`.

**Design decisions:** без forced hint planner вибирає найбільший feasible power-of-two `D` окремо для кожного каналу з урахуванням occupied width, minimum і preferred rate. Forced `FdcDecimationFactor` є global override. Engine-wide history округлюється до `max(D)`, chunk candidates bounded `MaxChunkSize`, вирівняні до `max(D)` і класифікуються за FFTW-friendly factors `2/3/5/7` при збереженні пріоритету близькості до preferred chunk. Runtime створює одну backward batch group на distinct `D`, виконує один shared forward FFT і маршрутизує output у початковому request order. Filter attenuation включає conservative alias-image budget `20*log10(D-1)`.

**Done підтверджено:** multi-D fixture автоматично вибирає `D=8` і `D=2`, має дві short-IFFT groups, один forward execution, deterministic counts/order і для обох каналів збігається з independent DDC. Окремо перевірено preferred-rate constraint, forced override та smooth-length classifier; повний regression/no-allocation suite зелений.

### Крок 7. Завершити scalar generalized PFB algebra для `P > 1`

**Статус: наступний.**

- Замінити `P = 1` rectangular fixture на Conservative prototype з `T = K * P`.
- Реалізувати scalar branch equation `h[p+qK] * x[r-(p+qK)]` і незалежний direct FIR+DFT oracle.
- Зберегти explicit post-FFT `C(r,k)` та pre-FFT shift reference variants.
- Розширити tests на `H=K`, `H=K/2`, arbitrary `H`, negative bins, non-aligned `firstNew`, process partitions і `P > 1`.

**Done:** PFB branch output, direct FIR+DFT, explicit correction і shifted FFT збігаються в установленій tolerance.

### Крок 8. Завершити scalar PFB production flow

**Статус: не почато.**

- Писати filtered phase vectors безпосередньо в FFTW-owned input або надати validated no-copy writable view; raw IQ history туди не копіювати.
- Додати precomputed unique-bin router і fan-out для кількох channels одного bin.
- Додати per-channel residual filter та scalar fine power-of-two decimator.
- Забезпечити `FramesPerBatch % FineDecimation == 0` і exact one block per channel.

**Done:** PFB проходить shared-bin, fine-decimation, no-duplicate-history, no-allocation і independent-DDC tests.

### Крок 9. Реалізувати generalized PFB planner

**Статус: не почато.**

- Enumerate valid `K`, integer `H` і `FramesPerBatch` під chunk bounds.
- Перевіряти single-bin feasibility, residual range, output-rate constraints і folded response.
- Спочатку підтримати лише Conservative prototypes; FoldAware залишити disabled до blocker/alias suite.
- Довести planner-selected non-2× configuration окремим end-to-end test.

**Done:** planner вибирає щонайменше один `H != K` і `H != K/2`, що проходить повну signal spec.

### Крок 10. Розширити correctness та integration suite

**Статус: не почато.**

- Додати всі релевантні сценарії розділу 10.2 головного плану для FDC і PFB.
- Перевірити exact counts/rates/timing, long-run phase, discontinuity/reset, first/last channel, bin wrap і worst residual `±DeltaF/2`.
- Додати standalone/folded response sweeps і blocker sweeps по всіх alias bands.
- Зберігати лише компактні machine-readable summaries в `artifacts/signal-validation/`.

**Done:** обидва engines проходять independent DDC і alias acceptance suite; failures містять reproducible seed/configuration.

### Крок 11. Додати diagnostics та observability

**Статус: не почато.**

- Реалізувати allocation-free counters і stage timing з розділу 12 головного плану.
- Не логувати на sample/frame hot path; debug tracing має бути sampled або explicit opt-in.
- Додати tests на monotonic counters та відсутність allocations із diagnostics enabled/disabled.

**Done:** plan/runtime status пояснюють consumed counts, output counts, latency і failures без зміни sink contract.

### Крок 12. Побудувати реальний BenchmarkDotNet suite

**Статус: не почато.**

- Підключити BenchmarkDotNet у наявний третій project, не створювати новий project.
- Додати FFTW, scalar primitives, FDC, PFB і end-to-end families з розділу 11 головного плану.
- Розділити initialization/planning та steady-state execution.
- Генерувати `artifacts/benchmarks/latest-summary.md` з commit/environment/raw paths.

**Done:** є reproducible baseline ns/input sample, allocations, working set і stage breakdown; немає realtime claims без end-to-end result.

### Крок 13. SIMD gate — лише після явного дозволу

**Статус: відкладено.**

- Після дозволу реалізувати scalar-equivalent AVX2/FMA primitives у порядку Phase 5.
- Далі реалізувати phase-parallel PFB FIR з direct rotated store та FDC extraction kernels.
- AVX-512 додавати лише якщо benchmark показує benefit; ISA dispatch робити один раз.

**Done:** random/tail/alignment, scalar-vs-SIMD end-to-end tests і benchmarks зелені.

### Крок 14. FoldAware та selected-bin experiments — лише за даними

**Статус: відкладено.**

- Увімкнути FoldAware candidate тільки після folded evaluator і blocker sweep.
- Реалізувати selected-bin/direct-DFT PFB лише якщо profile показує домінування full FFT для малого `Q`.
- Не підключати experimental path до `Auto` до correctness і crossover evidence.

**Done:** кожна optimization має stored correctness/benchmark evidence або залишається вимкненою.

### Крок 15. Завершити facade, docs і Auto останнім

**Статус: не почато.**

- Завершити public plan inspection, diagnostics, reset/reconfiguration docs і minimal production example.
- Перевірити Definition of Done і створити acceptance report.
- Реалізувати `Auto` лише з versioned benchmark profile schema і explainable resolved decision.
- Оновити FFTW licensing/distribution documentation перед release/publish.

**Done:** Definition of Done розділу 14 головного плану виконаний, Release tests і benchmarks відтворюються, а `Auto` не використовує неперевірені heuristics.
