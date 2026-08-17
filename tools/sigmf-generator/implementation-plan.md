# План реалізації браузерного SigMF Signal Generator

## 1. Мета

Створити в `tools/sigmf-generator` автономний браузерний застосунок, у якому користувач:

1. задає sample rate, тривалість і, за потреби, RF center frequency;
2. працює з time/frequency canvas: масштабує, прокручує, створює, рухає та змінює розмір блоків сигналів;
3. додає щонайменше complex tone (`sin`) і single-tone FM;
4. бачить точні числові параметри вибраного блока;
5. отримує оцінку кількості семплів і розміру файла до генерації;
6. натискає Download та отримує валідний SigMF Recording із фінальною сумою всіх блоків.

Поза активними блоками IQ дорівнює нулю. У місцях перетину блоків сигнали додаються як комплексні семпли.

## 2. Ключове уточнення моделі

Прямокутник у координатах «час × частота» не визначає IQ-сигнал однозначно. Наприклад, однакову займану смугу можуть мати FM із різними modulating frequency, фазою та deviation. Тому canvas задає часову й частотну геометрію, а тип блока та inspector задають решту DSP-параметрів.

Canvas має два режими відображення:

- **Design overlay** — редаговані блоки, які є source of truth для проєкту;
- **Spectral preview** — необов'язковий coarse STFT, розрахований тим самим DSP-кодом. Він показує реальну спектральну картину, включно з sidelobes, і усуває хибне очікування, що спектр фізично матиме ідеально прямокутні краї.

Для tone блок показується як тонка смуга з мінімальною висотою для hit-testing; його фізична частота — центральна лінія. Для FM висота блока відповідає оціненій occupied bandwidth.

## 3. Архітектурне рішення

### Рекомендація

Почати з HTML/CSS/TypeScript без Rust/WASM:

- Vite як dev/build оболонка;
- vanilla JavascriptScript, UI framework тільки якщо доступний як single file у MVP;
- Canvas 2D для grid, блоків і preview;
- Web Worker для DSP та формування файла, щоб UI не зависав;
- `Float32Array`/`ArrayBuffer` і генерація чанками;

Цей застосунок не потребує C# runtime і не повинен залежати від `src/IqChannelizer`. Водночас він зберігає конвенцію репозиторію: interleaved `float real, float imaginary`.

`npm run build` має створювати звичайний статичний site без backend. Для розробки й надійної роботи ES modules/Worker його слід відкривати через Vite localhost або HTTP(S), а не напряму через `file://`. Якщо обов'язковою вимогою стане буквально один переносний `.html`, після MVP можна додати окремий single-file build і перевірити, що Worker коректно bundle-иться в нього.

## 4. Межі MVP

### Входить у MVP

- один complex IQ channel;
- datatype тільки `cf32_le`;
- sample rate, duration, optional RF center frequency та output basename;
- tone і single-tone FM;
- amplitude у dBFS, initial phase, fade-in/fade-out;
- add/select/move/resize/delete блоків;
- zoom/pan по часу та частоті;
- числовий inspector;
- deterministic generation;
- progress, cancel та повідомлення про validation errors;
- експорт одного `.sigmf` archive;
- SigMF annotations для кожного блока;
- save/load editor project як окремий JSON;
- unit, spectral, metadata та archive tests.

### Не входить у MVP

- імпорт і редагування довільних існуючих SigMF recordings;
- realtime playback через Web Audio;
- серверна генерація;
- multi-channel SigMF;
- довільні modulation plugins;
- noise, AM, digital modulations та imported waveform;
- production-grade spectrogram для всього багатогігабайтного файла;
- інтеграція з поточним channelizer API.

Після MVP природно додати noise, AM, linear chirp, pulsed carrier та BPSK/QPSK як нові реалізації того самого `SignalGenerator` contract.

## 5. UX і поведінка canvas

### Layout

- верхня панель: basename, sample rate, duration, RF center, sample count, estimated bytes;
- ліва панель: Select, Tone, FM, Delete;
- центр: time/frequency canvas;
- права панель: точні властивості selected block;
- нижня панель: zoom controls, status/progress, Download, Cancel, Save Project, Load Project.

### Осі

- X: `[0, actualDurationSeconds]`;
- Y у baseband mode: `[-Fs/2, +Fs/2)` Hz;
- верх canvas — додатні частоти;
- якщо заданий RF center, UI може перемикати labels між baseband offset і absolute RF, але model завжди зберігає baseband offset;
- `actualDuration = totalSamples / sampleRate`.

### Interaction

- drag у режимі Tone/FM створює блок;
- drag усередині блока рухає його;
- handles змінюють початок/кінець і, для FM, lower/upper frequency;
- mouse wheel zoom-ить time axis навколо cursor;
- `Shift + wheel` панорамує час;
- окремий frequency zoom/control або `Alt + wheel` масштабує Y;
- middle-button/Space+drag панорамує viewport;
- Delete/Backspace видаляє selected block;
- Escape скасовує поточну операцію;
- inspector дозволяє точне редагування без залежності від pixel precision.

Усі pointer coordinates спочатку переводяться у world coordinates, потім квантуються в integer sample indices. Canvas не зберігає бізнес-стан.

Початкова вертикальна геометрія має однозначне перетворення в DSP-параметри:

- для Tone береться frequency у центрі drag, а висота після створення стає лише екранним hit target;
- для FM центр drag стає `centerFrequencyHz`, а його висота `B` — початковою Carson bandwidth; початково `modulationFrequencyHz = B / 4` і `deviationHz = B / 4`, тому `2 * (deviation + modulationFrequency) = B`;
- подальший vertical resize FM зберігає modulation frequency, змінює center/deviation і не дозволяє `B < 2 * modulationFrequency`; точні значення завжди можна змінити в inspector.

### Snapping

- час у model зберігається як `startSample` і `sampleCount`, а не floating-point seconds;
- під час drag: `sample = round(timeSeconds * sampleRate)`;
- значення clamp-яться до `[0, totalSamples]`;
- мінімальна довжина блока — один семпл;
- frequency clamp-иться так, щоб фізична occupied band не перетинала Nyquist edges;
- UI може мати optional snap grid у мілісекундах/кілогерцах, але фінальне квантування завжди семплове.

Якщо sample rate або duration змінюються після створення блоків, UI не має мовчки псувати проєкт. Рекомендована політика: показати confirm із вибором «зберегти фізичний час/частоти» або «скасувати»; після перерахунку провалідувати Nyquist та межі duration.

## 6. Model і контракти

```ts
type Project = {
  schemaVersion: 1;
  basename: string;
  sampleRateHz: number;
  totalSamples: number;
  rfCenterHz?: number;
  targetPeakDbfs: number;
  signals: SignalBlock[];
};

type SignalBlock = ToneBlock | FmBlock;

type BaseBlock = {
  id: string;
  startSample: number;
  sampleCount: number;
  centerFrequencyHz: number; // baseband offset
  amplitudeDbfs: number;
  phaseRad: number;
  fadeSamples: number;
};

type ToneBlock = BaseBlock & {
  kind: "tone";
};

type FmBlock = BaseBlock & {
  kind: "fm";
  modulationFrequencyHz: number;
  deviationHz: number;
  modulationPhaseRad: number;
};
```

Derived values не дублюються у persisted project:

- `endSample = startSample + sampleCount`;
- tone frequency bounds: `[centerFrequencyHz, centerFrequencyHz]`;
- FM estimated occupied bandwidth за Carson: `2 * (deviationHz + modulationFrequencyHz)`;
- `totalBytes = totalSamples * 8` для `cf32_le`;
- `durationSeconds = totalSamples / sampleRateHz`.

Кожен generator реалізує один інтерфейс:

```ts
interface SignalGenerator<T extends SignalBlock = SignalBlock> {
  validate(block: T, project: Project): ValidationIssue[];
  addTo(outputIq: Float32Array, firstSample: number, block: T, gain: number): void;
  annotation(block: T, project: Project): SigMfAnnotation;
}
```

`outputIq` має layout `[I0, Q0, I1, Q1, ...]`, сумісний із `ComplexF` у цьому репозиторії.

## 7. DSP semantics

### Complex tone

Для абсолютного sample index `n` і `k = n - startSample`:

```text
phase(n) = phase0 + 2*pi*fc*k/Fs
s(n)     = A*w(n)*(cos(phase(n)) + j*sin(phase(n)))
```

`phase0` означає фазу саме на початку блока. `k` обчислюється з абсолютних model indices і ніколи не походить від початку worker chunk, що відповідає чинній DSP-вимозі репозиторію не скидати phase на process-local межах. Для швидкості реалізація використовує complex rotator/phase accumulator, а не два transcendental calls на кожний семпл. Щоб результат не залежав від export chunk size, oscillator re-anchor boundaries фіксуються відносно `startSample` константою DSP-моделі, наприклад кожні 4096 семплів, а не межами worker chunks.

### Single-tone FM

Для `k = n - startSample`:

```text
beta     = deviationHz / modulationFrequencyHz
phase(n) = phase0
           + 2*pi*fc*k/Fs
           + beta*(sin(2*pi*fm*k/Fs + modulationPhase)
                   - sin(modulationPhase))
s(n)     = A*w(n)*(cos(phase(n)) + j*sin(phase(n)))
```

Віднімання початкового modulation term гарантує, що `phase0` є фактичною комплексною фазою першого семпла блока. `modulationFrequencyHz > 0`, `deviationHz >= 0`. FM rectangle edges у canvas відповідають estimated occupied band, а не жорсткому brick-wall spectrum. Якщо під «FM» потрібен linear chirp, його слід додати окремим `kind: "chirp"`, не змінюючи семантику FM після релізу.

Carson bandwidth є оцінкою: FM має теоретично нескінченні spectral tails. Validation вимагає, щоб Carson bounds були всередині Nyquist interval, і показує warning, якщо guard до Nyquist малий; UI не повинен обіцяти абсолютну відсутність енергії поза прямокутником.

### Envelope

На обох краях застосовується symmetric raised-cosine fade:

- default — `min(round(Fs * 1 ms), floor(sampleCount / 2))`;
- `fadeSamples = 0` дозволяє hard edge;
- envelope обчислюється за event-local sample index і не залежить від chunking.

Це зменшує spectral splatter. Preview та export використовують одну реалізацію envelope.

### Overlap і master gain

Сигнали додаються лінійно. Щоб не створювати несподівані значення вище full scale, перед генерацією sweep по start/end events знаходить максимальну суму linear amplitudes серед одночасно активних блоків.

```text
masterGain = min(1, dbToLinear(targetPeakDbfs) / maxConcurrentAmplitudeSum)
```

Default `targetPeakDbfs = -1`. Це conservative normalization без limiter distortion і без другого проходу по всіх семплах. UI показує застосований master gain. У майбутньому можна додати режим `no normalization`; для MVP одна явна політика зменшує неоднозначність.

### Determinism

Однаковий project JSON повинен давати byte-identical `.sigmf-data` незалежно від:

- worker chunk size;
- viewport/zoom;
- порядку відмальовування;
- швидкості браузера.

Перед mixing `signals` сортуються за стабільним ключем `(startSample, id)`. Arithmetic order у межах семпла фіксований.

## 8. SigMF output contract

### Формат data

- datatype: `cf32_le`;
- кожний complex sample займає 8 bytes;
- порядок: `I0, Q0, I1, Q1, ...`;
- IEEE-754 float32 little-endian;
- без header, delimiter або trailing bytes.

Не можна неявно покладатися на native endianness `Float32Array`. На little-endian browser використовується fast path; endianness перевіряється runtime. Для теоретичного big-endian host writer переходить на `DataView.setFloat32(offset, value, true)`.

### Формат metadata

MVP фіксується на SigMF specification `1.2.6` і генерує:

```json
{
  "global": {
    "core:datatype": "cf32_le",
    "core:sample_rate": 1000000,
    "core:version": "1.2.6",
    "core:recorder": "IqChannelizer SigMF Generator"
  },
  "captures": [
    {
      "core:sample_start": 0
    }
  ],
  "annotations": []
}
```

Якщо заданий RF center, він додається як `core:frequency` до capture. Для кожного блока додається annotation:

- `core:sample_start`;
- `core:sample_count`;
- обидва `core:freq_lower_edge` і `core:freq_upper_edge`;
- `core:label` (`tone` або `fm`);
- `core:generator`;
- короткий human-readable `core:comment` із параметрами.

Якщо RF center відомий, annotation frequency edges записуються як absolute RF; інакше — як baseband offsets. Масив annotations сортується за `core:sample_start`.

Не додавати довільні поля на кшталт `iqgen:deviation` без формально описаного SigMF extension. Повний machine-readable editor state зберігається окремою командою Save Project.

`core:sha512` у MVP опускається: поле optional, а Web Crypto не надає portable incremental digest для великого streaming export. Його можна додати пізніше разом із streaming hash implementation.

### Download container

Основна кнопка Download створює `basename.sigmf` — POSIX tar SigMF Archive, який містить:

```text
basename.sigmf-data
basename.sigmf-meta
```

Це дає один download і відповідає SigMF archive rules. Не використовувати ZIP під розширенням `.sigmf`.

Tar writer має бути невеликим власним модулем із тестами або вузькою browser-compatible dependency. Він знає data size наперед (`totalSamples * 8`), пише valid ustar headers, 512-byte padding і два фінальні zero blocks.

Для простого ustar MVP raw data обмежується максимальним значенням standard octal size field (`8 GiB - 1 byte`), а basename — коротким ASCII-safe значенням. Якщо потрібні більші recordings або довгі Unicode paths, спочатку слід реалізувати й протестувати POSIX PAX extended headers, а не мовчки створювати пошкоджений archive.

Додаткова кнопка Export Pair можлива після MVP. Два автоматичні download у браузері ненадійні через permission/anti-abuse policy.

## 9. Chunking, пам'ять і browser I/O

Worker генерує, наприклад, по 65,536 complex samples:

1. створює zeroed `Float32Array(chunkSamples * 2)`;
2. знаходить блоки, що перетинають chunk;
3. кожен generator додає свій внесок;
4. застосовує master gain;
5. передає `ArrayBuffer` main thread як transferable;
6. main thread дописує chunk у tar/file sink;
7. worker надсилає progress і перевіряє cancel між chunks.

Перед стартом показувати:

- exact sample count;
- raw data bytes і приблизний archive size;
- очікуваний факт, що portable Blob fallback потребує пам'ять, близьку до розміру export.

Передбачити два sinks за одним інтерфейсом:

```ts
interface ByteSink {
  write(chunk: Uint8Array): Promise<void>;
  close(): Promise<void>;
  abort(reason?: unknown): Promise<void>;
}
```

- `FileSystemSink`: streaming у user-selected file там, де доступний `showSaveFilePicker`;
- `BlobSink`: portable fallback із `<a download>`, але з конфігурованим guard, початково 512 MiB.

`showSaveFilePicker` не є єдиним шляхом: API має обмежену browser availability та потребує secure context. На unsupported browser UI автоматично використовує Blob fallback. Для export понад guard показати зрозумілу помилку з порадою скоротити duration/sample rate або використати browser зі streaming file access.

## 10. Запропонована структура файлів

```text
tools/sigmf-generator/
├── implementation-plan.md
├── README.md
├── package.json
├── package-lock.json
├── tsconfig.json
├── vite.config.ts
├── index.html
├── src/
│   ├── main.ts
│   ├── styles.css
│   ├── app/
│   │   ├── controller.ts
│   │   ├── validation.ts
│   │   └── project-io.ts
│   ├── model/
│   │   ├── project.ts
│   │   └── signal-block.ts
│   ├── canvas/
│   │   ├── viewport.ts
│   │   ├── renderer.ts
│   │   ├── hit-test.ts
│   │   └── interactions.ts
│   ├── dsp/
│   │   ├── generator.ts
│   │   ├── tone.ts
│   │   ├── fm.ts
│   │   ├── envelope.ts
│   │   ├── mixer.ts
│   │   └── preview.ts
│   ├── sigmf/
│   │   ├── metadata.ts
│   │   ├── archive.ts
│   │   ├── byte-sink.ts
│   │   └── endian.ts
│   └── worker/
│       ├── protocol.ts
│       └── generate.worker.ts
├── test/
│   ├── unit/
│   ├── integration/
│   └── fixtures/
└── e2e/
```

`src/dsp` не імпортує DOM/canvas, а `src/sigmf` не імпортує UI. Це залишає простий seam для майбутнього WASM backend.

## 11. Послідовність реалізації

### Phase 0 — зафіксувати contracts

- підтвердити `cf32_le`, single channel і `.sigmf` tar як defaults;
- підтвердити, що `FM` означає single-tone FM, а chirp буде окремим типом;
- зафіксувати rounding, Nyquist, overlap normalization та fade rules із цього документа;
- створити приклад project JSON і expected metadata fixture.

**Done:** однаковий input має однозначно визначений sample count, waveform semantics і metadata.

### Phase 1 — scaffold і pure model

- створити Vite/TypeScript app;
- реалізувати Project/SignalBlock types;
- реалізувати validation і derived size/duration;
- додати save/load project JSON із version check;
- написати unit tests для rounding, bounds і migrations/error handling.

**Done:** model працює без canvas і DSP, invalid project не доходить до generator.

### Phase 2 — canvas editor

- viewport/world-screen transforms із devicePixelRatio;
- grid та formatting Hz/kHz/MHz, s/ms/us;
- creation, selection, hit-testing, move і resize;
- zoom/pan anchored at cursor;
- inspector і keyboard actions;
- requestAnimationFrame rendering тільки при dirty state.

**Done:** блок можна створити мишею, точно відредагувати числами й після zoom/pan отримати незмінні model coordinates.

### Phase 3 — DSP backend

- tone, FM, raised-cosine envelope;
- chunk-independent oscillator initialization;
- overlap sweep і master gain;
- chunked mixer;
- coarse spectral preview на обмеженій кількості bins/time slices.

**Done:** DSP unit/spectral tests проходять, а один project дає однакові bytes при різних chunk sizes.

### Phase 4 — SigMF writer і worker

- metadata builder;
- schema validation проти pinned official SigMF 1.2.6 JSON Schema;
- little-endian encoder;
- ustar writer;
- Worker protocol, progress і cancellation;
- FileSystemSink та guarded BlobSink;
- cleanup/abort для partial export.

**Done:** archive відкривається стандартним `tar`, містить правильні filenames, exact data length і schema-valid metadata.

### Phase 5 — інтеграційна перевірка і hardening

- Playwright flow: create two blocks, zoom, edit, download;
- перевірка archive через системний `tar` у test script;
- optional compatibility test через `sigmf-python`;
- responsive layout, keyboard focus і error messages;
- benchmark на короткому/середньому/великому datasets;
- README з browser support, formulas та limitations.

**Done:** acceptance scenarios нижче відтворюються у Chromium і щонайменше одному браузері через Blob fallback.

### Phase 6 — рішення щодо WASM

- записати benchmark results;
- якщо TypeScript backend проходить performance target, WASM не додавати;
- якщо не проходить, реалізувати Rust crate лише для chunk DSP ABI;
- прогнати ті самі byte/spectral fixtures для TS і WASM backends.

**Done:** WASM додається лише з виміряним покращенням і не змінює waveform/output contract.

## 12. Validation rules

- `sampleRateHz` finite, `> 0`, у межах SigMF (`<= 1e12`), а UI задає практичніший configurable maximum;
- `totalSamples` — positive safe integer;
- `totalSamples * 8` — safe integer;
- basename відповідає ASCII-safe policy (рекомендовано `[A-Za-z0-9][A-Za-z0-9.-]{0,63}`), без path traversal;
- block повністю лежить у `[0, totalSamples)`;
- `abs(tone.fc) < Fs/2`;
- FM Carson bounds повністю лежать у `[-Fs/2, Fs/2)`;
- для ustar MVP `totalSamples * 8 <= 8 GiB - 1 byte`;
- amplitude finite і не вище дозволеного UI maximum;
- phase нормалізується, але не квантується;
- fade не довший за половину блока;
- усі metadata numbers finite: JSON не має `NaN`/`Infinity`;
- export заборонено, якщо є хоча б одна error-level issue; warnings показуються до Download.

## 13. Тести

### Unit

- screen/world transforms і cursor-anchored zoom;
- time-to-sample rounding на межах;
- oscillator known samples для DC, `Fs/4`, negative frequency;
- absolute phase при chunk split;
- FM instantaneous-frequency sanity checks;
- envelope endpoints і symmetry;
- overlap/master gain sweep;
- endian bytes для `1.0`, `-1.0`, zero;
- metadata mapping baseband vs RF;
- project serialization/version validation.

### Spectral

- bin-centered tone має peak у правильному positive/negative FFT bin;
- FM center і approximate Carson occupancy відповідають параметрам із tolerance;
- fade зменшує far-out leakage порівняно з hard edge;
- сума двох non-overlapping blocks дорівнює відповідним окремим fixtures;
- overlapping blocks додаються комплексно, без limiter distortion.

### SigMF/archive

- data length строго `totalSamples * 8`;
- metadata проходить pinned schema;
- top-level містить `global`, `captures`, `annotations`;
- annotation sample ranges збігаються з blocks;
- tar filenames мають однаковий basename;
- tar padding/checksum валідні;
- extract + reader отримує complex64 samples у правильному порядку I/Q.

### UI/E2E

- create/move/resize/delete;
- zoom не змінює model;
- invalid Nyquist block підсвічується й блокує export;
- progress монотонний;
- cancel повертає UI у ready state;
- Save Project → reload page → Load Project відновлює той самий model.

## 14. Acceptance scenario для MVP

1. Користувач задає `Fs = 1,000,000`, duration `0.1 s`; UI показує `100,000 samples` і `800,000 bytes` raw data.
2. Додає tone `+100 kHz`, `20–80 ms`, `-6 dBFS`.
3. Додає FM `-150 kHz`, `40–90 ms`, `fm = 5 kHz`, deviation `25 kHz`, `-10 dBFS`.
4. Zoom/pan і числовий inspector показують ті самі sample/frequency bounds.
5. Download не блокує UI та показує progress.
6. `recording.sigmf` розпаковується у `recording.sigmf-data` і `recording.sigmf-meta`.
7. Data має рівно `800,000 bytes`, outside active intervals містить zeros, а overlap є сумою двох waveforms із задокументованим master gain.
8. Metadata має `cf32_le`, `sample_rate = 1,000,000`, version `1.2.6`, один capture і дві sorted annotations.
9. Незалежний reader відкриває Recording і бачить tone/FM на очікуваних частотах.

## 15. Ризики та запобіжники

| Ризик | Запобіжник |
| --- | --- |
| Великий Blob вичерпує RAM | chunked pipeline, size estimate, 512 MiB Blob guard, streaming sink де доступний |
| UI зависає під час DSP | тільки Web Worker, progress/cancel між chunks |
| Canvas обіцяє нереалістичний brick-wall spectrum | design overlay окремо від computed spectral preview |
| Aliasing на Nyquist | central validation для кожного generator |
| Різні bytes при іншому chunk size | absolute phase, event-local envelope, deterministic ordering, fixture tests |
| Невалідний SigMF через custom metadata | лише core fields; editor state — окремий project JSON |
| Два browser downloads блокуються | один стандартний `.sigmf` tar archive |
| Float encoding залежить від host | runtime endian check і DataView fallback |
| Зміна Fs ламає блоки | explicit rescale confirmation і повторна validation |
| Передчасна складність Rust toolchain | WASM тільки після benchmark gate |

## 16. Питання, які не блокують старт

Якщо власник не задасть інше, реалізація використовує такі defaults:

- `FM` = single-tone frequency modulation, не chirp;
- output = `cf32_le`;
- baseband axis із optional RF center;
- default fade = 1 ms;
- conservative normalization до `-1 dBFS`;
- `.sigmf` tar archive як основний download;
- portable Blob cap = 512 MiB;
- empty canvas генерує zero IQ recording;
- усі обчислення локальні, без мережі й backend.

## 17. Джерела формату

- SigMF specification 1.2.6: <https://sigmf.org/>
- Official SigMF JSON Schema: <https://github.com/sigmf/SigMF/blob/main/sigmf-schema.json>
- File System Access API compatibility/fallback context: <https://developer.mozilla.org/en-US/docs/Web/API/Window/showSaveFilePicker>

Під час реалізації schema слід pin-нути до конкретного tag/version, а не завантажувати `main` під час build або runtime.
