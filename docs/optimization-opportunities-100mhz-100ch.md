# Пошук оптимізацій для 100 каналів із потоку 100 MHz

## Висновок

Для цільового навантаження треба розвивати PFB/AVX-512, а не FDC. Найкращий виміряний варіант зараз обробляє 33.73 MS/s, тобто 0.337x від потрібного real-time; необхідне прискорення ще приблизно у 2.97 раза.

Головна гаряча ділянка — polyphase FIR: 84.55% часу PFB/AVX-512. Чинний прототип має 21 tap/phase не через безпосередню вимогу 60 dB, а через додатковий консервативний alias-бюджет 65.44 dB. Окремий response-дослід показав:

- 8 taps/phase не проходять: 54.2 dB звичайного stopband і 38.5 dB folded attenuation;
- 12 taps/phase проходять поточний консервативний критерій із 61.9 dB folded attenuation;
- чинні 21 taps/phase дають 103.5 dB folded attenuation, тобто приблизно 43.5 dB запасу понад вимогу.

Отже, 8 taps/phase не можна просто підставити для поточної форми `K=4096, H=1871`, але 21 справді надлишкові. Перший практичний кандидат — 12 taps/phase із обов'язковою повною перевіркою спектральних вимог і end-to-end blocker sweep.

## Умови вимірювання

Синтетичний профіль відтворює цільову геометрію без дискового I/O:

- input sample rate: 100,000,000 samples/s;
- 100 каналів, центри від -24.75 MHz до +24.75 MHz із кроком 500 kHz;
- passband 20 kHz, transition 20 kHz, stopband 60 dB, ripple 0.2 dB;
- requested chunk 32,768 samples;
- .NET 10.0.11, Windows x64, AMD Family 25 Model 120, 12 logical processors;
- після warm-up вимірювався лише `engine.Process`; managed allocations на вимірюваному потоці — 0 bytes.

Наданий `C:\temp\recording.sigmf` має sample rate 40 MHz за metadata, тому він не є коректним джерелом чисел для цілі 100 MHz. Його не використано як основний performance workload.

## Базові результати

| Strategy | SIMD | Sustained input | Частка від 100 MS/s | Основний stage |
|---|---:|---:|---:|---:|
| FDC | Scalar | 1.140 MS/s | 0.011x | channel extraction 98.8% |
| FDC | AVX2 | 3.032 MS/s | 0.030x | channel extraction 96.64% |
| FDC | AVX-512 | 2.649 MS/s | 0.026x | channel extraction 97.5% |
| PFB Conservative | Scalar | 7.739 MS/s | 0.077x | polyphase FIR 95.5% |
| PFB Conservative | AVX2 | 21.397 MS/s | 0.214x | polyphase FIR 89.70% |
| PFB Conservative | AVX-512 | **33.729 MS/s** | **0.337x** | polyphase FIR 84.55% |
| PFB FoldAware | AVX-512 | 26.044 MS/s | 0.260x | polyphase FIR 86.79% |

Для найкращого PFB-плану:

- `K=4096`, `H=1871`, `FramesPerBatch=16`;
- actual chunk = 29,936 samples;
- prototype length = 86,016 taps = 21 taps/phase;
- estimated working set = 2,176,656 bytes;
- stage split: polyphase 84.55%, FFTW 10.65%, channel post-processing 4.26%, output 0.23%.

FDC/AVX2 приблизно у 11.1 раза повільніший за PFB/AVX-512 і має estimated working set 42,007,040 bytes. Його extraction loop виконує роботу для кожного каналу, partition та alias image, тому це алгоритмічно невдалий шлях для 100 каналів.

## Чому планувальник отримав 21 tap/phase

Для `H=1871` код додає до потрібних 60 dB бюджет:

```text
20 * log10(H - 1) = 65.44 dB
designer target = 60 + 65.44 = 125.44 dB
```

Такий бюджет відповідає сумі модулів усіх 1,870 alias images без урахування їх реального розподілу. Kaiser designer створює фільтр, який після padding до кратності `K=4096` має 86,016 taps, або 21 tap/phase.

Контрольний response study для тієї самої геометрії:

| Candidate | Taps/phase | Passband ripple | Stopband | Worst folded attenuation | 60 dB folded pass |
|---|---:|---:|---:|---:|---:|
| fixed length | 8 | 0.0317 dB | 54.2 dB | 38.5 dB | ні |
| fixed length | **12** | 0.0020 dB | 78.9 dB | **61.9 dB** | **так** |
| fixed length | 16 | 0.00014 dB | 102.4 dB | 78.1 dB | так |
| production magnitude-sum budget | 21 | 0.000007 dB | 126.4 dB | 103.5 dB | так |
| Kaiser без alias budget | 10 | 0.0115 dB | 63.2 dB | 48.8 dB | ні |
| Kaiser з power-sum budget | 15 | 0.00027 dB | 95.7 dB | 73.6 dB | так |

Практичний висновок: замість фіксованої формули запасу варто шукати найкоротший прототип і приймати його лише після наявної dense + folded validation. Для цього workload найкоротший із перевірених кандидатів — 12 taps/phase.

Сам показник taps/phase не можна оптимізувати ізольовано. Наприклад, збільшення `K` може зменшити taps/phase, але збільшити FFT і не зменшити загальну кількість FIR taps. Cost model має враховувати щонайменше `total taps / H`, FFT cost, кількість frames, unique bins і fine-stage cost.

## Пріоритетний список ідей

### P0 — скоротити й спеціалізувати polyphase FIR

1. **Підбирати найкоротший прототип через точну validation.** Почати з 12 taps/phase для цієї форми. Не послаблювати 60 dB requirement; змінити спосіб пошуку запасу, а не критерій приймання.

2. **Використати наявний specialized kernel для 12 taps/phase.** AVX2 та AVX-512 мають спеціалізації лише для 4/8/12/16. Чинні 21 taps/phase потрапляють у generic loop, тому перехід на 12 одночасно скоротить MAC count на 42.9% і прибере generic path.

3. **Якщо 21 taps залишаться для інших shapes — додати 20/21/24 specialization або `16 + tail` kernel.** Це менший за потенціалом варіант, ніж скорочення прототипу, але він потрібний як fallback.

За простим Amdahl estimate, лише пропорційне зменшення polyphase роботи `21 -> 12` піднімає ceiling приблизно з 33.7 до 53 MS/s. Це оцінка, не вимір: спеціалізований kernel може покращити результат, а memory/cache effects — змінити його в обидва боки.

### P0 — додати паралельність у polyphase stage

Навіть оптимістичні ~53 MS/s після скорочення прототипу не закривають 100 MS/s. Потрібна друга велика вісь прискорення:

- розділити незалежні frames або ranges of phases між довгоживучими worker threads;
- уникати `Task`/thread-pool scheduling на кожен chunk;
- зберегти deterministic output ordering і allocation-free hot path;
- виміряти scaling 1/2/4/6 workers, CPU utilization, latency та memory bandwidth.

Це найбільш імовірний спосіб отримати решту приблизно 1.9x після 12-tap prototype на машині з 12 logical processors.

### P1 — cost-based пошук `K/H/FramesPerBatch`

Поточний planner ранжує feasibility, shape distance, oversampling і близькість chunk size, але не включає реальну довжину прототипу чи оцінку обчислень. Для кожного допустимого кандидата варто:

- побудувати та validated prototype;
- оцінити polyphase work як функцію `total taps / H`;
- додати FFT cost `frames * K * log2(K)`;
- врахувати unique-bin gather, residual rotation та fine FIR;
- зберегти кілька найкращих shapes і вибрати переможця versioned benchmark profile.

Цей пошук також покаже, чи може інша геометрія зробити 8 taps/phase валідними без збільшення сумарної роботи. Просто форсувати 8 для поточного `K/H` не можна.

### P1 — зменшити coefficient bandwidth

AVX kernels зберігають expanded coefficients із дублюванням кожного real coefficient для I/Q. Для 21 taps/phase це збільшує coefficient footprint і може тиснути на cache. Варто порівняти:

- compact coefficients + broadcast;
- phase/tap tiling;
- expanded layout для 12-tap specialized kernel;
- hardware counters для cache misses, коли uProf report path стане стабільним.

### P2 — оптимізувати post-processing лише після polyphase

У PFB/AVX-512 весь channel stage займає лише 4.26%, тому ці зміни не можуть самі закрити gap:

- fuse unique-bin gather, residual rotation та factor-1 fine filtering;
- прибрати проміжні `_coarseStreams`/`_rotatedStreams` і копії коротких spans;
- дозволити AVX2 rotator у AVX-512 engine або додати AVX-512 rotator;
- паралелити 100 незалежних channel tails;
- перевірити prototype-only fast path для bin-aligned каналів.

Це корисний другий етап після скорочення і parallelization polyphase stage.

### P2 — профіль і CLI для цільового workload

- Додати versioned strategy profile для 100-channel/100-MHz family, щоб `Auto` обирав PFB/AVX-512 на підтвердженому hardware class.
- Не обирати FoldAware автоматично для цієї форми: він має ту саму довжину 21 taps/phase, але виміряні 26.04 MS/s проти 33.73 MS/s у Conservative.
- Додати benchmark gate: sustained rate, stage split, allocations, response validation і blocker sweep.
- Окремо порівняти `Diagnostics=None` із `StageTiming`, щоб production number не містив timing instrumentation.

## Що не варто робити першочергово

- Не вкладатися в micro-optimization FDC для цього workload: 96.64% його часу вже йде у per-channel extraction, а загальний результат лише 3.03 MS/s.
- Не оптимізувати FFTW першим: у найкращому PFB він займає 10.65%, тоді як polyphase — 84.55%.
- Не форсувати 8 taps/phase без зміни форми або фільтра: поточний response study показує порушення 60 dB.
- Не робити висновок із коротких smoke runs; наведена таблиця використовує warm-up 128/256 і довші серії.

## Статус AMD uProf

AMD uProf 5.3 зміг виконати TBP/PMC collection, але перетворення CLR session у detailed report двічі завершилося всередині `AMDuProfCLI.exe` з access violation за адресою `0x0`. До падіння застосунок завершував workload і записував JSON. Отже, це не evidence падіння channelizer hot path; найбільш вузько локалізована проблема — uProf report/translation path для цієї CLR session. Точна внутрішня причина без справного report не доведена.

Через це в документі не використовуються ненадійні function-level або PMC tables. Основні висновки спираються на повторювані direct benchmarks, внутрішні stage counters та response evaluation. Повторювати `report --detail` для цих sessions не потрібно; raw sessions збережені в `artifacts/uprof/raw/` для перевірки іншою версією uProf.

## Відтворення

Performance profile:

```powershell
dotnet run -c Release --project .\benchmarks\IqChannelizer.Benchmarks\IqChannelizer.Benchmarks.csproj -- --optimization-profile --strategy pfb --simd avx512 --pfb-design conservative --warmup 256 --iterations 5000 --output .\artifacts\uprof\pfb-conservative-avx512.json
```

Prototype response study:

```powershell
dotnet run -c Release --project .\benchmarks\IqChannelizer.Benchmarks\IqChannelizer.Benchmarks.csproj -- --prototype-study --output .\artifacts\uprof\prototype-study.json
```

Збережені результати:

- `artifacts/uprof/fdc-scalar.json`
- `artifacts/uprof/fdc-avx2.json`
- `artifacts/uprof/fdc-avx512.json`
- `artifacts/uprof/pfb-conservative-scalar.json`
- `artifacts/uprof/pfb-conservative-avx2.json`
- `artifacts/uprof/pfb-conservative-avx512.json`
- `artifacts/uprof/pfb-foldaware-avx512.json`
- `artifacts/uprof/prototype-study.json`

## Definition of done для наступної ітерації

Оптимізацію можна вважати придатною до інтеграції, якщо одночасно виконано:

1. dense, folded та end-to-end spectral validation для всіх 100 каналів;
2. blocker sweep не гірший за поточний 60 dB contract;
3. zero managed allocations у steady state;
4. sustained input rate не нижче 100 MS/s на зафіксованому target hardware;
5. збережені deterministic output, streaming continuity, reset/fault semantics;
6. benchmark profile і результати прив'язані до commit та hardware metadata.
