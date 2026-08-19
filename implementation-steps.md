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

- Дата: **2026-08-19**
- Verified implementation base commit: `6aaa8ae` + **current profile-backed Auto changeset**
- Release tests: **235 passed, 0 failed, 0 skipped**
- Companion tool: **27/27 Vitest**, production Vite build successful
- Benchmarks: retained **18-case scalar/AVX2/AVX-512 engine comparison**, SIMD kernel comparisons, schema-v2 allocation/latency/stage profile та Step 14 FoldAware/selected-bin comparison
- Найближчий implementation step: **записати target 100 MS/s end-to-end profile; portable realtime claim до цього заборонений**
- Release packaging: **managed-only NuGet verified; FFTW не входить до package і постачається consumer-ом окремо**

## Зведена таблиця

| Крок | Статус | Evidence / примітка |
| --- | --- | --- |
| 0. Baseline та acceptance map | виконано | [`docs/acceptance/manifest.md`](docs/acceptance/manifest.md) містить owner-role/fixture mapping і reproducible commands; [`docs/acceptance/report.md`](docs/acceptance/report.md) аудіює Definition of Done; 235/235 Release tests |
| 1. Contracts, validation, timing | виконано | Commit `ced2747`; повторно перевірено на `5b7492d` + Step 3 working tree, 90/90 tests |
| 2. FFTW runtime | виконано | Commit `3eb2c62`; повторно перевірено на `5b7492d` + Step 3 working tree, 90/90 tests |
| 3. Scalar filter-design foundation | виконано | Commit history до `5bae632`; повторно перевірено зі Step 5 working tree, 117/117 tests |
| 4. Independent reference DDC/toolkit | виконано | Commit `170dfe4`; 20 dedicated tests; повторно перевірено зі Step 5 working tree |
| 5. FDC overlap-save MVP | виконано | Commit `58ec6a5`; full-frame FFT, Kaiser window, exact discard, DDC/signal acceptance |
| 6. FDC planner/multiple-D | виконано | Commit `e969547`; per-channel D planner, shared N/forward FFT, grouped inverse plans; 122/122 tests на момент коміту |
| 7. Generalized PFB algebra `P > 1` | виконано | Commit `29c9771`; Conservative prototype, direct oracle, timing/partition tests; 127/127 tests на момент коміту |
| 8. Scalar PFB production flow | виконано | Commit `ecbf805`; native writable input, unique-bin fan-out, stateful fine decimation/DDC; 130/130 tests |
| 9. Generalized PFB planner | виконано | Deterministic power-of-two `K`, arbitrary integer `H`, bounded frames, exact Conservative/fine validation і planner-selected `H=33` end-to-end signal test |
| 10. Correctness/integration suite | виконано | Commit `f138d1d`; FDC/PFB alias sweeps, worst residuals, compact machine-readable summary |
| 11. Diagnostics/observability | виконано | Commit `db4e98d`; allocation-free counters/stage timing, fault/reset status і documented semantics |
| 12. BenchmarkDotNet suite | виконано | Commit `23b05b0`; 12 statistical cases, retained raw reports і integration stage/working-set profile |
| 13. SIMD gate | виконано | Creation-time scalar/AVX2/AVX-512 dispatch, 64-byte buffers, PFB direct-store FIR і FDC extraction; retained 18-case engine і kernel comparisons |
| 14. FoldAware/selected-bin experiments | виконано | Explicit FoldAware пройшов folded/blocker correctness і measured end-to-end comparison; selected-bin direct DFT має stored crossover evidence та лишається unwired |
| 15. Facade/docs/Auto | частково | Facade/docs, managed-only package та exact profile-backed strategy `Auto` виконано; 100 MS/s claim лишається за target profile gate |

## Детальні кроки

### Крок 0. Зафіксувати baseline та acceptance map

**Статус: виконано.** Base commit `4e4c76c` + uncommitted working tree; 148/148 Release tests.

- Запустити Release tests і записати кількість passed tests у робочий звіт.
- Зіставити кожен новий test fixture з конкретним acceptance criterion розділів 6–10 головного плану.
- Додати короткий `artifacts/signal-validation/README.md` або інший малий manifest, але не комітити великі raw IQ artifacts.

**Done:** baseline відтворюється на clean checkout з bundled FFTW DLL; список acceptance tests має owner/fixture mapping.

**Evidence:** [`docs/acceptance/manifest.md`](docs/acceptance/manifest.md) зіставляє enforced/deferred gates з owner roles, fixtures і командами; великі generated outputs залишаються ignored.

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

**Статус: виконано.** Commit `e969547`; 122/122 Release tests на момент коміту.

- Enumerate power-of-two `D` candidates та bounded/smooth `N` candidates.
- Узгодити engine-wide history/alignment для різних `D`.
- Групувати batched inverse plans за short length; не дублювати forward FFT.
- Заповнювати resolved plan реальними `D`, short lengths, filters, residuals, group delays і counts.

**Done:** один request із кількома `D` groups має один forward FFT, deterministic outputs і збігається з independent DDC.

**Evidence:** `FdcPlanner.cs`, multi-group runtime у `FftwFdcEngine.cs`, planner integration у `ChannelizerFactory.cs`, alias-budget refinement у `FdcFilterDesign.cs`, `FdcPlannerTests.cs`, оновлений `README.md`.

**Design decisions:** без forced hint planner вибирає найбільший feasible power-of-two `D` окремо для кожного каналу з урахуванням occupied width, minimum і preferred rate. Forced `FdcDecimationFactor` є global override. Engine-wide history округлюється до `max(D)`, chunk candidates bounded `MaxChunkSize`, вирівняні до `max(D)` і класифікуються за FFTW-friendly factors `2/3/5/7` при збереженні пріоритету близькості до preferred chunk. Runtime створює одну backward batch group на distinct `D`, виконує один shared forward FFT і маршрутизує output у початковому request order. Filter attenuation включає conservative alias-image budget `20*log10(D-1)`.

**Done підтверджено:** multi-D fixture автоматично вибирає `D=8` і `D=2`, має дві short-IFFT groups, один forward execution, deterministic counts/order і для обох каналів збігається з independent DDC. Окремо перевірено preferred-rate constraint, forced override та smooth-length classifier; повний regression/no-allocation suite зелений.

### Крок 7. Завершити scalar generalized PFB algebra для `P > 1`

**Статус: виконано.** Commit `29c9771`; 127/127 Release tests на момент коміту.

- Замінити `P = 1` rectangular fixture на Conservative prototype з `T = K * P`.
- Реалізувати scalar branch equation `h[p+qK] * x[r-(p+qK)]` і незалежний direct FIR+DFT oracle.
- Зберегти explicit post-FFT `C(r,k)` та pre-FFT shift reference variants.
- Розширити tests на `H=K`, `H=K/2`, arbitrary `H`, negative bins, non-aligned `firstNew`, process partitions і `P > 1`.

**Done:** PFB branch output, direct FIR+DFT, explicit correction і shifted FFT збігаються в установленій tolerance.

**Evidence:** `PfbPrototypeDesign.cs`, generalized FIR у `FftwPfbEngine.cs` і `PfbMath.cs`, independent `PfbDirectReference.cs`, `PfbAlgebraTests.cs`, оновлені `ChannelizerFactory.cs`, `ContractTests.cs` і `README.md`.

**Design decisions:** common Conservative prototype визначає pass/stop edges з фактичних residual offsets і channel widths, додає conservative alias-image attenuation budget та проходить folded validation для hop `H`. Kaiser taps нормалізовані до DC gain 1 і padded до `T=K*P`; exact group delay походить від позиції непорожнього symmetric FIR усередині padding. Production scalar kernel обчислює `sum_q h[p+qK]x[r-(p+qK)]` без raw-history copy та одразу пише у circularly shifted FFT input. Окремий oracle виконує direct double-precision FIR+DFT за absolute input indices й не використовує production phase-vector/correction math.

**Done підтверджено:** tiny `K=4,P=2` fixture перевіряє branch equation вручну; `K=4,P=3` fixtures для `H=K`, `H=K/2` і `H=3` звіряють direct oracle, explicit correction і circular shift для всіх positive/negative bins та non-aligned firstNew. Production `P>1` engine зберігає amplitude і continuity між Process partitions; попередні arbitrary-hop і no-allocation tests зелені.

### Крок 8. Завершити scalar PFB production flow

**Статус: виконано.** Commit `ecbf805`; 130/130 Release tests.

- Писати filtered phase vectors безпосередньо в FFTW-owned input або надати validated no-copy writable view; raw IQ history туди не копіювати.
- Додати precomputed unique-bin router і fan-out для кількох channels одного bin.
- Додати per-channel residual filter та scalar fine power-of-two decimator.
- Забезпечити `FramesPerBatch % FineDecimation == 0` і exact one block per channel.

**Done:** PFB проходить shared-bin, fine-decimation, no-duplicate-history, no-allocation і independent-DDC tests.

**Evidence:** writable native input у `FftwComplexPlan.cs`, precomputed router/fan-out і direct rotated native stores у `FftwPfbEngine.cs`, `PfbFineStage.cs`, complex-tap overload у `ReferenceDdc.cs`, `PfbProductionFlowTests.cs`, writable-input test у `FftwTests.cs`, оновлені factory/contract/streaming tests і `README.md`.

**Design decisions:** PFB phase FIR пише без managed input staging прямо у validated FFTW-owned span; один precomputed route відповідає кожному channel, а coarse stream збирається один раз на unique bin/frame. Fine planner вибирає найбільший feasible power-of-two factor, що ділить `FramesPerBatch`, з урахуванням occupied/minimum/preferred rate. Після absolute residual rotation stateful scalar fine FIR/decimator використовує fixed phase 0; divisibility гарантує безперервність phase між Process calls, Reset очищає history. Total group delay, final rate, stride, filter ID та output count записуються per channel. Independent DDC використовує mathematically equivalent complex-modulated prototype taps для coarse-before-residual convention.

**Done підтверджено:** shared-bin fixture збирає один bin для двох каналів, застосовує `Dfine=8` і `Dfine=2`, зберігає request order/exact counts і після FIR warmup збігається з independent DDC. Окремо перевірено Reset state, FFTW-owned writable input та попередній steady-state zero-allocation contract.

### Крок 9. Реалізувати generalized PFB planner

**Статус: виконано.** Base commit `4e4c76c` + uncommitted working tree; 148/148 Release tests.

- Enumerate valid `K`, integer `H` і `FramesPerBatch` під chunk bounds.
- Перевіряти single-bin feasibility, residual range, output-rate constraints і folded response.
- Спочатку підтримати лише Conservative prototypes; FoldAware залишити disabled до blocker/alias suite.
- Довести planner-selected non-2× configuration окремим end-to-end test.

**Done:** planner вибирає щонайменше один `H != K` і `H != K/2`, що проходить повну signal spec.

**Evidence:** `PfbPlanner.cs`, geometry/output-rate split у `PfbPrototypeDesign.cs`, factory wiring у `ChannelizerFactory.cs` і `PfbPlannerTests.cs`.

**Design decisions:** automatic policy перебирає power-of-two `K` до 8192, integer `H` у межах `MaxChunkSize` і deterministic frame candidates. Спочатку відсіюються single-bin geometry, periodic residual, required output rate та chunk bounds; потім кандидати у стабільному score order проходять exact Conservative prototype і fine-stage design. Partial hints лишають warning, повністю forced shape є override. `BenchmarkProfileKey` не заповнюється, а FoldAware залишається disabled.

**Done підтверджено:** planner детерміновано вибирає `K=64,H=29,F=4` та `K=64,H=33,F=4`, не пропускає feasible малий `H=16` при `MaxChunkSize=16`, відхиляє forced shape, що не вміщується, і planner-selected `H=33` зберігає amplitude після FIR warmup.

### Крок 10. Розширити correctness та integration suite

**Статус: виконано.** Base commit `8050d9c` + uncommitted working tree; 156/156 Release tests.

- Додати всі релевантні сценарії розділу 10.2 головного плану для FDC і PFB.
- Перевірити exact counts/rates/timing, long-run phase, discontinuity/reset, first/last channel, bin wrap і worst residual `±DeltaF/2`.
- Додати standalone/folded response sweeps і blocker sweeps по всіх alias bands.
- Зберігати лише компактні machine-readable summaries в `artifacts/signal-validation/`.

**Done:** обидва engines проходять independent DDC і alias acceptance suite; failures містять reproducible seed/configuration.

**Evidence:** `SignalValidationAcceptanceTests.cs`, `artifacts/signal-validation/scalar-acceptance.json`, розширений `docs/acceptance/manifest.md`, а також попередні `ContractTests`, `StreamingFlowTests`, `FdcOverlapSaveTests`, `PfbAlgebraTests`, `PfbProductionFlowTests` і `FilterDesignTests`.

**Design decisions:** representative FDC `D=8` sweep виконує всі `D-1` folded images і для кожного blocker звіряє complex output з незалежним double-precision DDC. Representative generalized PFB `K=8,H=2,Dfine=8` sweep виконує всі `H*Dfine-1` images фінального rate після FIR warmup. Окремі fixtures перевіряють обидва worst residual `±DeltaF/2`, а standalone/conservative-folded filter matrix покриває `D=2/4/8`. Кожна signal failure містить deterministic seed і повну shape/frequency configuration; tracked JSON зберігає лише компактну матрицю та acceptance limits без raw IQ.

**Done підтверджено:** обидва engines проходять alias acceptance, FDC sweep збігається з independent DDC, exact output counts перевіряються на кожному process, а tracked summary валідовується executable test-ом.

### Крок 11. Додати diagnostics та observability

**Статус: виконано.** Base commit `f138d1d` + uncommitted working tree; 168/168 Release tests.

- Реалізувати allocation-free counters і stage timing з розділу 12 головного плану.
- Не логувати на sample/frame hot path; debug tracing має бути sampled або explicit opt-in.
- Додати tests на monotonic counters та відсутність allocations із diagnostics enabled/disabled.

**Done:** plan/runtime status пояснюють consumed counts, output counts, latency і failures без зміни sink contract.

**Evidence:** `ChannelizerDiagnostics.cs`, diagnostics integration у `StreamingEngineBase`, `FftwFdcEngine` і `FftwPfbEngine`, `DiagnosticsTests.cs`, `docs/diagnostics.md` та acceptance-manifest mapping.

**Design decisions:** diagnostics є explicit creation-time opt-in із режимами `Disabled`, `Counters` і `StageTiming`. Один preallocated diagnostics object зберігає lifetime counters, per-channel output totals, rejection/failure/reset state та engine-specific input-stage metrics; `Snapshot` є value type. Stage timing використовує monotonic `Stopwatch` ticks лише на block/stage boundaries, без logging і без per-sample clock reads. `CurrentRealtimeMargin` описує останній успішний block, а не заяву про realtime capacity. `Reset` очищає current fault status, але зберігає historical counters і збільшує `ReconfigurationCount`.

**Done підтверджено:** counters монотонні й мають exact consumed/output значення; sink failure видимий до Reset; FDC copy та PFB polyphase metrics мають різні задокументовані semantics; усі enabled/disabled режими проходять steady-state zero-allocation tests для обох engines.

### Крок 12. Побудувати реальний BenchmarkDotNet suite

**Статус: виконано.** Implementation increment based on `db4e98d`; 168/168 Release tests and 12/12 full statistical benchmark cases.

- Підключити BenchmarkDotNet у наявний третій project, не створювати новий project.
- Додати FFTW, scalar primitives, FDC, PFB і end-to-end families з розділу 11 головного плану.
- Розділити initialization/planning та steady-state execution.
- Генерувати `artifacts/benchmarks/latest-summary.md` з commit/environment/raw paths.

**Done:** є reproducible baseline ns/input sample, allocations, working set і stage breakdown; немає realtime claims без end-to-end result.

**Evidence:** BenchmarkDotNet 0.15.8 у наявному третьому project; `PrimitiveBenchmarks`, `FftwBenchmarks`, `EngineBenchmarks` і `PlanningBenchmarks`; retained CSV/Markdown та environment/command summary у `artifacts/benchmarks/`; `StageProfileRunner.cs` і machine-readable `stage-profile.json`; оновлені `docs/benchmarks.md` та acceptance manifest.

**Design decisions:** decision-grade BDN run використовує default statistical job замість Dry і зберігає confidence/error data для всіх 12 cases. Steady-state engine results нормалізовані через `OperationsPerInvoke=4096` до ns/input sample; initialization лишається окремою allocation-bearing family. Integration runner виконує 2,000 blocks після same-engine stabilization, preallocate-ить latency storage, звітує p50/p95/p99/max, exact managed allocation delta, resolved working-set estimate, process working-set snapshots і diagnostics-based stage ticks. StageTiming overhead явно відокремлено від BDN baseline.

**Done підтверджено:** на AMD Ryzen 5 8500G / .NET 10.0.11 виконано 12/12 statistical cases; steady-state BDN та integration loops мають zero managed allocations; raw reports, commit/environment, exact commands, working-set і stage-breakdown evidence збережені. Результати не використовуються як portable realtime claim і не вмикають `Auto` чи experimental paths.

### Крок 13. SIMD gate — лише після явного дозволу

**Статус: виконано.** Власник repository явно дозволив AVX2 і згодом AVX-512 реалізацію 2026-08-19. AVX2 зафіксовано commit `998d427`, AVX-512 — commit `4363f84`; на момент AVX-512 closeout пройшло 221/221 Release tests.

- Після дозволу реалізувати scalar-equivalent AVX2/FMA primitives у порядку Phase 5.
- Далі реалізувати phase-parallel PFB FIR з direct rotated store та FDC extraction kernels.
- AVX-512 додавати лише якщо benchmark показує benefit; ISA dispatch робити один раз.

**Done:** random/tail/alignment, scalar-vs-SIMD end-to-end tests і benchmarks зелені.

**Evidence:** `FftwAlignedBuffer.cs` гарантує 64-byte effective address поверх FFTW-owned allocation. `SimdBackendResolver.cs` один раз при creation розв'язує `Auto/Scalar/Avx2/Avx512`; forced unsupported ISA дає actionable error. `Avx2ComplexKernels.cs`, `Avx512ComplexKernels.cs`, `PfbPhaseFir.cs` і `SpectralSliceExtractor.cs` реалізують tested AoS primitives, 4/8-phase PFB FIR із expanded-by-tap coefficients/direct two-segment rotated FFTW store та wrap-safe FDC complex extraction. FDC extraction пише прямо у writable native inverse-plan input; scalar fallback використовує ту саму validated layout.

**Correctness evidence:** `SimdTests`, `PfbSimdTests`, `SimdEngineTests`, розширені `ContractTests`, `FftwTests` і `StreamingFlowTests` покривають artificial capability matrices, random values, scalar tails, misaligned spans, exact/partial overlap, compact/expanded coefficient variants, arbitrary `H`, signed/large absolute origins, scalar-vs-AVX2/AVX-512 FDC/PFB partitions і 2,000-call zero-allocation representative profiles. Повний independent-DDC/alias suite лишається зеленим.

**Performance decisions:** full statistical AVX-512 comparison має 18/18 scalar/AVX2/AVX-512 engine cases без allocations. PFB AVX-512 перевершив AVX2 end-to-end: `9.953 -> 9.138`, `12.758 -> 11.625`, `24.382 -> 23.952` ns/sample для 1/8/32 channels; тому PFB `Auto` віддає перевагу AVX-512F. FDC AVX-512 extraction швидший за AVX2 (`35.10 -> 33.71` ns для 128 bins; `122.41 -> 102.05` для 512), але end-to-end результат змішаний і AVX2 кращий у 1/32-channel shapes (`2.682 vs 2.724`, `9.315 vs 9.373`), тому FDC `Auto` віддає перевагу AVX2; forced AVX-512 доступний. AVX2 residual rotator не підключено через непереконливий delta. Raw Markdown/CSV і schema-v2 three-backend stage profile збережені в `artifacts/benchmarks/`.

### Крок 14. FoldAware та selected-bin experiments — лише за даними

**Статус: виконано.** Власник repository дозволив реалізацію 2026-08-19; 229/229 Release tests.

- Увімкнути FoldAware candidate тільки після folded evaluator і blocker sweep.
- Реалізувати selected-bin/direct-DFT PFB лише якщо profile показує домінування full FFT для малого `Q`.
- Не підключати experimental path до `Auto` до correctness і crossover evidence.

**Done:** кожна optimization має stored correctness/benchmark evidence або залишається вимкненою.

**Correctness evidence:** `PfbFoldAwareTests` перевіряє коротший FoldAware prototype, requested passband amplitude, folded attenuation та plan/filter identity. `SignalValidationAcceptanceTests` проганяє однаковий 15-image blocker sweep для Conservative і FoldAware; machine-readable summary оновлений до schema 2. `PfbSelectedBinDftTests` зіставляє scalar/AVX2/AVX-512 direct DFT для random frames, tails і доступних ISA та підтверджує steady-state zero allocations.

**Performance decision:** explicit FoldAware зменшив end-to-end PFB cost для representative `K=64,H=32,F=128,Q=8` з `12.281` до `7.232` ns/input sample (~41.1%, 0 B allocated), тому доступний лише через `PfbPrototypeDesignMode.FoldAware`; default і automatic planner лишаються Conservative до versioned profile. Selected-bin direct DFT виграє лише в частині shapes (`K=64,Q=1`; `K=512,Q<=4` на AVX-512), програє при `K=64,Q>=4` і `K=512,Q=8`, тоді як stored end-to-end stage profile віддає FFTW лише ~9.1% PFB часу. Тому `PfbSelectedBinDft` збережений як internal benchmark/test prototype і не підключений до production engine або `Auto`. Raw Markdown/CSV збережені в `artifacts/benchmarks/results/`.

### Крок 15. Завершити facade, docs і Auto останнім

**Статус: частково (facade/docs, hardening, managed-only package і exact profile-backed Auto виконано).** Base commit `6aaa8ae` + current Auto changeset; 235/235 Release tests.

- Завершити public plan inspection, diagnostics, reset/reconfiguration docs і minimal production example.
- Перевірити Definition of Done і створити acceptance report.
- Реалізувати `Auto` лише з versioned benchmark profile schema і explainable resolved decision.
- Перед release/publish перевірити managed-only NuGet: MIT license присутня, а FFTW DLL/header відсутні.

**Done:** Definition of Done розділу 14 головного плану виконаний, Release tests і benchmarks відтворюються, а `Auto` не використовує неперевірені heuristics.

**Evidence:** `ChannelizerFactory.cs` повертає read-only snapshots для channels і warnings; `ContractTests.ResolvedPlanCollectionsAreImmutableSnapshots` перевіряє обидві strategies та відв’язування від mutable request list. README містить самодостатній request/plan/process/sink example. [`docs/facade.md`](docs/facade.md) фіксує plan inspection, span lifetime, serialized streaming і різницю між `Reset` та створенням нового engine. [`docs/acceptance/report.md`](docs/acceptance/report.md) аудіює кожен пункт Definition of Done і явно відділяє enforced, guarded, deferred та externally blocked gates.

**Design decisions:** in-place reconfiguration не додається: зміна channels/rates/strategy/hints потребує створення та перевірки нового engine поза hot path. SIMD `Auto` є hardware/backend selection між accepted scalar/AVX2/AVX-512 paths; channelizer strategy `Auto` застосовує лише exact versioned profile match і не extrapolate-ить невідомі shapes. IqChannelizer має MIT license; FFTW 3.3.5 використовується для локальної відтворюваності, але DLL/header не входять до NuGet. `build/verify-package.ps1` інспектує package, використовує isolated package cache, збирає clean consumer і додає FFTW runtime лише після build.

**Auto evidence:** `artifacts/benchmarks/strategy-profile-v1.json` фіксує same-request Q=1/8/32 scalar/AVX2/AVX-512 comparison: FDC виграв усі дев'ять rows щонайменше у 2.5x. `StrategyProfileSelector` вимагає exact CPU/OS/runtime/FFTW і request-family match, вибирає FDC та записує profile key/explanation; невідомі cases кидають actionable `NotSupportedException`. `StrategyProfileTests` перевіряє schema/runtime-key consistency, margin, matching і rejection.

**Done ще не підтверджено повністю:** лишилося target 100 MS/s realtime evidence. Clean-environment managed-only consumer validation, profile-backed channelizer strategy `Auto`, FoldAware/selected-bin comparisons та AVX-512 measured decision виконано. SigMF generator ведеться окремо й не є gate цього проєкту.
