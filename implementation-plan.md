# План реалізації IQ channelizer: FDC та generalized/resampling PFB

## 1. Призначення документа

Цей файл є робочою production-oriented специфікацією для Codex/іншої coding model. Реалізація повинна бути можливою без необхідності «домислювати» DSP conventions, phase conventions, memory layout або SIMD strategy.

Потрібно реалізувати потокове розділення широкосмугового complex IQ сигналу на довільно розташовані вузькосмугові канали щонайменше двома взаємозамінними способами:

1. **FDC — Frequency-Domain Channelizer** на основі overlap-save, одного shared forward FFT та batched short IFFT для requested channels.
2. **PFB — generalized/resampling Polyphase Filter Bank**:
   - перша correctness-реалізація може використовувати 2× oversampling (`Hop = FftSize / 2`);
   - внутрішня модель відразу повинна бути `FftSize + HopSize`, а не hardcoded `R = 2`;
   - після MVP потрібно підтримати arbitrary integer hop `1 <= HopSize <= FftSize`, тобто rational oversampling ratio `FftSize / HopSize`;
   - PFB повинен мати явну frame-dependent phase/circular-shift correction при `HopSize != FftSize`.

Третій можливий performance candidate — **selected-bin/direct-DFT PFB** — не є MVP, але architecture і benchmarks повинні дозволяти додати його без зміни public streaming API.

Базою для DSP-рішень є:

- Harris / Dick / Rice, *Digital Receivers and Transmitters Using Polyphase Filter Banks for Wireless Communications*:
  - commutator керує resampling, а FFT size/channel spacing не зобов’язані визначати output sample rate;
  - приклад 48:1 resampling усередині 64-path PFB;
  - arbitrary small-integer resampling ratios;
  - circular shifts перед FFT як еквівалент output phase rotations;
  - після decimation потрібно враховувати інтегровані folded sidelobes;
  - recursive polyphase filters залишаються future optimization.
- Intel, *Versatile Channelizer with DSP Builder for Intel FPGAs*:
  - oversampled PFB має `Hop < FftSize`;
  - при `Hop != FftSize` потрібен frame-dependent modulation term;
  - post-FFT modulation можна замінити circular shift до FFT;
  - для 2× oversampling odd bins на odd frames отримують sign flip;
  - fold-aware prototype design може суттєво зменшити FIR length;
  - при невеликому oversampling, наприклад ~20%, немає необхідності автоматично платити 2× FFT/PF work.

Обидва production engines повинні:

- працювати через спільне публічне API;
- приймати довільний input sample rate;
- приймати набір каналів із довільними central frequencies та bandwidths;
- визначати actual output rate як властивість resolved plan, а не припускати power-of-two semantics у public API;
- для FDC дозволяти optimized power-of-two decimation policy як implementation choice;
- для PFB дозволяти output/coarse rate, що визначається `HopSize`, незалежно від `FftSize`;
- повертати complex float samples кожного каналу разом із фактичним output sample rate;
- публікувати точні `HistorySize` і `ChunkSize` для наявного зовнішнього ring buffer;
- приймати на кожному виклику один contiguous span `[history | new chunk]`;
- для FDC копіювати overlap-save frame у forward FFTW buffer;
- для PFB читати history лише polyphase FIR і писати у FFTW buffer тільки filtered phase vectors;
- не знати тип, layout, cursors або synchronization implementation зовнішнього ring buffer;
- тримати `ChunkSize` bounded/configurable, щоб ring buffer міг містити кілька chunks запасу;
- використовувати FFTW single-precision C2C API;
- не виконувати managed allocations у steady-state hot path;
- не виконувати FFTW planning у hot path;
- мати scalar correctness path для кожного нетривіального SIMD kernel;
- мати AVX2/FMA production kernels на x64 як основний SIMD target;
- мати AVX-512 kernels там, де target CPU/.NET runtime їх підтримує і benchmark показує benefit;
- мати portable scalar fallback;
- по можливості vectorize **across outputs/phases**, щоб уникати horizontal reduction у внутрішніх FIR loops;
- не використовувати gather у найгарячішому PFB FIR loop, якщо ту саму операцію можна переформулювати як contiguous phase-vector loads;
- мати correctness-тести проти незалежної reference-реалізації;
- мати окремі BenchmarkDotNet benchmarks для primitives, FFTW та end-to-end engines.

AM demodulation не входить у ядро channelizer. На виході мають бути complex baseband samples, придатні для окремого AM/FM/SSB/digital demodulator.

Ключовий принцип для coding model: **не оптимізувати математично неперевірений код**. Для кожної optimized primitive спочатку існує проста scalar/reference формула, потім tests, потім SIMD implementation з тим самим contract.

---

## 2. Основні архітектурні рішення

### 2.1. Вхідний контракт: `HistorySize + ChunkSize`

Ring buffer уже існує поза цією library. Channelizer не реалізує ring buffer, scheduler, cursors, wrap handling або backpressure. Після створення engine публікує:

- `HistorySize` — кількість samples безпосередньо перед новим chunk, потрібних для згортки;
- `ChunkSize` — точна кількість нових samples, яку engine споживає за один виклик;
- `InputSize = HistorySize + ChunkSize`.

Кожен виклик отримує один contiguous `ReadOnlySpan<ComplexF>` довжини `InputSize`:

~~~text
input[0 .. HistorySize)                  previous samples, лише context
input[HistorySize .. InputSize)          рівно ChunkSize нових samples
firstNewSampleIndex                      absolute index input[HistorySize]
advance input cursor after the call      рівно ChunkSize
~~~

Наприклад, для `HistorySize = 8` і `ChunkSize = 8192` довжина input span дорівнює `8200`; перші 8 samples повторюються з кінця попередньої ділянки, а output генерується лише для 8192 нових samples. History ніколи не повинен породжувати повторний output.

На першому виклику caller надає history до початку логічного stream, зазвичай заповнений нулями. Після discontinuity caller створює новий engine або явно виконує `Reset(firstNewSampleIndex)` і знову надає визначений initial history.

### 2.2. Raw input history і FFTW buffers мають різну семантику

Зовнішній double-mapped ring buffer зберігає transport stream і формує contiguous `[history | chunk]`. Channelizer ніколи не пише в ring buffer, не керує ним і не зберігає переданий span після завершення `Process`.

Не застосовувати одне правило staging до обох engines:

| Engine | Хто читає `[history \| chunk]` | Що містить FFTW input buffer | Чи входить raw `HistorySize` у FFTW buffer |
| --- | --- | --- | --- |
| FDC overlap-save | staging copy | `N = HistorySize + ChunkSize` raw complex samples | Так, overlap є частиною forward FFT |
| Oversampled PFB | SIMD polyphase FIR | `K × M` уже відфільтрованих polyphase vectors | Ні, history потрібен лише згортці |

Отже, для PFB не копіювати весь raw input у FFTW buffer. Polyphase kernel читає потрібні taps із `[history | chunk]` і одразу записує свої `K` vectors довжини `M` у preallocated aligned buffer для `fftwf_plan_many_dft`.

Для FDC чистий copy raw overlap-save frame у FFTW input buffer реалізувати через оптимізований `Span.CopyTo`, `Buffer.MemoryCopy` або еквівалент і benchmark проти ручного SIMD. Ручний AVX2/AVX-512 має сенс лише для fused copy/scale/conversion, якщо він реально швидший.

Усі FFTW input/output buffers виділяються один раз, мають потрібне alignment та належать engine. Output router передає ephemeral output spans відповідним consumers.

### 2.3. `ChunkSize` є bounded planning constraint

Не дозволяти FDC planner автоматично перетворювати великий FFT frame на надто великий зовнішній `ChunkSize`. Caller задає `PreferredChunkSize` і обов’язковий `MaxChunkSize`; resolved engine повертає точний `ChunkSize`, що не перевищує maximum і задовольняє DSP alignment.

Ring owner вибирає maximum так, щоб у buffer залишався запас щонайменше на кілька chunks між writer і reader. Базова перевірка для `K` chunks запасу:

~~~text
RingCapacity >= HistorySize + K * ChunkSize + WriterBurstReserve
K >= 3, рекомендовано 4 або більше; точне значення визначає latency/backpressure policy application
~~~

Оскільки `HistorySize` відомий лише після filter planning, factory повинна повертати resolved requirements до запуску stream. Application валідує їх проти місткості свого ring buffer. Channelizer не приймає ring instance або ring capacity.

Для FDC power-of-two вимагається від decimation `D`, але не обов’язково від FFT length `N`. FFTW ефективно підтримує також smooth composite lengths. Planner повинен шукати `N = HistorySize + ChunkSize`, кратне потрібним `D`, із bounded `ChunkSize`, а фактичну швидкість кандидатів визначати benchmark/profile data.

### 2.4. Один data layout

Використовувати наявний blittable complex float type із двох послідовних float:

~~~csharp
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct ComplexF
{
    public float Real;
    public float Imaginary;
}
~~~

Назву потрібно адаптувати до вже наявного типу в repository, не створюючи зайвої конвертації.

Обов’язкові runtime/unit assertions:

- Unsafe.SizeOf<ComplexF>() == 8;
- layout відповідає FFTW fftwf_complex;
- real та imaginary розташовані послідовно;
- input/output buffers мають потрібне alignment.

Не використовувати System.Numerics.Complex у hot path, оскільки він зберігає два double.

### 2.5. Output sample rate не є тотожним FFT size або power-of-two decimation

Public API не повинен обіцяти, що output rate завжди є `Fs / 2^k`.

Для FDC MVP оптимізований planner може використовувати:

~~~text
D = 2^k
FsOut = FsIn / D
~~~

але це **engine capability/policy**, а не фундаментальна семантика `ChannelRequest`.

Для generalized PFB:

~~~text
K          = FftSize / number of polyphase branches
H          = HopSize, input samples між сусідніми PFB frames
DeltaF     = FsIn / K
FsCoarse   = FsIn / H
OSR        = K / H
~~~

`K` визначає coarse frequency grid, а `H` визначає coarse output sample rate. Це два незалежні planning dimensions.

Якщо channels у request мають різні target output rates:

- FDC групує їх за resolved `D` / short-IFFT length;
- PFB може:
  1. використовувати один shared coarse PFB і per-channel fine decimator;
  2. створити кілька PFB groups з різними `H`, якщо benchmark показує, що це дешевше;
  3. у майбутньому використовувати rational fine resampler.

MVP PFB може залишити один shared `H` та power-of-two fine decimator, але architecture не повинна робити `R=2` частиною public contract.

### 2.6. PFB DFT direction і frame phase correction фіксуються однозначно

Щоб coding model не вгадувала FFT sign/bin reversal, для PFB прийняти окрему canonical convention.

Нехай:

~~~text
K        = PFB FFT size
H        = hop
r_m      = absolute input index newest sample of frame m
omega_k  = 2*pi*k/K
~~~

Desired direct-DDC result на coarse center `k`:

~~~text
y_k[m] =
    sum(l=0..T-1)
        h[l] * x[r_m-l] * exp(-j*omega_k*(r_m-l))
~~~

Розкладемо `l = p + q*K`:

~~~text
u_m[p] =
    sum(q=0..P-1)
        h[p + q*K] * x[r_m - (p + q*K)]
~~~

Тоді:

~~~text
B_m[k] =
    sum(p=0..K-1)
        u_m[p] * exp(+j*omega_k*p)
~~~

і:

~~~text
y_k[m] = exp(-j*omega_k*r_m) * B_m[k]
~~~

Отже для PFB production convention використовувати **FFTW_BACKWARD** як unnormalized `+j` DFT над phase vector. Не ділити його результат на K: ця DFT є phase-summing operation filter bank, а не normalized inverse transform. Prototype taps нормалізуються окремо.

Потрібна frame-dependent correction:

~~~text
C(r_m,k) = exp(-j * 2*pi * k * r_m / K)
~~~

Якщо frames починаються з phase origin, де `r_m = r_0 + m*H`, relative correction між frames:

~~~text
C(m,k)/C(0,k) =
    exp(-j * 2*pi * k * m * H / K)
~~~

Для `H=K/2`:

~~~text
relative correction = (-1)^(m*k)
~~~

тобто odd bin на odd relative frame змінює знак.

Preferred production implementation прибирає post-FFT complex multiply через time-shift property.

Нехай:

~~~text
s_m = Mod(r_m, K)        // mathematical modulo 0..K-1
uShifted_m[p] = u_m[(p + s_m) mod K]   // cyclic LEFT shift
~~~

Тоді для FFTW_BACKWARD:

~~~text
DFTbackward(uShifted)[k]
    = exp(-j*2*pi*k*s_m/K) * B_m[k]
    = C(r_m,k) * B_m[k]
    = y_k[m]
~~~

оскільки `r_m` і `s_m` congruent modulo K.

Це означає:

- phase correction можна повністю absorb у порядок запису phase FIR outputs до FFTW input;
- post-FFT PFB phase multiplier у production path не потрібен;
- scalar/reference path **повинен** мати обидва варіанти:
  1. unshifted vector + explicit complex `C`;
  2. pre-DFT cyclic-left-shift;
- tests доводять їх equivalence.

Positive signed coarse frequency mapping:

~~~text
k = 0 .. K-1
signedBin = k <= K/2 ? k : k-K
fCoarse = signedBin * FsIn / K
~~~

Special handling Nyquist bin для even K документувати окремо.

### 2.7. Не фізично обертати buffers у hot path

Circular shifts із Harris/Intel — математична операція. У CPU implementation не копіювати `K` values тільки заради rotation.

Preferred order:

1. scalar reference може використовувати простий modulo indexing;
2. optimized PFB FIR одразу записує filtered phase outputs у FFTW input у rotated order;
3. rotation реалізується двома contiguous destination segments:
   - `[s .. K)` / `[0 .. s)`, або еквівалентною парою source segments;
4. inner phase-vector kernel не має `% K`;
5. якщо shift aligned до SIMD width — обидва loops повністю vectorized;
6. якщо ні — обробити невеликий prefix/suffix scalar, основну частину SIMD.

Для `K` power-of-two modulo при scheduler calculation можна виконувати `& (K - 1)`, але тільки поза inner FIR loop.

### 2.8. Auto strategy не реалізовувати до benchmark data

Початковий public strategy:

- `Fdc`;
- `Pfb`;
- `Auto`.

`Auto` не повинен містити «розумну» heuristic до вимірювань. До Phase Auto він або явно unsupported, або використовує documented temporary default лише в test/demo code.

Пізніше Auto може розглядати:

- unique requested channel count;
- unique coarse bins;
- channel density;
- filter lengths / overlap overhead;
- PFB `K`, `H`, taps-per-phase;
- number of PFB groups;
- selected-bin DFT estimated work;
- target latency;
- AVX2/AVX-512 availability;
- measured FFTW profiles.

### 2.9. SIMD є частиною design, а не фінальним cosmetic tuning

Scalar path потрібен для correctness, але layouts повинні проектуватися так, щоб не блокувати SIMD.

Обов’язкові design rules:

- public IQ layout — AoS `re,im,re,im...`;
- internal PFB coefficient layout може мати SIMD-expanded copy;
- hot PFB FIR vectorize по phase/output dimension, не по sparse taps;
- FFTW buffers 64-byte aligned;
- ISA dispatch виконується один раз при engine creation;
- не перевіряти `Avx2.IsSupported` / `Avx512*.IsSupported` у per-sample loop;
- один block-level virtual/delegate/function-pointer dispatch прийнятний;
- no LINQ, enumerators, closures, iterator state machines;
- no bounds-check-heavy indexing у proven hot loops: після scalar tests дозволені `unsafe` pointer kernels;
- `unsafe` kernel має окремий safe wrapper, який validates lengths/alignment один раз;
- ручний prefetch додається лише після hardware-counter benchmark;
- AVX-512 не вважати автоматично швидшим: враховувати downclock і memory bandwidth.

---

## 3. Запропонована структура solution

Solution повинна містити **рівно три projects / `.csproj`**:

1. `IqChannelizer` — увесь production code без поділу на додаткові assemblies;
2. `IqChannelizer.Tests` — усі unit, signal та integration tests;
3. `IqChannelizer.Benchmarks` — усі BenchmarkDotNet benchmarks і performance profiles.

Не створювати окремі projects для abstractions, runtime, DSP, FFTW, FDC або PFB. Межі між цими частинами зберігати через папки, namespaces та `internal` API всередині `IqChannelizer`. Назви namespace/project адаптувати до repository, але кількість projects не змінювати.

~~~text
IqChannelizer.sln

src/
  IqChannelizer/
    IqChannelizer.csproj

    Abstractions/
      public request/plan/streaming contracts
      timing/rational sample-position types

    Runtime/
      validation
      planner shared policies
      input requirements
      diagnostics
      ISA/capability detection

    Dsp/
      ComplexF helpers
      filter design + response validation
      alias-folded response evaluator
      residual NCO / rotator
      half-band/fine decimators
      scalar kernels
      AVX2/FMA kernels
      AVX-512 kernels
      aligned unmanaged buffers

    Fftw/
      P/Invoke or minimal C shim
      fftwf_malloc/free wrappers
      plan cache
      wisdom
      plan_many C2C
      benchmark-profile loading contracts

    Fdc/
      FDC planner
      overlap-save engine
      spectral slice extraction
      FDC phase/normalization
      grouped batched IFFT

    Pfb/
      PFB planner
      prototype designer
      scalar generalized PFB reference
      SIMD phase-vector FIR
      frame phase/circular-shift scheduler
      batched FFT
      selected-bin router
      per-channel refiners
      future selected-bin DFT

    Channelizer.cs / facade and factory
    explicit strategy selection
    future Auto

tests/
  IqChannelizer.Tests/
    IqChannelizer.Tests.csproj
    Unit/
      pure primitives and algebraic invariants
    Signal/
      tones, blockers, chirps, reference-DDC comparisons
    Integration/
      exact HistorySize/ChunkSize streaming contract
      long-run continuity
      allocation tests

benchmarks/
  IqChannelizer.Benchmarks/
    IqChannelizer.Benchmarks.csproj
    FFTW/
    SIMD primitives/
    FDC/
    PFB/
    selected-bin DFT experimental/
    end-to-end comparison/
    Profiles/
      serialized benchmark profiles consumed by planning

artifacts/
  benchmarks/
  signal-validation/
  generated-filter-responses/
~~~

Project-reference rules:

- `IqChannelizer.Tests` references only `IqChannelizer` plus test packages;
- `IqChannelizer.Benchmarks` references only `IqChannelizer` plus BenchmarkDotNet packages;
- `IqChannelizer` never references the tests or benchmark project;
- production code and production runtime dependencies must not be placed in either auxiliary project;
- benchmark-profile data may be generated by `IqChannelizer.Benchmarks`, but the schema/loader used at runtime belongs to `IqChannelizer`.

Якщо repository уже має complex type, aligned native buffers, FFTW interop, FIR designer або ring-buffer contracts, перевикористати їх усередині main production project. Перед створенням нового folder/namespace coding model має виконати repository search. Не додавати четвертий project заради shared test utilities, native interop, source generators або formal layering; такі компоненти мають залишатися в одному з трьох projects, переважно в `IqChannelizer` якщо вони потрібні production runtime.

---

## 4. Публічне API

### 4.1. Конфігурація channelizer

Не кодувати `OversampledPfb` у назві strategy: PFB engine має еволюціонувати від R=2 до arbitrary hop без API break.

~~~csharp
public enum ChannelizerStrategy
{
    Fdc,
    Pfb,
    Auto
}

public sealed record ChannelizerRequest(
    double InputSampleRateHz,
    IReadOnlyList<ChannelRequest> Channels,
    ChannelizerStrategy Strategy = ChannelizerStrategy.Fdc,
    InputBlockConstraints? InputBlocks = null,
    ChannelizerImplementationHints? Hints = null);

public sealed record InputBlockConstraints(
    int PreferredChunkSize = 8192,
    int MaxChunkSize = 32768);
~~~

`PreferredChunkSize` — tuning target. `MaxChunkSize` — hard upper bound.

Advanced hints не є semantic requirement і можуть бути `null`:

~~~csharp
public sealed record ChannelizerImplementationHints(
    int? FdcDecimationFactor = null,
    int? PfbFftSize = null,
    int? PfbHopSize = null,
    int? PfbFramesPerBatch = null,
    SimdPreference Simd = SimdPreference.Auto);
~~~

Production caller зазвичай не задає hints. Benchmarks/tests можуть задавати їх для forced configurations.

### 4.2. Опис каналу

Public contract задає вимоги до сигналу, а не конкретний DSP decomposition.

~~~csharp
public sealed record ChannelRequest(
    int ChannelId,
    double CenterFrequencyHz,
    double PassbandWidthHz,
    double TransitionWidthHz,
    double StopbandAttenuationDb = 80.0,
    double PassbandRippleDb = 0.1,
    double? MinimumOutputSampleRateHz = null,
    double? PreferredOutputSampleRateHz = null);
~~~

Семантика:

- `ChannelId` — opaque application-defined `int`; IDs у межах одного request мають бути unique;
- channelizer не перенумеровує IDs і передає той самий `ChannelId` у `IChannelOutputSink.Write`;
- `CenterFrequencyHz` відносно DC input complex IQ;
- valid range `[-Fs/2, Fs/2)`;
- `PassbandWidthHz` — **full complex occupied bandwidth**;
- pass edge після mixing:
  `Fpass = PassbandWidthHz / 2`;
- `TransitionWidthHz` — повна необхідна transition allowance від pass edge до stop edge policy;
- `MinimumOutputSampleRateHz` — hard lower bound;
- `PreferredOutputSampleRateHz` — soft target;
- actual output rate повертає resolved plan.

FDC MVP policy може вибирати найбільший `D=2^k`, для якого:

~~~text
FsOut = FsIn / D
FsOut >= MinimumRateRequiredByFilter
FsOut >= MinimumOutputSampleRateHz, if specified
~~~

але це **не** правило public API.

PFB planner вибирає `K` і `H`, а потім за потреби fine decimation.

### 4.3. Resolved plan

Factory/engine expose immutable `ResolvedChannelizerPlan`.

Per-channel:

- integer `ChannelId`;
- requested/normalized center;
- pass/transition/attenuation/ripple;
- actual output sample rate;
- resolved coarse rate;
- FDC: `D`, short-IFFT length, coarse bin, residual;
- PFB: PFB group id, `K`, `H`, coarse bin, residual;
- fine decimation factor, якщо є;
- prototype/fine filter identifiers;
- exact group delay у input-sample units;
- exact first-output timing convention;
- exact `OutputSamplesPerProcess`;
- exact output sample rate;
- exact first-output/group-delay timing metadata, доступне через plan, а не hot-path callback;
- warning/rejection reason.

Engine-level:

- strategy;
- input sample rate;
- exact `HistorySize`, `ChunkSize`, `InputSize`;
- chunk alignment;
- FFT lengths/batches;
- PFB `K`, `H`, frames-per-batch, oversampling ratio;
- PFB phase-shift mode (`PreFftCircularShift` preferred);
- taps per phase;
- filter-design mode (`Conservative` / `FoldAware`);
- selected SIMD backend;
- FFTW thread count;
- aligned buffer sizes;
- estimated working set;
- benchmark-profile key used for planning, якщо є.

### 4.4. Streaming input contract

~~~csharp
public readonly record struct InputRequirements(
    int HistorySize,
    int ChunkSize)
{
    public int InputSize => checked(HistorySize + ChunkSize);
}

public interface IStreamingChannelizer : IDisposable
{
    ResolvedChannelizerPlan Plan { get; }
    InputRequirements InputRequirements { get; }

    void Process(
        ReadOnlySpan<ComplexF> historyAndChunk,
        long firstNewSampleIndex,
        IChannelOutputSink output);
}
~~~

`Process`:

1. validates exact span length;
2. validates continuity;
3. does not retain caller span;
4. FDC copies full `[history | chunk]` into aligned FFT input;
5. PFB FIR reads caller span directly and writes only filtered phase vectors into aligned FFT input;
6. produces outputs only for new `ChunkSize`, never for repeated history;
7. updates expected absolute input index by exactly `ChunkSize`.

No ring buffer reference, cursor, capacity, lock або backpressure policy enters this API.

### 4.5. Мінімальний output contract

Hot-path callback повинен бути максимально простим. Не передавати `ChannelSampleBlock`, sample rate, timestamps або іншу metadata на кожному `Write`.

Public output interface:

~~~csharp
public interface IChannelOutputSink
{
    void Write(int channelId, ReadOnlySpan<ComplexF> samples);
}
~~~

`ReadOnlySpan<ComplexF>` валідний тільки до повернення з `Write`. Consumer повинен скопіювати samples, якщо хоче зберігати їх довше.

Обов’язкові invariants:

- `channelId` точно дорівнює `ChannelRequest.ChannelId`;
- IDs у request unique;
- channelizer не remap/renumber IDs;
- `Write` не отримує empty block у normal streaming path;
- один `Process` викликає `Write` **рівно один раз для кожного active channel**;
- порядок `Write` стабільний протягом lifetime engine і відповідає порядку resolved channel plans;
- `samples.Length == channelPlan.OutputSamplesPerProcess`;
- `OutputSamplesPerProcess` є точним і незмінним протягом lifetime resolved engine;
- callback span є ephemeral view на engine-owned preallocated output buffer;
- sink не викликається з internal scratch buffer, який буде перезаписаний до повернення з `Write`;
- `Write` не виконується паралельно для одного engine, якщо engine явно не документований як concurrent.

Щоб simple sink був можливим, planner повинен вибирати block sizes так, щоб кожен channel мав **ціле deterministic число output samples на кожен `Process`**.

Для FDC:

~~~text
OutputSamplesPerProcess = ChunkSize / D
~~~

тому `ChunkSize` має бути кратним кожному resolved D.

Для PFB з fine decimation:

~~~text
coarse frames per Process = FramesPerBatch
OutputSamplesPerProcess   = FramesPerBatch / FineDecimation
~~~

для MVP power-of-two fine stage. Отже `FramesPerBatch` має бути кратним усім `FineDecimation` у PFB group. Якщо це неможливо під `MaxChunkSize`, planner повинен вибрати інший `(K,H,FramesPerBatch)` або відхилити candidate.

Якщо пізніше буде додано rational fine resampler із non-constant block output count, не ламати цей interface мовчки. Або planner вибирає super-block із deterministic integer output count, або вводиться окремий streaming contract/version.

### 4.5.1. Sample rate і timing зберігаються в resolved plan

Sample rate не потрібно дублювати в кожному callback. Consumer один раз будує lookup `ChannelId -> ResolvedChannelPlan`.

~~~csharp
public sealed record ResolvedChannelPlan
{
    public required int ChannelId { get; init; }
    public required double OutputSampleRateHz { get; init; }
    public required int OutputSamplesPerProcess { get; init; }

    public required RationalSampleOffset GroupDelayInputSamples { get; init; }
    public required RationalSampleOffset InputSamplesPerOutputSample { get; init; }
}

public readonly record struct RationalSampleOffset(
    long Numerator,
    long Denominator);
~~~

`RationalSampleOffset` нормалізується при planning, не в hot path, і не передається через `Write`.

Якщо consumer потребує absolute timestamp, application уже знає `firstNewSampleIndex`, номер `Process`, `OutputSamplesPerProcess`, output rate та group delay із plan. Тому metadata object на кожен callback не потрібен.

### 4.5.2. Output ordering

Для reproducibility та дешевого routing:

- resolved plan зберігає channels у deterministic order, рекомендовано original request order;
- engine створює parallel arrays `channelIds[]`, `channelPlans[]`, `outputBuffers[]`;
- hot path routing не використовує dictionary;
- dictionary `ChannelId -> plan index` дозволений лише поза hot path;
- після завершення processing channel `i` викликається:
  `output.Write(channelIds[i], outputBuffers[i].Span)`;
- якщо кілька requested channels ділять один PFB coarse bin, coarse stream обчислюється один раз, але кожен downstream channel отримує окремий `Write`.

### 4.6. Приклад API

~~~csharp
var request = new ChannelizerRequest(
    InputSampleRateHz: 100_000_000,
    Channels:
    [
        new ChannelRequest(
            ChannelId: 1,
            CenterFrequencyHz: -12_345_000,
            PassbandWidthHz: 12_000,
            TransitionWidthHz: 8_000,
            PreferredOutputSampleRateHz: 24_000),

        new ChannelRequest(
            ChannelId: 2,
            CenterFrequencyHz: 21_750_000,
            PassbandWidthHz: 15_000,
            TransitionWidthHz: 9_000)
    ],
    Strategy: ChannelizerStrategy.Pfb);

using var channelizer = ChannelizerFactory.Create(request);

var r = channelizer.InputRequirements;

ReadOnlySpan<ComplexF> input = ringBuffer.GetContiguousSpan(
    firstNewSampleIndex - r.HistorySize,
    r.InputSize);

channelizer.Process(input, firstNewSampleIndex, outputSink);
firstNewSampleIndex += r.ChunkSize;
~~~

Application використовує resolved `Plan` для перевірки ring capacity та actual output rates.

---

## 5. Shared DSP modules

### 5.1. Filter specification та design modes

~~~csharp
public readonly record struct LowPassFilterSpec(
    double InputSampleRateHz,
    double PassbandEdgeHz,
    double StopbandEdgeHz,
    double PassbandRippleDb,
    double StopbandAttenuationDb);
~~~

Перший production designer:

- Kaiser-windowed sinc;
- deterministic order estimation;
- `double` під час design;
- result taps as `float`;
- response metadata зберігається;
- cache by normalized spec;
- no design in hot path.

Пізніше:

- weighted/equiripple designer;
- modified weighting/end-point policy для falling sidelobes;
- fold-aware PFB designer.

### 5.2. Alias-folded response validation є обов’язковою

Не вважати standalone FIR stopband attenuation достатньою після decimation.

Причина: stopband sidelobes різних Nyquist images складаються після downsampling. Constant-height Remez sidelobes можуть дати integrated folded leakage значно вище nominal stopband level.

Потрібен окремий `AliasedResponseEvaluator`.

Він має два рівні:

1. **frequency-domain evaluator**:
   - бере dense complex response `H(f)`;
   - моделює spectral replicas/folding для заданого rate change;
   - оцінює worst-case alias entering requested passband;
   - conservative mode може сумувати magnitudes, не покладаючись на lucky phase cancellation.
2. **signal validation**:
   - tone sweep через всі alias bands;
   - blocker level known;
   - після decimation виміряти максимальний blocker leakage у passband.

Planner приймає filter тільки якщо:

- passband ripple meets requested value;
- nominal stopband meets requested value;
- **folded/aliased leakage** meets requested value з визначеним engineering margin.

Recommended initial margin: configurable 3–6 dB; не hardcode без tests.

### 5.3. PFB prototype design: Conservative та FoldAware

`PfbPrototypeDesignMode`:

~~~csharp
public enum PfbPrototypeDesignMode
{
    Conservative,
    FoldAware
}
~~~

`Conservative`:

- transition завершується до alias/folding boundary coarse output;
- простіше reason/test;
- use for first scalar PFB.

`FoldAware`:

- дозволяє частині transition band перейти за folding boundary;
- folded transition може повертатися в transition region, але не в required passband;
- candidate може мати приблизно вдвічі ширшу effective transition і значно менше taps;
- **ніколи** не приймати лише за формулою;
- обов’язково прогнати `AliasedResponseEvaluator` та blocker sweep.

Planner benchmark'ить обидва designs, якщо обидва проходять signal spec.

### 5.4. Residual frequency rotator

Correctness reference:

~~~text
y[n] = x[n] * exp(-j * 2*pi * fResidual * t[n])
~~~

Phase origin determined from absolute input/output timing, not from number of `Process` calls.

Production oscillator:

- double-precision base phase/state;
- compute `step = exp(-j*omega)` once;
- SIMD block width `Vcomplex`:
  - AVX2: 4 complex float per 256-bit vector;
  - AVX-512: 8 complex float per 512-bit vector;
- precompute lane phasors:
  `1, step, step^2, ... step^(Vcomplex-1)`;
- each SIMD iteration:
  1. build/multiply lane phasors from current base;
  2. complex-multiply V complex input values;
  3. advance base by `step^Vcomplex`;
- periodic renormalization/re-anchor from absolute index in double precision;
- `Math.SinCos` only at setup/re-anchor, not per sample;
- scalar tail.

Correctness tests:

- random initial phase;
- positive/negative residual;
- millions of samples;
- split stream into different chunk boundaries;
- scalar vs SIMD;
- max phase drift and magnitude error.

### 5.5. Fine power-of-two decimator

MVP fine stage may support `Dfine = 1,2,4,8,...` using cascaded half-band filters.

Exploit half-band structure:

- every second coefficient (except center) is zero;
- do not multiply zero coefficients;
- center tap handled separately;
- maintain streaming state;
- output count deterministic.

Implement and benchmark two SIMD kernel shapes:

**A. Tap-parallel**

- one/few output samples at a time;
- vectorize across contiguous taps;
- duplicated real coefficients `[c,c,c,c...]` multiply interleaved complex data;
- horizontal reduction only once per output.

**B. Output-parallel**

- compute 4 AVX2 or 8 AVX-512 complex outputs in parallel;
- for each nonzero tap, load/rearrange the required input samples for consecutive decimated outputs;
- accumulate each output lane directly;
- avoids horizontal reduction but may need shuffle/permute.

Do not assume one is faster. Benchmark per taps count/CPU and select at engine creation.

### 5.6. SIMD backend dispatch

~~~csharp
public enum SimdBackend
{
    Scalar,
    Avx2Fma,
    Avx512
}
~~~

Rules:

- detect once at initialization;
- resolved plan records selected backend;
- no ISA checks in inner loops;
- forced backend available in tests/benchmarks;
- forced unsupported backend => validation error;
- scalar output is reference for SIMD tests.

Preferred dispatch mechanisms:

- sealed backend object called once per block;
- static function pointer/delegate cached at construction;
- avoid interface/virtual call per sample/tap.

### 5.7. Complex AoS SIMD primitives

Public/internal base layout remains:

~~~text
re0 im0 re1 im1 re2 im2 ...
~~~

Required primitives:

1. `ScaleComplexByReal`
2. `MultiplyComplexByComplex`
3. `MultiplyComplexByScalarComplex`
4. `CopyScaleComplex`
5. `MagnitudeSquared` only if downstream needs it
6. `AddComplexVectors`

For real coefficient vectors, store or create duplicated lanes:

~~~text
c0 c0 c1 c1 c2 c2 c3 c3   // AVX2, 4 complex values
~~~

For very hot kernels benchmark:

- compact coefficient load + lane duplication via shuffle;
- pre-expanded coefficient storage.

Pre-expanded layout costs 2× coefficient bytes but can remove shuffle pressure. Keep both implementations behind benchmark, not ideology.

### 5.8. PFB FIR SIMD: vectorize across phases

Це найважливіший SIMD kernel.

Prototype length:

~~~text
T = K * P
K = phase/FFT count
P = taps per phase
h_phase[p, q] = h[p + q*K]
~~~

Scalar canonical formula має бути записана в code comments/tests. Конкретний sign/reversal залежить від commutator convention, наприклад концептуально:

~~~text
v_m[p] = sum(q=0..P-1)
             h_phase[p,q] * x[frameBase(m) - p - q*K]
~~~

Production layout треба вибрати так, щоб **для fixed q і consecutive phases p** input samples були contiguous або перетворювалися на contiguous шляхом branch numbering/reversal.

Preferred kernel shape:

~~~text
for each frame m:
    determine logical pre-FFT circular shift s_m
    for phase block p = 0..K-1 step Vcomplex:
        acc = 0
        for q = 0..P-1:
            xvec = load Vcomplex consecutive ComplexF samples
            cvec = load/expand Vcomplex real phase coefficients
            acc += xvec * duplicated(cvec)
        store acc directly to FFT input at rotated destination
~~~

Benefits:

- AVX2 computes 4 polyphase outputs simultaneously;
- AVX-512 computes 8;
- no horizontal reduction;
- no gather in ideal path;
- each q processes a contiguous block;
- destination rotation can be fused with store indexing.

Coefficient layouts to benchmark:

~~~text
CompactByTap:
  coeff[q][p]              // K floats per q

ExpandedByTapAvx2:
  c0,c0,c1,c1,c2,c2,c3,c3 ...

ExpandedByTapAvx512:
  c0,c0,... c7,c7
~~~

Do not create expanded copies for ISA not selected.

If scalar formula naturally walks input phases in reverse order:

- prefer reversing coefficient/phase numbering once during plan creation;
- або use two contiguous loops;
- avoid per-vector lane reversal if precomputed layout can remove it.

### 5.9. PFB frame rotation fused into FIR store

For frame `m` compute `s_m` outside inner loop.

Do not:

~~~text
FIR -> temp[K] -> rotate copy[K] -> FFT input
~~~

Do:

~~~text
FIR -> FFT input in already rotated order
~~~

Implementation must split destination into at most two contiguous regions.

Example structure:

~~~text
leftCount  = K - s
dstA       = fftInput + frameOffset
dstB       = dstA + leftCount

compute logical phases [s .. K) -> dstA
compute logical phases [0 .. s) -> dstB
~~~

Exact mapping direction is tied to the scalar phase-correction test.

### 5.10. FDC spectral extraction SIMD

Implement hot extraction as at most two contiguous source segments because FFT spectrum may wrap.

No per-bin modulo.

Kernels:

- copy only;
- real-window multiply;
- complex-window multiply;
- complex-window + block scalar phase;
- optional normalization fused if numerically convenient.

AVX2/AVX-512:

- real window: easiest/high-throughput, duplicated real coefficients;
- complex window: specialized interleaved complex multiply;
- process aligned/unaligned safely; internal destinations aligned;
- scalar tail only.

If channel windows identical, share immutable coefficient buffers.

### 5.11. Buffer ownership/alignment

Prefer FFTW-owned allocation for FFT buffers:

- `fftwf_malloc` / `fftwf_free`, or equivalent guaranteed FFTW-compatible aligned allocation;
- minimum effective alignment target 64 bytes;
- wrap in `SafeHandle`/disposable owner outside hot path;
- expose spans only transiently;
- debug poison on dispose if practical.

Other DSP state may use `NativeMemory.AlignedAlloc`, provided alignment and free paths are tested.

No aligned allocation/free in `Process`.

### 5.12. Optional software prefetch

Не додавати наосліп.

PFB phase-vector FIR may stream large history/coefficient ranges. Only if hardware counters show L1/L2 miss bottleneck:

- benchmark software prefetch 1–3 tap-groups ahead;
- separate input and coefficient prefetch experiments;
- keep only if end-to-end improves;
- record CPU-specific result.

---

## 6. FFTW module

### 6.1. Загальні вимоги

Використовувати single-precision fftwf API:

- complex-to-complex transforms;
- forward та backward directions;
- FFTW_MEASURE для production plan creation;
- FFTW_ESTIMATE для швидких unit tests за потреби;
- FFTW wisdom import/export;
- fftwf_plan_many_dft для batched transforms;
- aligned memory;
- не використовувати FFTW_UNALIGNED;
- inverse normalization має бути явною і покриватися тестами.

PFB convention in this plan:

- FDC uses FFTW_FORWARD for the large transform and FFTW_BACKWARD for short reconstruction;
- PFB phase separation uses FFTW_BACKWARD intentionally, because the exact polyphase derivation requires the `+j` DFT; 
- PFB backward transform is **not divided by K**;
- FDC short backward transform follows the separate `1/N` FDC normalization rule.

FFTW planning виконується лише при створенні або reconfiguration plan.

### 6.2. Native interop

Спочатку перевірити, чи direct P/Invoke API достатньо стабільний для target platforms.

Якщо signatures, platform packaging або thread API стають крихкими, створити мінімальний native C/C++ shim із C ABI:

~~~text
iqfft_create_plan
iqfft_execute
iqfft_destroy_plan
iqfft_import_wisdom
iqfft_export_wisdom
iqfft_get_last_error
~~~

Не переносити весь channelizer у C++ без benchmark-доказу. Native boundary має бути на рівні FFT frame/batch, а не sample.

### 6.3. Plan cache

Ключ plan cache:

~~~text
length
direction
batch count
input/output stride
in-place/out-of-place
thread count
alignment class
~~~

Вимоги:

- plan creation серіалізується;
- execution thread-safety документується;
- один engine не виконує той самий mutable plan одночасно без гарантії FFTW;
- buffers, на яких вимірювався plan, мають сумісне alignment;
- cache очищується при shutdown.

### 6.4. FFTW tests

Потрібні тести:

- impulse forward;
- single-bin tone;
- negative-frequency tone;
- forward + inverse round trip;
- correct inverse scale;
- in-place та out-of-place, якщо обидва підтримуються;
- batched transforms;
- non-unit batch distance/stride, якщо використовується;
- exact ComplexF layout;
- aligned buffer contract;
- repeated execution without allocation.

### 6.5. Licensing task

Окремо зафіксувати рішення щодо FFTW GPL/commercial licensing та способу distribution native binaries. Не залишати це неявним.

---

## 7. FDC engine

### 7.1. Mathematical contract

For one resolved channel:

~~~text
N = HistorySize + ChunkSize
D = resolved integer decimation, MVP optimized for power-of-two
Lshort = N / D
~~~

One input frame produces `ChunkSize / D` new output samples after overlap removal.

Forward FFT uses FFTW convention:

~~~text
X[k] = sum(n=0..N-1) x[n] * exp(-j*2*pi*k*n/N)
~~~

FFTW backward is unnormalized.

After selecting `Lshort = N/D` bins from the N-point spectrum, time-domain decimation identity implies the final backward result must be normalized by **1/N**, not naively by `1/Lshort`, якщо selected bins were copied from the unnormalized N-point FFT without an extra `1/D` factor.

This must be encoded as an explicit invariant:

~~~text
FdcInverseScale = 1.0f / N
~~~

for the chosen implementation convention.

Obов’язковий amplitude test: unit-amplitude bin-centered tone -> unit-amplitude output (within float tolerance) for several D.

### 7.2. Processing algorithm

1. Validate exact `[history | chunk]`.
2. Copy all `N` raw complex samples into aligned forward FFTW input.
3. Execute one N-point forward FFT.
4. For each channel/group:
   - resolve coarse center bin;
   - extract exactly `N/D` bins with wrap;
   - apply anti-alias frequency response/window;
   - apply block phase correction required by absolute frame origin;
   - write short-IFFT input.
5. Execute batched backward FFT per distinct `N/D`.
6. Multiply backward output by `1/N` according to the fixed convention.
7. Discard exactly `HistorySize / D` leading output samples.
8. Apply residual frequency rotation using absolute timing.
9. Emit exactly `ChunkSize / D` new samples.

History participates in FFT but never causes duplicated emitted output.

### 7.3. FDC filter/window design

Do not automatically design a huge input-rate FIR and then sample its FFT blindly without validation.

Planner must define for each distinct output/filter group:

- desired baseband pass edge;
- stop edge relative to `FsOut/2`;
- maximum coarse-bin residual that the residual rotator will correct;
- transition margin;
- required attenuation after decimation.

Frequency-domain window may be generated from a time-domain anti-alias FIR and sampled on N-grid, or directly from a validated frequency response. Whatever method is chosen:

- scalar reference DDC must match effective response;
- output amplitude/group delay must be documented;
- aliased response validation applies.

### 7.4. FDC planner

Steps:

1. For each channel determine required minimum output rate.
2. Enumerate allowed `D` values. MVP default:
   `1,2,4,...`.
3. If benchmark hint forces `D`, validate it.
4. Design/resolve filter for each candidate D.
5. Determine required history `filterLength - 1`.
6. Engine-wide `HistorySize` must be compatible with all channel D groups.
7. For power-of-two D groups, align history/chunk to `max(D)`.
8. Enumerate candidate `ChunkSize <= MaxChunkSize`.
9. For each candidate:
   - `N = History + Chunk`;
   - `N % D == 0` for all groups;
   - FFTW forward length profile acceptable;
   - short IFFT lengths profile acceptable;
   - overlap overhead `N/Chunk` acceptable;
   - working set within limits.
10. Choose based on measured profile when available.

Do not round to next power-of-two FFT if that violates chunk bound. Smooth composite FFTW lengths are valid candidates.

### 7.5. Candidate profile at 100 MS/s

Keep as benchmark profile, not default:

~~~text
D = 4096
FsOut = 24,414.0625 Hz
History = 65,536
~~~

| Chunk | N | N/D | discard | valid | calls/s @100M | N/Chunk |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 8,192 | 73,728 | 18 | 16 | 2 | 12,207.0 | 9.0× |
| 16,384 | 81,920 | 20 | 16 | 4 | 6,103.5 | 5.0× |
| 32,768 | 98,304 | 24 | 16 | 8 | 3,051.8 | 3.0× |

This profile intentionally exposes overlap-save overhead. If realtime fails because history dominates, planner should report it; do not silently violate `MaxChunkSize`.

### 7.6. SpectralSliceExtractor

API is internal and span/pointer based.

Inputs:

- full `N` FFT output;
- signed/normalized center bin;
- short length `Lshort`;
- precomputed window;
- block phase scalar;
- destination.

Responsibilities:

- map negative frequencies correctly;
- place bins in correct short-IFFT order;
- handle wrap with two contiguous segments;
- no modulo in per-bin loop;
- SIMD real/complex window kernels;
- optional fused phase scalar;
- no allocations.

Scalar implementation must exist and be tested on tiny N where expected bins can be inspected manually.

### 7.7. FDC block phase continuity

Derive once from absolute frame start:

~~~text
frameStartInputIndex = firstNewSampleIndex - HistorySize
coarseFrequency      = coarseBin * FsIn / N
residualFrequency    = requestedCenter - coarseFrequency
~~~

The exact block scalar depends on spectral extraction indexing and FFT sign. Put derivation in code comment/ADR.

Tests must catch one-sample/frame phase errors:

- bin-centered tone;
- half-bin-ish residual;
- arbitrary initial phase;
- split same logical input with different valid chunk plans;
- compare phase at every boundary to reference DDC.

No phase accumulator may reset on `Process`.

### 7.8. Multiple D groups

One forward FFT shared by all channels.

For each distinct D:

- short length `N/D`;
- one batched FFTW backward plan per batch layout;
- discard `History/D`;
- valid count `Chunk/D`;
- scale remains `1/N` under the chosen convention.

Group channels by short length first. Window identity can be secondary grouping/tuning.

### 7.9. FDC SIMD priorities

Order of optimization:

1. forward FFTW profile;
2. spectral extraction/windowing;
3. short batched IFFT layout;
4. residual rotator;
5. raw input copy only if it is measurable.

Raw copy should first use `Span.CopyTo`/`Buffer.MemoryCopy`. Hand SIMD copy is only justified if fused with conversion/scale and benchmark wins.

### 7.10. FDC acceptance criteria

- one forward FFT per input frame;
- amplitude normalization test proves `1/N` convention;
- arbitrary positive/negative centers;
- wrap across bin zero;
- multiple D groups;
- no phase discontinuity;
- exact output count;
- exact history discard;
- no managed allocations;
- filter and **folded alias** specs pass;
- scalar extraction and SIMD extraction match;
- result matches independent DDC after timing/group-delay alignment;
- `ChunkSize <= MaxChunkSize`.

---

## 8. Generalized/resampling PFB engine

### 8.1. Scope та evolution path

Production architecture uses generic parameters:

~~~text
K = FftSize / number of polyphase branches
H = HopSize / input samples between PFB output frames
P = taps per phase
T = K * P prototype FIR length
FsCoarse = FsIn / H
DeltaF   = FsIn / K
OSR      = K / H
~~~

MVP sequence:

1. scalar critically sampled test (`H=K`) — simplest algebra validation;
2. scalar 2× oversampled (`H=K/2`);
3. SIMD 2× oversampled;
4. generalized integer hop (`1 <= H <= K`);
5. planner chooses non-2× hop;
6. optional multiple PFB groups;
7. optional selected-bin DFT path.

Do not build separate `OversampledPfbEngine` and later `ResamplingPfbEngine`. One engine, one scheduler, configurable H.

### 8.2. Single-bin feasibility constraint

For requested channel bandwidth `B` and nearest coarse bin:

~~~text
DeltaF = FsIn / K
maxResidual = DeltaF / 2
requiredPassEdge >= B/2 + maxResidual
FsCoarse = FsIn / H
NyquistCoarse = FsCoarse / 2
~~~

A single coarse bin is feasible only if the effective PFB channel response provides requested pass/transition/stop behavior before coarse-rate aliasing.

At minimum:

~~~text
requiredPassEdge < NyquistCoarse
~~~

але цього недостатньо — actual folded response must pass evaluator/tests.

If infeasible:

- reduce K;
- reduce H (increase oversampling);
- make another PFB group;
- use adjacent-bin reconstruction in a future extension;
- fall back to FDC;
- reject forced PFB plan with a clear message.

Never silently truncate a channel near a bin boundary.

### 8.3. Hop planning

Do not hardcode `H=K/2`.

Candidate enumeration:

1. choose candidate K values, usually power-of-two for FFTW speed:
   `512,1024,2048,4096,...`;
2. derive a desired coarse rate from widest channel in group plus residual/transition margin;
3. estimate:
   `Htarget ~= FsIn / FsCoarseDesired`;
4. enumerate nearby integer H values:
   - `1 <= H <= K`;
   - prefer H values producing bounded ChunkSize;
   - prefer scheduling/rotation patterns friendly to SIMD;
   - use benchmark profile if available;
5. validate folded filter response;
6. estimate work:
   - FIR frames/s = `FsIn/H`;
   - FFT frames/s = `FsIn/H`;
   - smaller H means more work;
7. choose minimal oversampling that safely meets signal spec rather than automatically 2×.

Intel-style insight to preserve in planner: if only ~20% oversampling is required, aim for roughly ~20% more PFB/FFT frame rate, not 2× by default.

### 8.4. 100 MS/s profile

Old R=2 profile remains useful as baseline:

~~~text
K = 4096
H = 2048
DeltaF = 24,414.0625 Hz
FsCoarse = 48,828.125 Hz
OSR = 2
maxResidual = 12,207.03125 Hz
~~~

For 15 kHz full bandwidth:

~~~text
required nominal pass edge >= 7,500 + 12,207.03125
                           >= 19,707.03125 Hz
~~~

But planner must also benchmark larger H (less oversampling) if prototype/folded response can still satisfy spec, e.g. nearby H values resulting in 1.2×–1.5× oversampling.

Do not assume those examples are valid without filter validation.

### 8.5. Prototype FIR, frame anchors і exact scalar equation

Prototype length padded to multiple of K:

~~~text
T = K * P
h_phase[p,q] = h[p + q*K]
~~~

Frame anchors are defined so each output frame consumes exactly H **new** samples.

For one `Process`:

~~~text
firstNew = firstNewSampleIndex
F        = FramesPerBatch

r_local(m) = firstNew + (m+1)*H - 1
             for m = 0..F-1
~~~

`r_local(0)` is the newest input sample of the first newly produced PFB frame.

Across Process calls:

~~~text
next firstNew = previous firstNew + F*H
~~~

therefore frame anchors remain spaced by exactly H with no duplicate/missing frame.

Full scalar phase vector:

~~~text
for p = 0..K-1:
    u[p] = 0

    for q = 0..P-1:
        l = p + q*K
        u[p] += h[l] * x[r_m - l]
~~~

Index `x[...]` above is an **absolute logical input index**. Implementation maps it into caller span:

~~~text
spanAbsoluteStart = firstNewSampleIndex - HistorySize
spanIndex(abs)    = abs - spanAbsoluteStart
~~~

With `HistorySize=T-1`, all required indices for produced frames are available.

Canonical coarse output centered at bin k:

~~~text
B[k] =
    sum(p=0..K-1)
        u[p] * exp(+j*2*pi*k*p/K)       // FFTW_BACKWARD

yCenter[k] =
    exp(-j*2*pi*k*r_m/K) * B[k]
~~~

Equivalent preferred form:

~~~text
s = Mod(r_m, K)
uShifted[p] = u[(p+s) mod K]
yCenter[k] = FFTW_BACKWARD(uShifted)[k]
~~~

No `1/K` normalization is applied to this PFB transform.

Filter gain convention:

- prototype low-pass DC gain should be normalized to 1 unless a different explicit gain is requested;
- unit-amplitude tone exactly at bin center should produce approximately unit-amplitude baseband output after startup transient;
- this amplitude test catches accidental `/K` or `*K` normalization.

Required scalar tests before SIMD:

- K=4/8 with hand-checkable h/x;
- direct equation vs phase vector + backward DFT;
- explicit `C` vs cyclic-left-shift;
- H=K;
- H=K/2;
- arbitrary H;
- positive and negative signed bins;
- firstNew not aligned to K to prove absolute `r_m` phase handling.

### 8.6. History contract

Safe streaming contract:

~~~text
HistorySize = T - 1
ChunkSize   = FramesPerBatch * H
InputSize   = T - 1 + FramesPerBatch * H
FFTW input  = FramesPerBatch * K filtered ComplexF values
~~~

Raw history exists only so FIR can evaluate the first frame(s) of the new chunk.

No internal duplicate raw-IQ ring is allowed in MVP.

If a future optimized stateful PFB retains FIR state internally to reduce external history, that is a different API/engine contract and must not be introduced silently.

### 8.7. Batched processing

Let `F = FramesPerBatch`.

Preferred memory:

~~~text
fftInput [F][K]  row-major ComplexF
fftOutput[F][K]  row-major ComplexF
~~~

FFTW `plan_many` conceptually (direction = FFTW_BACKWARD for PFB):

~~~text
rank    = 1
n       = K
howmany = F
istride = 1
idist   = K
ostride = 1
odist   = K
~~~

Exact P/Invoke parameters covered by FFTW tests.

Process:

1. validate span;
2. for each frame `m` in process:
   - compute absolute global PFB frame index or equivalent phase origin;
   - compute circular shift schedule;
   - SIMD FIR directly into already-rotated `fftInput[m]`;
3. one batched FFTW_BACKWARD transform for all F frames;
4. gather only requested unique bins;
5. create one coarse stream per unique bin;
6. fan out same coarse stream to multiple channels;
7. per-channel residual rotation/fine decimation;
8. emit outputs.

### 8.8. Absolute frame anchors and reset behavior

Do not maintain PFB correctness using only a local frame parity counter if absolute input position is already available.

For every produced frame calculate/advance:

~~~text
r_m = absolute index of newest input sample used by that frame
s_m = Mod(r_m, K)
~~~

Within one Process:

~~~text
r_0 = firstNewSampleIndex + H - 1
r_m = r_0 + m*H
~~~

Fast scheduler state:

~~~text
s_0 = Mod(r_0, K)
s_next = s + H
if s_next >= K:
    s_next -= K          // only valid when H <= K
~~~

or `& (K-1)` only for K power-of-two. This scheduling is outside FIR inner loops.

On next Process, recomputing `r_0` from new `firstNewSampleIndex` must yield exactly previous last anchor + H.

On `Reset(firstNewSampleIndex)`:

- no stale frame parity;
- first anchor is recomputed by the same formula;
- FIR history is supplied externally according to contract;
- residual NCO phase derives from absolute time.

### 8.9. Mandatory PFB phase/circular correction tests

Reference form:

~~~text
B[k] = FFTW_BACKWARD(u)[k]
C[k] = exp(-j*2*pi*k*r_m/K)
reference[k] = C[k] * B[k]
~~~

Optimized form:

~~~text
s = Mod(r_m,K)
uShifted[p] = u[(p+s) mod K]
optimized[k] = FFTW_BACKWARD(uShifted)[k]
~~~

Require:

~~~text
optimized[k] ~= reference[k]
~~~

for all k on tiny random vectors and multiple r_m.

R=2 relative sanity:

If consecutive frames differ by `H=K/2`, then:

~~~text
C(r_m+H,k) / C(r_m,k) = (-1)^k
~~~

Therefore:

- even k: same correction next frame;
- odd k: sign flips next frame.

Important: absolute first frame does **not** necessarily have correction 1 because `r_0` may not be 0 modulo K. Tests must include nonzero `firstNewSampleIndex`.

### 8.10. SIMD polyphase FIR

Use the phase-parallel kernel from section 5.

Critical requirement: no hot-loop gather caused by naive `h[p+qK]` / `x[n-p-qK]` formulation.

During plan creation prepare:

- phase order mapping;
- coefficient reversal;
- compact/expanded SIMD coefficient layout;
- two destination segments for each possible/recurring shift if caching helps.

For each frame:

~~~text
for each rotated phase segment:
    for p in segment step Vcomplex:
        acc = zero
        for q in 0..P-1:
            xvec = contiguous load of Vcomplex IQ values
            cvec = duplicated real coefficients for those phases
            acc  = FMA/mul-add(xvec, cvec, acc)
        contiguous store to aligned fftInput
~~~

If a convention makes the source block reversed, fix the layout at plan time. Do not add AVX gather as the first solution.

Bounds must be proven from `HistorySize=T-1` and frame scheduling. Unsafe pointer kernel may assume validated bounds.

### 8.11. Selected-bin router

After FFT, do not transpose/copy all K bins unless required.

Precompute unique bins:

~~~text
requested channel -> coarse bin
unique coarse bin -> list of downstream channel refiners
~~~

For F batched frames, gather:

~~~text
coarse[uniqueBin][frame]
~~~

Possible layouts:

- one contiguous buffer per unique bin;
- one packed `[uniqueBin][F]` scratch.

Select based on channel count and cache behavior.

The gather loop is small compared with FFT/FIR but still no allocations or dictionaries in hot path; mappings are arrays built in plan.

### 8.12. Residual correction order

For each channel:

1. receive phase-corrected coarse-bin stream;
2. apply residual NCO for:
   `fResidual = requestedCenter - coarseBinCenter`;
3. low-pass filter to requested bandwidth;
4. fine decimate if needed;
5. emit.

Do **not** use residual NCO to hide missing PFB frame correction. PFB deterministic frame correction happens first at shared-bin level; residual NCO handles actual frequency offset.

Multiple channels sharing one bin share:

- PFB FFT result;
- PFB frame phase correction;
- coarse bin gather.

They may differ in residual, final filter, output rate.

### 8.13. Fine stage policy

Generalized H should reduce unnecessary fine-decimation work but does not eliminate it for heterogeneous channels.

Planner choices:

- one PFB group + fine decimators;
- multiple PFB groups with different H;
- FDC for outlier channels.

MVP: one PFB group + power-of-two fine stage.

Later benchmark group splitting.

### 8.14. Fold-aware prototype optimization

For each candidate `(K,H)`:

1. design Conservative prototype;
2. optionally design FoldAware shorter prototype;
3. run standalone response;
4. run alias-fold evaluator for hop H;
5. run worst-bin-position blocker tests;
6. retain only valid candidates;
7. benchmark FIR+FFT cost.

Do not equate fewer taps with faster end-to-end if higher oversampling/H choice increases frames/s.

Planner objective is total cost:

~~~text
rough work/sec ~
(Fs/H) * (PFB_FIR_cost(K,P) + FFT_cost(K))
+ fine-stage cost
~~~

### 8.15. Selected-bin/direct-DFT PFB future path

For small number `Q` of unique requested coarse bins, full K-point FFT may be unnecessary.

Experimental candidate:

~~~text
after polyphase vector v[p]:
for each selected k:
    X[k] = sum(p=0..K-1) v[p] * W_K^(k*p)
~~~

Naive arithmetic crossover is around `Q ~ log2(K)`, but real crossover must be benchmarked against FFTW.

Implementation requirements if added:

- precompute twiddle vectors per selected bin;
- SIMD complex dot products;
- use same PFB frame correction convention;
- compare output bit/tolerance-wise to full FFT path;
- Auto never selects it before benchmark data.

### 8.16. Recursive polyphase future optimization

Harris notes recursive polyphase structures can reduce workload materially, but recursion limits pipelining and changes numerical/state behavior.

Not MVP.

Only investigate after FIR+FFT PFB is correct and profiled. Separate ADR required.

### 8.17. PFB acceptance criteria

- H=K critical-sampling scalar reference passes;
- H=K/2 oversampled correction passes `(-1)^(m*k)` sanity;
- arbitrary integer H phase-shift equivalence test passes;
- correct center-bin output;
- correct halfway-between-bins output;
- worst residual preserves requested passband;
- no frame/chunk discontinuity;
- changing `FramesPerBatch` does not change logical output;
- history produces no duplicated output;
- FFT input contains exactly filtered vectors, no raw IQ history;
- SIMD FIR matches scalar;
- no managed allocations;
- folded alias response meets requested attenuation;
- one coarse bin fans out to multiple channels correctly;
- result matches independent DDC.

---

## 9. Reference DDC

До оптимізованих engines реалізувати незалежний correctness reference:

1. Complex NCO на input rate.
2. Високоякісний low-pass FIR.
3. Polyphase або прямий decimator.
4. Double-precision accumulator, якщо це допомагає reference accuracy.

Reference DDC:

- не зобов’язаний працювати realtime;
- не використовується в production hot path;
- не повинен ділити критичний код із FDC/PFB, щоб не повторювати ту саму помилку;
- використовується для automated signal comparisons;
- може бути окремим benchmark baseline.

---

## 10. Signal correctness tests

### 10.1. Deterministic generators

Створити reusable signal generator:

- complex tone;
- AM із заданим carrier/audio modulation;
- multicarrier IQ;
- AWGN із deterministic seed;
- strong adjacent blocker;
- impulse;
- frequency sweep/chirp;
- signal із довільною initial phase.

### 10.2. Обов’язкові сценарії

Для FDC і PFB:

1. exact coarse-bin center;
2. halfway between coarse bins;
3. near positive Nyquist;
4. near negative Nyquist;
5. wrap through FFT index zero;
6. two close channels;
7. weak channel beside strong blocker;
8. 20 simultaneous AM carriers;
9. mixed bandwidth/output-rate requests;
10. exact `HistorySize + ChunkSize`;
11. off-by-one input length reject;
12. first call with zero history;
13. repeated history does not duplicate output;
14. missing/repeated firstNewSampleIndex reject;
15. long-run phase drift test;
16. arbitrary initial complex phase;
17. deterministic noise seed;
18. same logical stream split into different valid chunk sizes/plans when mathematically comparable.
19. `IChannelOutputSink.Write` отримує original integer `ChannelId`;
20. exactly one `Write` per active channel per `Process`;
21. callback `samples.Length` exactly equals resolved `OutputSamplesPerProcess`;
22. stable callback order across long run;

FDC-specific:

19. forward FFT input equals full `[history | chunk]`;
20. amplitude normalization for D=1/2/4/... proves selected convention (`1/N`);
21. spectral extraction wrap;
22. scalar vs SIMD extraction;
23. mixed short-IFFT groups;
24. phase continuity across thousands of frames.

PFB-specific:

25. `H=K` critically sampled sanity;
26. `H=K/2`: odd-frame/odd-bin sign rule;
27. direct post-FFT correction equals pre-FFT circular shift;
28. arbitrary H values, including H not dividing K;
29. global frame correction does not reset at Process boundary;
30. same stream with `FramesPerBatch=1` vs 2/4/8 produces equivalent output;
31. PFB FFT input has exactly `F*K` filtered values and no raw history;
32. scalar polyphase vector matches direct FIR decomposition for tiny K;
33. AVX2 FIR matches scalar;
34. AVX-512 FIR matches scalar when supported;
35. worst-case residual at ±DeltaF/2;
36. one coarse bin feeding multiple channels;
37. Conservative vs FoldAware prototypes both meet actual alias spec before performance comparison;
38. non-2× oversampling candidate, e.g. H near `K/1.2` or `K/1.5`, if planner says valid.

Filter-specific:

39. standalone magnitude response;
40. folded/aliased response;
41. blocker sweep across all alias bands;
42. constant-sidelobe stress case showing why folded validation exists.

### 10.3. Метрики correctness

- exact output sample count;
- actual output sample rate;
- timing continuity derived from plan metadata and deterministic output counts;
- frequency error;
- amplitude error;
- phase continuity;
- long-run phase drift;
- RMS error vs independent DDC after timing alignment;
- max absolute complex error;
- passband ripple;
- nominal stopband attenuation;
- **folded alias attenuation**;
- adjacent-channel leakage;
- blocker rejection;
- SNR;
- AM envelope/audio spectrum as sanity check.

Tolerance is configuration-dependent.

SIMD equivalence tolerance should be tighter than end-to-end reference tolerance. Prefer ULP/relative+absolute tolerance appropriate for different summation order rather than requiring bit-identical FMA/non-FMA results.

### 10.4. Golden artifacts

Зберігати machine-readable results у:

~~~text
artifacts/signal-validation/
~~~

Не комітити великі raw IQ файли без потреби. Генерувати deterministic inputs у тесті.

---

## 11. BenchmarkDotNet plan

### 11.1. Загальні правила

- Target framework: net10.0, якщо repository не вимагає іншого.
- Release build.
- Default out-of-process BenchmarkDotNet jobs.
- Окремо benchmark initialization/planning та steady-state execution.
- FFTW plans створюються в GlobalSetup, не всередині measured iteration.
- Всі buffers preallocated.
- Output checksum/consumer запобігає dead-code elimination.
- MemoryDiagnoser для managed allocations.
- Hardware counters, де вони підтримуються OS/hardware.
- DisassemblyDiagnoser лише для managed SIMD primitives.
- Записувати environment summary, CPU model, instruction sets, OS, FFTW build і thread count.
- Не робити висновок за одним коротким benchmark.

Основні метрики:

- ns per input complex sample;
- cycles per input complex sample;
- sustained input MS/s;
- realtime margin для 100 MS/s;
- output samples/s;
- allocations/op;
- working-set bytes;
- cache misses, якщо доступно;
- p50/p95/p99 streaming block latency в окремому integration harness.

### 11.2. FFTW benchmarks

Forward C2C lengths:

- 4096;
- 8192;
- 16,384;
- 32,768;
- 65,536;
- 73,728;
- 81,920;
- 98,304;
- 131,072;
- 262,144;
- 524,288;
- 1,048,576.

Inverse batched lengths:

- 8;
- 16;
- 18;
- 20;
- 24;
- 32;
- 64;
- 128;
- 256.

Batch counts:

- 1;
- 5;
- 10;
- 20;
- 50.

Thread counts:

- 1;
- 2;
- 4;
- доступні physical-core configurations.

Порівняти:

- in-place vs out-of-place;
- FFTW_ESTIMATE vs measured plan лише як cold-start/plan-quality experiment;
- wisdom loaded vs fresh plan;
- different alignment/buffer ownership;
- batched inverse vs цикл окремих inverse calls.

### 11.3. DSP primitive benchmarks

Required benchmark families:

**Complex SIMD**

- real scale AoS;
- complex × complex;
- complex × scalar complex;
- scalar vs AVX2/FMA vs AVX-512;
- aligned vs intentionally unaligned source;
- different vector lengths/tails.

**Residual rotator**

- scalar recurrence;
- AVX2 lane-phasor recurrence;
- AVX-512;
- re-anchor interval sensitivity.

**Half-band decimator**

- TapParallel vs OutputParallel;
- taps/stage variants;
- scalar/AVX2/AVX-512.

**PFB FIR**

- scalar;
- phase-parallel AVX2 compact coefficients;
- phase-parallel AVX2 expanded coefficients;
- AVX-512 compact/expanded;
- taps-per-phase from real planner;
- K = 512..8192;
- pre-FFT rotation fused store vs FIR + rotate-copy baseline;
- no-gather kernel vs gather experiment only as negative/control benchmark;
- optional software prefetch.

**FDC**

- raw frame copy;
- contiguous spectral extraction;
- wrapped two-segment extraction;
- real-window;
- complex-window;
- fused block phase;
- scalar/AVX2/AVX-512.

Record cycles/input complex sample and GB/s where meaningful.

### 11.4. FDC end-to-end benchmarks

Parameters:

- Fs metadata 100 MHz;
- channels 1/5/10/20/50;
- Chunk 8192/16384/32768 and larger only if allowed;
- real filter-derived history;
- D 1024/2048/4096/8192 plus mixed groups;
- smooth non-power-of-two N;
- FFTW threads;
- Scalar DSP vs AVX2 vs AVX-512 forced backends.

Stage timing:

- raw copy;
- forward FFT;
- extraction/window;
- batched backward FFT;
- normalization/discard;
- residual;
- output routing.

### 11.5. PFB end-to-end benchmarks

Parameters:

- K 512/1024/2048/4096/8192 when signal constraints permit;
- H:
  - K critical baseline;
  - K/2 2×;
  - candidates around 0.6K, 0.7K, 0.8K, 0.85K where valid;
  - arbitrary H not dividing K;
- real taps-per-phase from Conservative and FoldAware designer;
- controlled P=4/8/12/16 microprofiles;
- FramesPerBatch 1/2/4/8/16 subject to MaxChunk;
- unique bins 1/5/10/20/50;
- channels sharing bins;
- fine D 1/2/4/8;
- FFTW threads;
- scalar vs AVX2 vs AVX-512 FIR.

Measure:

- FIR direct-to-rotated-FFT-input;
- FFT;
- selected-bin gather;
- residual/fine;
- total.

Crucially compare **equal signal specification**, not equal arbitrary K/H.

### 11.6. Comparative and experimental benchmark

Same `ChannelizerRequest`:

1. independent DDC;
2. FDC;
3. PFB full FFT;
4. selected-bin/direct-DFT PFB experimental when implemented.

Report:

- correctness;
- sustained input MS/s;
- realtime margin @100 MS/s;
- ns/input sample;
- cycles/input sample;
- p50/p95/p99 block latency in integration harness;
- allocations;
- working set;
- FFTW threads;
- SIMD backend;
- K/H/P or N/D parameters.

Auto strategy uses only profiles that passed correctness.

### 11.7. Benchmark output

BenchmarkDotNet artifacts зберігати у стандартному каталозі та генерувати коротке резюме:

~~~text
artifacts/benchmarks/latest-summary.md
~~~

Резюме повинно містити commit hash, environment та raw result links/paths.

---

## 12. Diagnostics та observability

Додати counters без allocations у hot path:

- input samples consumed;
- chunks processed;
- rejected input length/discontinuity count;
- output samples per channel;
- FDC input-copy bytes/time;
- PFB polyphase input samples/time;
- processing time per stage;
- maximum observed processing latency;
- current realtime margin;
- FFTW execution failures;
- channel reconfiguration count.

Tracing/logging не виконувати на кожному sample/frame без sampling або explicit debug mode.

---

## 13. Послідовність реалізації

### Phase 0. Repository inspection + ADR

- locate existing ComplexF/ring/DSP/native components;
- establish target OS/CPU;
- FFTW packaging/licensing;
- write FFT sign, complex layout, timing convention ADR;
- write PFB branch/commutator convention before optimized code.

Done when no unresolved layout/sign ambiguity remains.

### Phase 1. Abstractions and timing

- request/plan types;
- exact input contract;
- plan-only `RationalSampleOffset` timing metadata;
- output sink;
- validation;
- discontinuity/reset semantics.

Done when pure contract tests pass.

### Phase 2. FFTW module

- `fftwf_malloc/free`;
- forward/backward C2C;
- plan_many;
- wisdom/cache;
- smooth lengths;
- threading;
- explicit normalization tests;
- FFTW benchmark baseline.

Done when repeated execute has zero managed allocations.

### Phase 3. Scalar DSP references

Before SIMD:

- filter designer;
- standalone response evaluator;
- alias-fold evaluator;
- scalar residual rotator;
- scalar fine decimator;
- scalar spectral extractor;
- scalar PFB phase-vector FIR;
- scalar circular-shift phase correction.

Done when tiny hand-checkable tests pass.

### Phase 4. Independent reference DDC

- double/high-quality NCO;
- FIR;
- decimation;
- deterministic generators;
- rational timing alignment.

Must not reuse FDC/PFB critical math.

### Phase 5. SIMD foundation

Implement AVX2 first:

- AoS real scale;
- complex multiply;
- scalar-complex multiply;
- residual rotator;
- FIR helper kernels.

Then PFB-specific phase-parallel AVX2.

Then optional AVX-512.

Every kernel:

1. scalar baseline;
2. random-vector equivalence tests;
3. tail/alignment tests;
4. BenchmarkDotNet;
5. only then production wiring.

### Phase 6. FDC MVP

- one D;
- forward FFT;
- scalar extractor first;
- short IFFT;
- explicit `1/N` normalization;
- overlap discard;
- phase correction;
- reference DDC comparison.

Then:

- multiple D;
- SIMD extraction;
- window reuse;
- planner candidates.

Done when amplitude/phase/alias acceptance suite passes.

### Phase 7. PFB algebra MVP — no SIMD assumption

Implement in this order:

1. K small, H=K, scalar;
2. compare to direct FIR+DFT;
3. H=K/2 scalar;
4. implement direct post-FFT `C(m,k)`;
5. implement pre-FFT circular shift;
6. prove equivalence;
7. global frame index across Process;
8. arbitrary H scalar.

Do not start optimized PFB until these tests pass.

### Phase 8. PFB SIMD MVP

- phase-parallel AVX2 FIR;
- direct store into rotated FFTW input;
- batched plan_many;
- selected-bin gather;
- residual/fine stage;
- FramesPerBatch tuning;
- AVX-512 optional.

Done when scalar vs SIMD PFB output matches and no allocations occur.

### Phase 9. PFB generalized planner

- enumerate K;
- enumerate H, not only K/2;
- Conservative filter;
- FoldAware candidates;
- folded response validation;
- choose minimal safe oversampling from benchmark profile;
- one PFB group initially.

Done when at least one non-2× H configuration passes full signal suite.

### Phase 10. Performance tuning

Profile end-to-end, then optimize largest stage only.

Expected high-value targets:

1. FFTW size/thread selection;
2. PFB FIR coefficient/input layout;
3. fused PFB rotation stores;
4. FDC spectral extraction;
5. fine decimator;
6. residual mixer.

No premature manual copy SIMD.

### Phase 11. Selected-bin PFB experiment

Only if full FFT dominates for low unique-bin counts.

- direct selected DFT;
- SIMD twiddle dot products;
- correctness against full FFT;
- find crossover Q.

Keep experimental until proven.

### Phase 12. Unified facade

- `Fdc`/`Pfb`;
- diagnostics;
- plan inspection;
- docs/examples.

### Phase 13. Auto planner

Only measured data.

Heuristic inputs include unique bins, K/H/P, overlap overhead, FFT profiles, SIMD backend and latency constraints.

Resolved plan must explain selection.

---

## 14. Definition of Done

- FDC and generalized PFB available behind one API.
- Public API does not require power-of-two output-rate semantics.
- FDC can still use power-of-two D where optimal.
- PFB internal/public plan exposes K and H separately.
- PFB R=2 phase correction is proven, not implicit.
- At least one arbitrary H (`H != K`, `H != K/2`) passes correctness.
- PFB frame correction uses global absolute stream phase and does not reset each Process.
- Pre-FFT circular shift is mathematically tested against post-FFT correction.
- PFB SIMD FIR writes directly to rotated FFTW input without intermediate K-element rotate copy.
- AVX2/FMA backend exists for hot kernels on x64.
- AVX-512 backend exists where worthwhile or documented benchmark shows no benefit.
- Scalar fallback exists.
- ISA dispatch occurs outside inner loops.
- FDC inverse normalization is explicit and amplitude-tested.
- Filter validation includes aliased/folded response.
- Conservative and FoldAware PFB designs can be compared safely.
- Exact HistorySize/ChunkSize contracts are respected.
- Output hot-path API is only `Write(int channelId, ReadOnlySpan<ComplexF> samples)`.
- Channel IDs are unique application-defined integers and are never remapped.
- Exactly one deterministic-size output block per active channel is emitted per `Process`.
- Output sample rate/timing metadata is read from resolved plan, not repeated in callbacks.
- No raw history is copied into PFB FFT input.
- No duplicated output from history.
- Plan-only rational timing metadata handles fractional group delay without enlarging the hot-path sink contract.
- No steady-state managed allocations.
- No FFTW planning in Process.
- Both engines match independent DDC.
- Benchmark suite includes primitives, FFTW, FDC, PFB and SIMD backends.
- Target 100 MS/s profile has a recorded realtime result on target hardware.
- FFTW licensing/distribution documented.
- README contains a minimal request/process/output example.
- Any Auto decision is backed by stored benchmark profile.

---

## 15. Правила виконання для Codex

1. Спочатку inspect repository; не дублювати existing components.
2. Не змінювати public complex layout без benchmark + migration plan.
3. Зафіксувати FFT sign convention до FDC/PFB phase code.
4. Зафіксувати PFB scalar branch equation до SIMD.
5. Не використовувати residual NCO для маскування PFB frame-phase bug.
6. Для H != K обов’язково реалізувати/перевірити frame correction.
7. R=2 odd-frame/odd-bin sign test є mandatory.
8. Не фізично rotate/copy K-vector у production PFB, якщо rotation можна fuse у store.
9. PFB FIR SIMD спочатку проектувати phase-parallel contiguous-load kernel.
10. Не використовувати gather у головному PFB kernel без benchmark, що доводить перевагу.
11. AVX2/FMA — перший optimized x64 backend.
12. AVX-512 — optional measured backend, не assumption.
13. ISA dispatch one-time; no support checks in inner loops.
14. `unsafe` дозволено лише всередині isolated validated kernels.
15. У hot path: no allocations, LINQ, closures, per-frame collections, strings/log formatting.
16. FFTW planning тільки initialization/reconfiguration.
17. FDC backward normalization не замінювати на generic `1/IfftLength`; використовувати documented FDC convention і amplitude tests.
18. Filter pass/fail визначати не лише standalone response, а folded alias response.
19. FoldAware PFB prototype не приймати без blocker/alias tests.
20. Усі oscillator/frame phases derive from absolute timing.
21. PFB global frame phase не reset on Process boundary.
22. `FramesPerBatch` не повинен змінювати logical output.
23. Power-of-two decimation — implementation policy, не public invariant.
24. Не hardcode 100 MHz / 15 kHz / K=4096 / H=2048 у production.
25. Candidate tables — benchmark profiles only.
26. Після кожної optimization запускати scalar-vs-SIMD tests і end-to-end signal suite.
27. Не заявляти realtime без end-to-end benchmark.
28. Hardware counters використовувати для memory/cache hypotheses.
29. Software prefetch додавати тільки після measurable cache-miss evidence.
30. Не реалізовувати Auto до comparative benchmark data.
31. Не реалізовувати ring buffer/scheduler у channelizer library.
32. PFB FFTW input містить тільки filtered phase vectors.
33. FDC FFTW forward input містить весь `[history | chunk]`.
34. Кожна phase повинна залишати repository compiling/tests green.
35. Якщо mathematics неясна — спочатку додати tiny scalar test, а не «виправляти» order/sign swap'ом.
36. Не повертати `ChannelSampleBlock` або per-callback metadata без окремої API зміни.
37. Hot-path output routing — array/index based; не використовувати dictionary lookup на кожен block.
38. Planner гарантує deterministic `OutputSamplesPerProcess`, щоб simple sink залишався достатнім.

---

## 16. Implementation cheat sheet

Цей розділ навмисно дублює критичні invariants у короткій формі, щоб coding model не загубила їх серед деталей.

### 16.1. FDC

~~~text
Input:
  [History | Chunk]
  N = History + Chunk

Forward:
  FFT_N once

For channel with D:
  L = N / D
  extract L bins
  apply validated anti-alias window
  apply absolute-frame phase correction
  backward FFT_L
  scale by 1/N          <-- critical
  discard History/D
  residual mix
  emit Chunk/D
~~~

Never emit overlap history.

### 16.2. PFB

~~~text
K = phase count / DFT size
H = hop
P = taps/phase
T = K*P
History = T-1
Chunk = FramesPerBatch * H

For local frame m:
  r = firstNewSampleIndex + (m+1)*H - 1

For p = 0..K-1:
  u[p] = sum(q=0..P-1)
             h[p+q*K] * x[r-(p+q*K)]

Reference:
  B = FFTW_BACKWARD(u)
  y[k] = exp(-j*2*pi*k*r/K) * B[k]

Production equivalent:
  s = Mod(r,K)
  uShifted[p] = u[(p+s) mod K]     // cyclic LEFT shift
  y = FFTW_BACKWARD(uShifted)      // NO /K normalization

Gather requested unique bins
Fan out shared bins
Residual mix from fRequested-fCoarse
Fine filter/decimate
Emit
~~~

At `H=K/2`, relative correction between adjacent frames:

~~~text
(-1)^k
~~~

for each k; odd bins flip sign, even bins do not.

Frame anchor `r`, not local Process frame number, is the source of truth.

### 16.3. PFB AVX2 inner-loop intent

Conceptual, not copy-paste code:

~~~text
Vcomplex = 4

for p = phaseStart; p < phaseEnd; p += 4:
    acc[4 complex] = 0

    for q = 0; q < P; q++:
        x = load 4 consecutive complex samples
        c = load 4 real coefficients
        cdup = [c0,c0,c1,c1,c2,c2,c3,c3]
        acc += x * cdup

    store 4 complex outputs directly to corrected FFT destination
~~~

AVX-512 identical concept with `Vcomplex=8`.

If current indexing cannot provide four consecutive samples, reconsider phase numbering/data layout before reaching for gather.

### 16.4. Required benchmark decision points

Do not guess these:

- AVX2 compact vs expanded coefficients;
- AVX-512 vs AVX2;
- TapParallel vs OutputParallel half-band;
- Conservative vs FoldAware prototype;
- K/H combinations;
- FFTW thread count;
- FDC chunk/FFT length;
- full FFT PFB vs selected-bin DFT;
- optional prefetch.

### 16.5. Required debugging order for wrong PFB output

If PFB signal is wrong:

1. disable SIMD;
2. set tiny K=4/8;
3. set H=K;
4. verify branch FIR against direct FIR;
5. verify FFT bin order/sign;
6. enable H=K/2;
7. compare post-FFT correction formula;
8. compare pre-FFT shift;
9. verify global frame parity across Process boundary;
10. only then enable residual NCO;
11. only then enable fine decimator;
12. only then re-enable SIMD.

Do not start by swapping I/Q, reversing bins, conjugating, or changing FFT direction until a scalar test proves which convention is wrong.

---

## 17. Поточний стан і execution checklist для sub-агента

Цей розділ є аудитом repository станом на **2026-08-19**, перевіреним на base commit `998d427` + current AVX-512 changeset. Він не змінює вимоги розділів 1–16. Операційна черга винесена в [`implementation-steps.md`](implementation-steps.md); якщо tracker конфліктує з математичним або API contract цього документа, пріоритет мають розділи 1–16.

### 17.1. Звірка поточної реалізації з планом

Позначення:

- **готово** — наявна реалізація та автоматичні тести для поточного scope;
- **частково** — існує correctness skeleton, але production acceptance criteria фази ще не виконані;
- **не почато** — production implementation відсутня або є лише placeholder;
- **відкладено** — свідомо не виконувати без окремого рішення/даних.

| Область | Статус | Що вже є | Що відсутнє до вимог плану |
| --- | --- | --- | --- |
| Solution structure | готово | Рівно три `.csproj`: library, tests, benchmarks; `net10.0` | Не створювати додаткові projects надалі |
| Phase 0: conventions/ADR | частково | `ComplexF` 8 bytes; FFT sign, PFB correction і absolute phase зафіксовані в ADR; FFTW provenance, Windows x64 matrix і native diagnostics документовані | Release owner ще має явно обрати GPL-compatible distribution, commercial FFTW license або no-bundle policy |
| Phase 1: contracts/timing | готово | Повний resolved metadata, immutable plan snapshots, exact timing/counts, chunk alignment, warnings, numeric/hint validation, reset/discontinuity/fault semantics, stable order/opaque IDs/disposal tests; FDC/PFB timing походить від реальних FIR | In-place reconfiguration навмисно не входить у streaming contract; зміна request виконується через новий engine |
| Phase 2: FFTW | готово | Platform/export/version diagnostics; reusable 64-byte-aligned buffers; cached ref-counted plans; 1D/`plan_many`; in-place/out-of-place; wisdom; smooth lengths; stress, normalization/alignment/no-allocation tests; documented provenance/license policy | Multi-thread runtime і benchmark-selected `MEASURE` default свідомо відкладені до відповідних data/gates |
| Phase 3: scalar DSP | частково | Independent scalar DFT/rotator; normalized-cache Kaiser designer; tap-density-driven FFT response grid і conservative folded-alias evaluators; scalar FIR, power-of-two decimator, standalone spectral extractor; generalized `P > 1` PFB phase FIR, correction/shift references і direct FIR+DFT oracle; tested AVX2 AoS primitives | Production cascaded half-band specialization лишається окремим measured optimization scope |
| Phase 4: reference DDC | готово | Independent `System.Numerics.Complex` DDC з absolute-index NCO, власними double FIR/decimation, exact rational timing alignment, signal metrics і reusable deterministic generators; обидва production engines зіставлені з oracle | Немає для scalar Conservative scope |
| Phase 5: SIMD foundation | готово (AVX2/AVX-512) | Creation-time `Auto/Scalar/Avx2/Avx512` resolution, scalar fallback, actionable unsupported behavior, 64-byte FFTW buffers, tested AoS primitives, FDC extraction і PFB phase-parallel direct-store kernels | residual AVX2 rotator не wired через відсутність переконливого benefit |
| Phase 6: FDC MVP | готово | Реальний multi-D overlap-save: per-channel power-of-two planner, aligned shared history/chunk/N, full `[history|chunk]` FFT once, causal Kaiser anti-alias response з alias budget і folded validation, grouped short backward plans, exact discard, explicit `1/N`, absolute phase, independent-DDC та alias-sweep acceptance | Benchmark-backed planner cost model лишається майбутнім profile-driven scope; forced hint залишається global override |
| Phase 7: PFB algebra MVP | готово | Generic `K/H`; Conservative Kaiser `T=K*P`, `P>1` scalar branch equation; exact FIR group delay; independent double direct FIR+DFT oracle; explicit correction/pre-FFT shift equivalence; `H=K`, `H=K/2`, arbitrary H, signed-bin, non-aligned-origin і partition tests | SIMD kernels свідомо відкладені до окремого permission gate |
| Phase 8: PFB production path | готово (scalar/AVX2/AVX-512) | Filtered phase vectors пишуться прямо у FFTW-owned input; AVX2/AVX-512 vectorize four/eight phases with expanded coefficients and direct two-segment rotated store; batched transform; precomputed unique-bin gather/fan-out; periodic residual rotation; per-channel validated stateful power-of-two fine FIR/decimator; exact counts/delays; Reset/no-allocation/independent-DDC tests | fine-stage specialization лишається measured future scope |
| Phase 9: generalized PFB planner | готово (Conservative) | Deterministic power-of-two `K`, arbitrary integer `H`, bounded frames, geometry/output/chunk feasibility, exact Conservative/fine validation, partial/forced hints, non-2× end-to-end selection | Benchmark-backed ranking і FoldAware лишаються окремими data gates |
| Phase 10: performance tuning | частково | Retained 18-case scalar/AVX2/AVX-512 engine matrix; SIMD kernel comparisons; zero-allocation schema-v2 three-backend stage profile, p50/p95/p99/max, working set і stage breakdown; PFB AVX-512 має measured end-to-end benefit | 100 MS/s realtime не заявлено |
| Phase 11: selected-bin PFB | відкладено | Немає | Починати лише якщо full FFT benchmark показує потребу |
| Phase 12: unified facade | готово (scalar scope) | `ChannelizerFactory` exposes FDC/PFB through one API; immutable plan snapshots, allocation-free diagnostics, complete example і reset/new-engine reconfiguration semantics задокументовані | `Auto` залишається окремим benchmark-profile gate |
| Phase 13: Auto planner | відкладено | `Auto` явно throws `NotSupportedException` | Реалізувати лише після comparative benchmark profiles |
| Signal tests | готово (Conservative scalar/AVX2/AVX-512 scope) | 221 unit/integration tests: independent-DDC checks, FDC/PFB alias images, worst residuals, partition invariance, SIMD random/tail/misalignment/large-origin and end-to-end equivalence, Dfine=1 blocker, Nyquist wrap, sink fault/reset і adversarial response-grid coverage | FoldAware scenarios лишаються за окремим data gate |
| Benchmarks | готово (scalar/AVX2/AVX-512 baseline) | BenchmarkDotNet 0.15.8; retained 18-case engine comparison, SIMD kernel comparisons, CSV/Markdown and schema-v2 working-set/stage profile | target 100 MS/s realtime result відкладений |
| Diagnostics/docs | частково | Allocation-free counters/stage timing, README, facade guide, ADR, acceptance manifest/report, benchmark guide, versioned backend string, FFTW provenance/licensing і explicit no-package release policy | Фінальне FFTW license/distribution рішення належить release owner |

Поточні важливі обмеження, які не можна помилково вважати production behavior:

1. FDC planner використовує deterministic feasibility/shape policy, а не benchmark-profile cost model; наявний scalar baseline ще не є versioned candidate-shape profile для runtime ranking.
2. Representative FDC і generalized PFB plans проходять повні deterministic alias-image sweeps; це acceptance evidence для перевірених shapes, а не доказ для невідомих future planners.
3. PFB використовує generalized deterministic feasibility planner, але його ranking ще не benchmark-backed; план прямо повертає warning і не встановлює `BenchmarkProfileKey`.
4. FDC і PFB `GroupDelayInputSamples` походять від фактичного FIR placement; fractional-delay variants поки не реалізовані.
5. PFB і FDC extraction напряму заповнюють FFTW-owned writable inputs; FFTW outputs усе ще копіюються у managed routing/output buffers за поточним ownership contract.
6. Fine stages є scalar Kaiser implementations; re-profile не виправдав їх пріоритет над PFB polyphase, а cascaded half-band specialization залишається performance work, не correctness blocker.
7. `Auto`, FoldAware і selected-bin paths залишаються вимкненими до accepted comparative data.
8. Decision-grade scalar/AVX2 benchmark і stage profile збережені, але вони configuration-specific; target 100 MS/s realtime performance не заявляється.

### 17.2. Правила роботи для sub-агента

1. Виконувати кроки нижче послідовно; за один інкремент брати один крок або одну явно ізольовану його частину.
2. Перед змінами прочитати відповідні основні розділи 1–16, а не покладатися лише на цей checklist.
3. На початку кожного інкременту зафіксувати baseline: `git status`, поточний commit і `dotnet test IqChannelizer.sln -c Release`.
4. Не змінювати user-owned/unrelated files, зокрема IDE metadata, і не створювати четвертий `.csproj`.
5. Спочатку додавати scalar/reference formula та failing test, потім production wiring.
6. Після кожного інкременту запускати весь Release test suite; не переходити далі з red tests.
7. Дозволи на AVX2 та AVX-512 отримано; обидва paths мають comparative correctness/benchmark evidence. Нові ISA paths не додавати без окремого evidence.
8. Не реалізовувати `Auto`, selected-bin DFT, FoldAware acceptance або performance heuristics без відповідних benchmark/signal data.
9. Не підміняти відсутню PFB frame correction residual oscillator-ом і не змінювати FFT sign/bin order без tiny scalar proof.
10. Кожен завершений крок повинен залишати zero-allocation steady-state tests зеленими або документувати, чому конкретний тест ще не застосовний до initialization-only коду.
11. Після **кожного** інкременту обов’язково актуалізувати [`implementation-steps.md`](implementation-steps.md): статус кроку, evidence, verified commit/working tree, кількість Release tests і наступний рекомендований крок. Не завершувати інкремент із застарілим tracker.

### 17.3. Execution steps і статус

Повний checklist, статус кожного кроку, evidence та обов’язковий update protocol перенесені в [`implementation-steps.md`](implementation-steps.md). Після кожного інкременту цей tracker потрібно актуалізувати до завершення роботи.

### 17.4. Найближчий рекомендований інкремент

Кроки 0–13 та ungated facade/docs частина Кроку 15 перевірені для Conservative scalar/AVX2/AVX-512 scope. Наступного ungated implementation increment немає. **Крок 14** (FoldAware/selected-bin) можна починати лише після окремих correctness/crossover data; channelizer strategy `Auto` залишається за versioned comparative profile gate, а packaging — за managed-only clean-consumer validation.
