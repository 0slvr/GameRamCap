# GameRamCap Lab — دليل التجارب

> **تنبيه:** الكود مكتوب بدون ما اتجمّع (compile) عندي. أول خطوة عندك: `dotnet build`. إذا طلعت أخطاء، انسخها وارسلها.
> أي رقم أو حقيقة هنا تنقسم: **[Documented]** موثّق من Microsoft، **[Guess]** تخمين/طريقة شائعة غير رسمية، **[Test]** لازم تختبره انت.

## 0) التجهيز (مرة وحدة)
1. افتح PowerShell **كـ Admin** وشغّل `Lab-Scripts\Lab-Setup.ps1` → يسوي Restore Point ويطبع مواصفاتك (RAM/GPU/MMAgent/Pagefile). **ارسل لي ناتجه.**
2. بناء التطبيق: `dotnet build -c Release` (يحتاج .NET 8 SDK، ويجلب package اسمه System.Diagnostics.PerformanceCounter).
3. شغّل `GameRamCap.exe`. ما في كشف ألعاب: أي تطبيق مو بالفوكس يُعامل نفس الشي.
4. كل النتائج تنحفظ في `%APPDATA%\GameRamCap\lab\` (من التراي: Lab experiment ▸ Open lab folder).

## 1) طريقة الاختبار (مهمة جدًا)
- **تغيير واحد كل مرة.** من التراي: `Lab experiment` ▸ اختر التجربة. لما تختار Off ما يشتغل شي.
- نفس السيناريو كل مرة: نفس اللعبة، نفس المكان، 20–30 دقيقة، Discord شغال (صوت/مايك)، Chrome، Spotify.
- الترتيب: **E0 → E1 → E2s → E2h → E5 → E6 → E8**. لا تنتقل للتالية إذا السابقة فشلت (stutter / صوت يتقطع).
- Frame time: استعمل CapFrameX أو PresentMon بنفس الوقت. الصوت: LatencyMon أو سماعك.
- الرجوع للتطبيق (Alt+Tab): الأداة ترجع كل شي **من hook الفوكس مباشرة** (SetWinEventHook) وتسجل الزمن.

## 2) التجارب

| # | التجربة (التراي) | شنو تسوي | الفشل = |
|---|---|---|---|
| E0 | Baseline | **ما تغيّر شي.** تسجل metrics + زمن الرجوع للفوكس (قاعدة المقارنة) | — |
| E1 | Memory Priority | unfocused → priority **1**. عمليات الصوت/المايك → **3** (`LabProtectedPriority`، حط 5 إذا تريدها بدون لمس). فوكس → ترجع للأصل (5) | crackle، مايك، stutter، `protected_hardfaults_s` فوق صفر |
| E2s | Working Set **soft** | child processes الخاملة فقط (CPU < 3%). soft max بدون HARDWS flag **[Documented: soft = مو ملزم لما الرام متوفرة]** | غالبًا ما راح تشوف فرق إلا تحت ضغط رام |
| E2h | Working Set **hard** | نفس الشي بس `QUOTA_LIMITS_HARDWS_MAX_ENABLE` | زيادة `hardfaults_s` أو `page_reads_s`، تقطيع |
| E5 | Thread memory priority | يخفض priority لكل threads العملية غير المحمية عدا اسماء تشبه audio | أي فشل صوت. **السجل يجاوب: هل SetThreadInformation يشتغل على process ثانية؟** |
| E6 | Adaptive | levels 0–4 (priority 3 → priority 1 → trim كل دقيقة → soft WS). كل 10 ثواني: إذا `Page Reads/sec` (متوسط) > `LabPageReadsLimit` (100) أو hard faults لعملية صوت > `LabAudioHardFaultLimit` (1/ث) → يخفف مستوى ويحفظ الحد الآمن في `lab\safe.json`. بعد 60 ثانية هادية يشدد خطوة | عدّل الحدود بعد ما تشوف أرقام E0 |
| E8 | Suspend + trim **(RISKY)** | فقط تطبيقات من `Targets`، **صامتة تمامًا** (لا جلسة صوت حتى لو paused، لا مايك/كاميرا/capture، نافذتها مو ظاهرة)، بعد `max(delay,120s)`. ترجع (resume) عند الفوكس أو الخروج، وملف `suspended.txt` + watchdog يرجّعها حتى لو انقفل التطبيق | تنقطع اتصالات الشبكة (Discord ما يستلم رسائل بالخلفية) — متوقع |

اختيار **Delay** (5 / 30 / 120 ثانية) و**Visible mode** (Background / Foreground) من نفس القائمة. جرّب كل واحد بجولة وقارن `episodes.csv`.

### E3 — إعدادات داخل التطبيقات (يدوي، بدون كود)
- Chrome/Edge: Memory Saver / Sleeping tabs **[Documented]**.
- Electron (Discord…): `--js-flags="--max-old-space-size=256"` يحدد JS heap فقط **[Documented]**، ما يمس بقية الذاكرة.
- .NET apps: متغير `DOTNET_GCConserveMemory` (1–9) و`DOTNET_GCHeapHardLimit` **[Documented]**. Java: `-Xmx`.
- القياس: `apps.csv` عمود `ws_mb` و`private_mb` قبل/بعد، ومعاه `hardfaults_s`.

### E4 — VRAM (يدوي)
- **[Documented]** مافي طريقة معروفة تقلّص VRAM لتطبيق ثاني من برا مثل working set.
- جرّب: Settings داخل Discord/Chrome/Steam ▸ **Hardware Acceleration = Off** (يحتاج restart للتطبيق).
- iGPU routing: `Lab-Scripts\Lab-IGPU.ps1 -Show` ثم `-Exe <مسار> -Pref PowerSaving` **[Guess: مفتاح الريجستري هو نفسه اللي تكتبه واجهة Settings، بس مو API رسمي]**. لازم يكون عندك iGPU فعلًا.
- القياس: `apps.csv` عمود `gpu_ded_mb` (نفس counters حقت Task Manager: GPU Process Memory) و`system.csv` عمود `gpu_adapter_ded_mb`.

### E7 — MMAgent
`Lab-Scripts\Lab-MMAgent.ps1 -Action DisableCompression` (Admin) → **Reboot** → جلسة E0 → قارن. بعدين `EnableCompression`. غيّر شي واحد بكل مرة (compression أو page combining). النتائج بالمجتمع متضاربة **[Guess]**.

### E9 — ما سويته (عن قصد)
- **DLL injection**: ما أكتبه (تقنية malware، وتسبب banning مع anti-cheat).
- **Memory Partitions (NtCreatePartition)**: غير موثق، يحتاج SeLockMemory، ونتيجته غير مضمونة. إذا تبي نبحث عنه بعد ما تخلص الباقي نناقشه.

## 3) الملفات
- **system.csv** (كل ثانية): `avail_mb`, `modified_mb`, `standby_mb`, `commit_mb` vs `commit_limit_mb` (`commit_pct`), `pagefile_pct`, **`page_reads_s`**, **`pages_input_s`** (hard faults حقيقية)، `gpu_adapter_ded_mb`. *(Page Faults/sec ما نستخدمه عن قصد.)*
- **apps.csv** (كل 5 ثواني لكل تطبيق): `ws_mb`, `private_mb`, **`hardfaults_s`** (حقيقية، من NtQuerySystemInformation HardFaultCount)، **`protected_hardfaults_s`** (هدفها صفر)، `audio_active_pids`, `capture_in_use`, `focused`, `visible`, `reduced`, `level`, `gpu_ded_mb`.
- **episodes.csv** (حلقة واحدة = من لحظة التخفيض لحد الرجوع): `unfocused_s`, `ws_before_mb`, `ws_after_mb`, `freed_mb`, `avail_delta_mb`, `focus_to_restore_ms`, `restore_ms`, **`ui_responsive_ms`**, `hardfaults_3s`.
- **log.txt**: أحداث (من احتل الصوت، mismatch بين WASAPI و ConsentStore، نتيجة E5…).

**تحذير قراءة:** `ws_mb` لوحده مو مقياس نجاح (التقليم ينقل الصفحات لـ Standby/Modified ويرجعون). `freed_mb`/`avail_delta_mb` فيهم ضجيج (اللعبة نفسها تغير الرام). الحكم النهائي = stutter + صوت + `page_reads_s`/`hardfaults_s`.
`ui_responsive_ms` يقيس متى حلقة رسائل التطبيق ترد (WM_NULL)، **مو** سلاسة الرسم؛ استعمله مع frame time.

## 4) أسئلتك المفتوحة — وين تلقى الجواب
| السؤال | الجواب |
|---|---|
| SetThreadInformation على threads عملية ثانية؟ | E5: سطر `LAB E5:` بالـ log (OK / failed / Win32 error) |
| Discord renderer priority يفيد اللعبة؟ | E1 مقابل E0: `page_reads_s`, `avail_mb`, frame time |
| WASAPI يغطي المايك و child processes؟ | سطور `LAB audio:` (mic/playback + parent) و`LAB MISMATCH` و`LAB capture` |
| Hardware Acceleration / iGPU يحرر VRAM؟ | E4: `gpu_ded_mb` قبل/بعد |
| زمن الرجوع، وأي delay أنسب؟ | `episodes.csv`: `ui_responsive_ms`, `hardfaults_3s` لكل delay (5/30/120) |

## 5) حدود وتنبيهات
- حماية الصوت: عملية تملك stream فعّال (render **أو** mic على **كل** الأجهزة النشطة، مو بس الافتراضي) + أجدادها بنفس الاسم + التنبيهات الجديدة (IAudioSessionNotification، 15 ثانية) + فترة سماح. المايك/الكاميرا/screen capture بالـ ConsentStore تحمي **التطبيق كله**. هذا الجزء **[Guess/غير رسمي]**.
- "ظاهر لكن مو بالفوكس" = نافذة مرئية، مو مصغّرة، مو cloaked، مو بالكامل تحت نافذة الفوكس (تخمين لتغطية اللعبة).
- لا يفتح handle للعبة أبدًا: قائمة العمليات من NtQuerySystemInformation (بدون handles)، والاستثناء الوحيد: قائمة Protected. إذا شغّلت "Test ALL apps" ولعبتك بالخلفية رح تنعامل مثل أي تطبيق، فخلّ الاختبار على قائمة التطبيقات. مع هذا، **اختبر مع anti-cheat لعبتك على مسؤوليتك**.
- التراكيب (offsets) الخاصة بـ SYSTEM_PROCESS_INFORMATION لـ x64 فقط **[Test]**: إذا `hardfaults_s` كلها صفر دائمًا أو الأسماء فاضية، قل لي.
