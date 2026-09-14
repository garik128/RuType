using System.Text;
using RuType.Config;
using RuType.Interop;

namespace RuType.Core;

/// <summary>
/// Headless-проверка логики ядра без хука и инъекции ввода: грузит реальный
/// словарь и прогоняет образцы слов через Analyzer. Запуск: RuType.exe --selftest.
/// Результат пишется в файл и (если есть консоль) в stdout.
/// </summary>
public static class SelfTest
{
    public static int Run()
    {
        var sb = new StringBuilder();
        var store = new ConfigStore();
        var cfg = store.Load();
        store.EnsureUserLists();

        var dict = new Dictionaries();
        string ruDic = store.ResolveAppPath(cfg.Dictionaries.RuHunspell);
        bool ruOk = dict.LoadRuHunspell(ruDic);
        bool enOk = dict.LoadEnHunspell(store.ResolveAppPath(cfg.Dictionaries.EnHunspell));
        bool freqOk = dict.LoadFreq(store.ResolveAppPath(cfg.Dictionaries.RuFreq));
        bool extraOk = dict.LoadExtra(store.ResolveAppPath(cfg.Dictionaries.RuExtra));
        bool extraDicOk = dict.LoadRuExtraHunspell(
            store.ResolveAppPath(cfg.Dictionaries.RuExtraDic), System.IO.Path.ChangeExtension(ruDic, ".aff"));
        bool enExtraOk = dict.LoadEnExtra(store.ResolveAppPath(cfg.Dictionaries.EnExtra));
        dict.LoadUserLists(store.MyWordsPath, store.StopWordsPath, store.RulesPath);
        if (cfg.Ngram.Enabled && ruOk)
            dict.SetNgram(NgramModel.BuildOrLoad(ruDic, store.ResolveAppPath(cfg.Dictionaries.RuFreq),
                System.IO.Path.Combine(store.DataDir, cfg.Ngram.CacheFile), cfg.Ngram.WeightByFrequency));

        sb.AppendLine($"ru Hunspell: {(ruOk ? "загружен" : "НЕ ЗАГРУЖЕН")} ({ruDic})");
        sb.AppendLine($"en Hunspell: {(enOk ? "загружен" : "НЕ ЗАГРУЖЕН")}");
        sb.AppendLine($"частоты: {(freqOk ? $"загружены ({dict.FreqCount})" : "НЕ ЗАГРУЖЕНЫ")}");
        sb.AppendLine($"ru_extra: {(extraOk ? $"загружен ({dict.ExtraWordsCount})" : "нет")}");
        sb.AppendLine($"ru_extra.dic (аффиксный): {(extraDicOk ? "загружен" : "нет")}");
        sb.AppendLine($"en_extra: {(enExtraOk ? $"загружен ({dict.EnExtraWordsCount})" : "нет")}");
        sb.AppendLine($"n-gram: {(dict.NgramLoaded ? "загружена" : "нет")}");
        sb.AppendLine($"my_words: {dict.MyWordsCount}, stopwords: {dict.StopWordsCount}, rules: {dict.RulesCount}");
        sb.AppendLine($"max_edit_distance={cfg.Typo.MaxEditDistance}, min_word_length={cfg.Typo.MinWordLength}, " +
                      $"dominance_ratio={cfg.Typo.DominanceRatio}, min_frequency={cfg.Typo.MinFrequency}");
        sb.AppendLine(new string('-', 60));

        var analyzer = new Analyzer(dict, cfg) { LayoutDetectionEnabled = enOk };

        // Опечатки, которые должны исправиться.
        string[] typos = { "привт", "докумен", "ошбка", "сегдня", "сообщене", "превед", "касается" };
        sb.AppendLine("ОПЕЧАТКИ (ожидаем исправление):");
        foreach (var w in typos)
            sb.AppendLine($"  {w,-14} -> {Describe(analyzer.Analyze(w))}");

        // Корректные слова (в т.ч. редкие формы) - не трогать.
        string[] valid = { "кошка", "документ", "сегодня", "привет", "программа", "работает", "вкладки", "вкладок", "ноутбук" };
        sb.AppendLine("КОРРЕКТНЫЕ (ожидаем без изменений):");
        foreach (var w in valid)
            sb.AppendLine($"  {w,-14} -> {Describe(analyzer.Analyze(w))}");

        // Англицизмы/современная лексика из ru_extra - НЕ трогать (симптом "ложно исправляет").
        sb.AppendLine("АНГЛИЦИЗМЫ (ожидаем без изменений, если ru_extra загружен):");
        foreach (var w in new[] { "десктоп", "лайфхак", "фронтенд", "бэкенд", "деплой", "гаджет" })
            sb.AppendLine($"  {w,-14} -> {Describe(analyzer.Analyze(w))}");

        // Опечатки в англицизмах - ожидаем исправление через кандидатов из ru_extra.
        sb.AppendLine("ОПЕЧАТКИ В АНГЛИЦИЗМАХ (ожидаем исправление):");
        foreach (var w in new[] { "десктп", "лайфак", "гаджт" })
            sb.AppendLine($"  {w,-14} -> {Describe(analyzer.Analyze(w))}");

        // Регистр.
        sb.AppendLine("РЕГИСТР:");
        foreach (var w in new[] { "Привт", "ПРИВТ" })
            sb.AppendLine($"  {w,-14} -> {Describe(analyzer.Analyze(w))}");

        // Раскладка (ожидаем переключение + перенабор).
        sb.AppendLine("РАСКЛАДКА (ожидаем смену раскладки):");
        foreach (var w in new[] { "ghbdtn", "руддщ", "ьфылф", "Ghbdtn" })
            sb.AppendLine($"  {w,-14} -> {Describe(analyzer.Analyze(w))}");
        sb.AppendLine("РАСКЛАДКА - НЕ трогать настоящие слова:");
        foreach (var w in new[] { "hello", "world", "привет", "tcp", "url", "gif", "vps", "dir", "ctrl", "xhttp", "уты" })
            sb.AppendLine($"  {w,-14} -> {Describe(analyzer.Analyze(w))}");

        // Раскладка по "сырому" прогону со знаком-буквой (',' -> 'б'): ',fylbn' -> 'бандит'.
        // Проверяем IsLayoutSwap так же, как его вызывает InputProcessor на границе:
        // первый случай - валидное слово (смена), второй - мусор (без изменений).
        sb.AppendLine("РАСКЛАДКА со знаком-буквой:");
        foreach (var (cur, tgt) in new[] { (",fylbn", "бандит"), (",rjrj", "бкоко") })
            sb.AppendLine($"  {cur,-14} -> {(analyzer.IsLayoutSwap(cur, tgt, LayoutTarget.Ru) ? $"смена => '{tgt}'" : "(без изменений)")}");

        // Правила (если заданы в rules.txt).
        sb.AppendLine("ПРАВИЛА (зависит от rules.txt):");
        foreach (var w in new[] { "тлф", "кантора" })
            sb.AppendLine($"  {w,-14} -> {Describe(analyzer.Analyze(w))}");

        // Сценарии InputProcessor: полный путь границ/сегментов на фейковой раскладке.
        // Ловят класс граблей "знак-буква" ('продолжить' = 'ghjljk;bnm' -> раньше
        // 'продал;ить'), который Analyzer-фикстуры не видят. Дефолтный конфиг, чтобы
        // ожидания не зависели от пользовательского config.json.
        int scenFail = 0;
        if (ruOk && enOk)
        {
            sb.AppendLine("СЦЕНАРИИ ВВОДА (клавиши по EN-позициям -> итоговый текст):");
            var scenAnalyzer = new Analyzer(dict, new AppConfig()) { LayoutDetectionEnabled = true };
            foreach (var (keys, expect, startRu) in new (string, string, bool)[]
            {
                ("ghbdtn ",      "привет ",      false), // детект раскладки на пробеле
                ("ghjljk;bnm ",  "продолжить ",  false), // знак-буква ';'='ж' в середине
                ("ufhf; ",       "гараж ",       false), // знак-буква в конце
                (",fylbn ",      "бандит ",      false), // знак-буква в начале (регресс)
                ("gjt[fkb ",     "поехали ",     false), // знак-буква '['='х' (регресс)
                ("ghjljk;bnm? ", "продолжить, ", false), // хвостовой знак по целевому рендеру ('?'='RU запятая')
                ("ghbdtn. ",     "привет. ",     false), // хвостовая точка ('.'='ю', но задумана точкой)
                ("hello ",       "hello ",       false), // keep: настоящее английское
                ("ghbdtn5 ",     "ghbdtn5 ",     false), // keep: цифра -> технический токен
                ("ghbdtn ",      "привет ",      true),  // keep: обычный русский набор
                ("ghbdtn& ",     "привет? ",     true),  // keep: русский со знаком '?' (Shift+7)
                ("hello ",       "hello ",       true),  // 'руддщ' -> детект на пробеле
                ("rfrjq-nj ",    "какой-то ",    false), // дефис внутри слова: чинить обе части
                ("ghbdtn,rfrjq ", "привет,какой ", false), // знак МЕЖДУ словами: режем прогон
                ("test,data ",   "test,data ",   false), // keep: настоящий английский со знаком
                ("ghbdtn?rfrjq ", "привет,какой ", true), // keep: русский набор со знаком не трогаем
                ("ghbdtn/rfrjq ", "привет.какой ", false), // знак-разделитель, в цели тоже знак
                (",'rfg ",       "бэкап ",       false), // знак-буква '\''='э' (слово из ru_extra.dic)
                ("ghbdtn/ ",     "привет. ",     false), // клавиша '/' - точка в RU
                ("(ghbdtn) ",    "(привет) ",    false), // слово в скобках
            })
            {
                var kb = new FakeKeyboard(scenAnalyzer);
                if (startRu) kb.Current = FakeKeyboard.Ru;
                kb.Type(keys);
                bool ok = kb.Screen == expect;
                if (!ok) scenFail++;
                sb.AppendLine($"  {(ok ? "OK  " : "FAIL")} '{keys}' -> '{kb.Screen}'{(ok ? "" : $" (ожидалось '{expect}')")}");
            }

            // Alt+Tab с невалидным словом в буфере: Tab при зажатом Alt - команда, а не
            // граница. Раньше ToUnicodeEx отдавал табуляцию, слово уходило в Analyze, а
            // правка (и подавление клавиши) улетали в переключение окон.
            {
                var kb = new FakeKeyboard(scenAnalyzer);
                kb.Type("ghbdtn");
                kb.Chord = true; kb.Type("	"); kb.Chord = false;
                kb.Type(" ");
                bool ok = kb.Screen == "ghbdtn	 ";
                if (!ok) scenFail++;
                sb.AppendLine($"  {(ok ? "OK  " : "FAIL")} Alt+Tab не граница -> '{kb.Screen.Replace("	", "<tab>")}'{(ok ? "" : " (ожидалось 'ghbdtn<tab> ')")}");
            }

            // Смена активного окна без клика мышью (Alt+Tab, Win+N): force по слову из
            // прежнего окна не должен впечатываться в новое по Pause.
            {
                var kb = new FakeKeyboard(scenAnalyzer);
                kb.Type("hello ");            // валидное английское - force взведён
                kb.Window = (IntPtr)2;        // другое окно
                kb.Press(0x13, false);        // Pause
                bool ok = kb.Screen == "hello ";
                if (!ok) scenFail++;
                sb.AppendLine($"  {(ok ? "OK  " : "FAIL")} Pause после смены окна -> '{kb.Screen}'{(ok ? "" : " (ожидалось 'hello ')")}");
            }

            // Поля пароля в браузере (быстрый Win32-детект их не видит, только UIA).
            sb.AppendLine("СЦЕНАРИИ ПОЛЯ ПАРОЛЯ (фейковый UIA):");
            void Pw(string name, bool ok, string screen, string expect)
            {
                if (!ok) scenFail++;
                sb.AppendLine($"  {(ok ? "OK  " : "FAIL")} {name} -> '{Visible(screen)}'{(ok ? "" : $" (ожидалось '{Visible(expect)}')")}");
            }

            // Логин -> Tab -> пароль быстрее TTL кеша. hwnd фокуса у браузера один на все
            // поля, события фокуса нет: раньше закешированное на Tab "не пароль" жило в
            // поле пароля, и 'ghbdtn' перекладывался в 'привет' (и попадал в лог правок).
            {
                var fx = new FakeExclusions();
                var kb = new FakeKeyboard(scenAnalyzer, fx.Manager);
                fx.Window = () => kb.Window;
                kb.Type("hello\t");
                fx.Password = true;               // фокус ушёл в поле пароля
                kb.Type("ghbdtn\r");
                Pw("логин, Tab, пароль быстрее TTL", kb.Screen == "hello\tghbdtn\r", kb.Screen, "hello\tghbdtn\r");
            }

            // Событие фокуса UIA: пароль отсекается уже на клавишах, прямой запрос не нужен.
            {
                var fx = new FakeExclusions();
                var kb = new FakeKeyboard(scenAnalyzer, fx.Manager);
                fx.Window = () => kb.Window;
                fx.Manager.ReportFocusChanged(kb.Window, isPassword: true);
                kb.Type("ghbdtn ");
                Pw("событие фокуса: пароль", kb.Screen == "ghbdtn " && fx.Manager.UiaQueryCount == 0, kb.Screen, "ghbdtn ");
            }

            // Pause в браузерном поле пароля не перекладывает набранное.
            {
                var fx = new FakeExclusions { Password = true };
                var kb = new FakeKeyboard(scenAnalyzer, fx.Manager);
                fx.Window = () => kb.Window;
                kb.Type("ghbdtn");
                kb.Press(0x13, false);
                Pw("Pause в поле пароля", kb.Screen == "ghbdtn", kb.Screen, "ghbdtn");
            }

            // Кеш по-прежнему экономит запросы в пределах одного поля, а TTL его обновляет.
            {
                var fx = new FakeExclusions();
                var kb = new FakeKeyboard(scenAnalyzer, fx.Manager);
                fx.Window = () => kb.Window;
                kb.Type("hello world ghbdtn ");
                int inField = fx.Manager.UiaQueryCount;
                fx.Now += 2000;
                kb.Type("test ");
                bool ok = inField == 1 && fx.Manager.UiaQueryCount == 2 && kb.Screen == "hello world привет test ";
                Pw($"кеш: запросов в поле {inField}, после TTL {fx.Manager.UiaQueryCount}", ok, kb.Screen, "hello world привет test ");
            }

            sb.AppendLine(scenFail == 0 ? "  все сценарии пройдены" : $"  ПРОВАЛЕНО СЦЕНАРИЕВ: {scenFail}");
        }

        int infraFail = RunInfraChecks(sb);

        string report = sb.ToString();
        string outPath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "rutype_selftest.txt");
        System.IO.File.WriteAllText(outPath, report);
        Console.WriteLine(report);
        Console.WriteLine($"[report -> {outPath}]");
        return !ruOk ? 2 : scenFail > 0 ? 3 : infraFail > 0 ? 4 : 0;
    }

    private static string Visible(string s) => s.Replace("\t", "<tab>").Replace("\r", "<enter>");

    /// <summary>ExclusionManager на фейковых швах: окно, UIA-ответ и часы задаются тестом.</summary>
    private sealed class FakeExclusions
    {
        public bool Password;
        public long Now = 1_000_000;
        public Func<IntPtr> Window = () => (IntPtr)1;
        public readonly ExclusionManager Manager = new();

        public FakeExclusions()
        {
            Manager.ForegroundWindow = () => Window();
            Manager.FocusedControl = () => (IntPtr)10;   // браузер: один hwnd на все поля
            Manager.NativePasswordCheck = _ => false;     // Win32-детект браузерный пароль не видит
            Manager.UiaFocusedIsPassword = () => Password;
            Manager.IsOwnProcessWindow = _ => false;
            Manager.Clock = () => Now;
        }
    }

    /// <summary>Проверки инфраструктуры: хранилище конфига, нормализация, уровень целостности.</summary>
    private static int RunInfraChecks(StringBuilder sb)
    {
        int fail = 0;
        void Check(string name, bool ok)
        {
            if (!ok) fail++;
            sb.AppendLine($"  {(ok ? "OK  " : "FAIL")} {name}");
        }

        sb.AppendLine("ИНФРАСТРУКТУРА:");
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"rutype_selftest_{Environment.ProcessId}");
        try
        {
            System.IO.Directory.CreateDirectory(dir);
            var store = new ConfigStore(dir);

            // Битый config.json: дефолты, копия .broken, сам файл не перезаписан.
            const string broken = "{ \"typo\": { \"min_word_length\": 3,";
            System.IO.File.WriteAllText(store.ConfigPath, broken);
            var cfg = store.Load();
            var backups = System.IO.Directory.GetFiles(dir, "config.json.broken-*");
            Check("битый конфиг: предупреждение + копия .broken + исходник цел",
                store.LoadProblem != null && backups.Length == 1
                && System.IO.File.ReadAllText(backups[0]) == broken
                && System.IO.File.ReadAllText(store.ConfigPath) == broken
                && cfg.Typo.MinWordLength == new AppConfig().Typo.MinWordLength);

            // null-секции и значения вне диапазона приводятся к допустимым.
            System.IO.File.WriteAllText(store.ConfigPath,
                "{\"typo\": null, \"layout\": {\"min_word_length\": -5, \"hotkey_undo_vk\": 999}, \"tray\": {\"blink_ms\": 999999}, \"exclusions\": {\"app_blacklist\": null}}");
            cfg = store.Load();
            Check("нормализация: null-секции и диапазоны",
                store.LoadProblem == null && cfg.Typo != null && cfg.Layout.MinWordLength == 1
                && cfg.Layout.HotkeyUndoVk == 255 && cfg.Tray.BlinkMs == 10000 && cfg.Exclusions.AppBlacklist != null);

            // Нормализация не трогает корректные значения (дефолты проходят как есть).
            var defaults = new AppConfig();
            string before = System.Text.Json.JsonSerializer.Serialize(defaults);
            defaults.Normalize();
            Check("нормализация не меняет корректный конфиг", before == System.Text.Json.JsonSerializer.Serialize(defaults));

            // Атомарная запись: временный файл не остаётся, перечитывается то же.
            cfg.Suggestions.Threshold = 7;
            store.Save(cfg);
            var reloaded = store.Load();
            Check("атомарная запись: без .tmp, значения сохранены",
                !System.IO.File.Exists(store.ConfigPath + ".tmp") && reloaded.Suggestions.Threshold == 7 && store.LoadProblem == null);

            Check("лог правок по умолчанию выключен", !new AppConfig().Learning.Enabled);
        }
        catch (Exception ex)
        {
            Check($"хранилище конфига: исключение {ex.Message}", false);
        }
        finally
        {
            try { System.IO.Directory.Delete(dir, recursive: true); } catch { }
        }

        uint il = ExclusionManager.OwnIntegrityLevel;
        Check($"уровень целостности процесса прочитан (0x{il:X})", il >= 0x1000);
        IntPtr shell = NativeMethods.GetShellWindow();
        if (shell != IntPtr.Zero)
            Check("окно рабочего стола (explorer) не считается окном с правами выше", !new ExclusionManager().IsAboveOwnIntegrity(shell));

        return fail;
    }

    /// <summary>
    /// Фейковая клавиатура для headless-прогона InputProcessor: таблицы QWERTY/ЙЦУКЕН
    /// вместо ToUnicodeEx, модель "экрана" вместо SendInput. Клавиши подаются по
    /// символам АНГЛИЙСКОЙ раскладки (= физические позиции), Current задаёт активную.
    /// </summary>
    private sealed class FakeKeyboard
    {
        public static readonly IntPtr En = (IntPtr)0x04090409;
        public static readonly IntPtr Ru = (IntPtr)0x04190419;

        public IntPtr Current = En;
        public bool Shift;
        public bool Chord;      // зажат Ctrl/Alt/Win (команда, не текст)
        public IntPtr Window = (IntPtr)1; // "активное окно" - смена сбрасывает состояние
        public string Screen => _screen.ToString();

        private readonly StringBuilder _screen = new();
        private readonly InputProcessor _proc;

        // (vk, shift) -> символ; только клавиши, нужные сценариям.
        private static readonly Dictionary<(uint Vk, bool Shift), string> EnMap = new();
        private static readonly Dictionary<(uint Vk, bool Shift), string> RuMap = new();
        private static readonly Dictionary<char, (uint Vk, bool Shift)> EnRev = new();

        static FakeKeyboard()
        {
            const string enKeys    = "qwertyuiop[]asdfghjkl;'zxcvbnm,.";
            const string enShifted = "QWERTYUIOP{}ASDFGHJKL:\"ZXCVBNM<>";
            const string ruLetters = "йцукенгшщзхъфывапролджэячсмитьбю";
            for (int i = 0; i < enKeys.Length; i++)
            {
                char en = enKeys[i];
                uint vk = en switch
                {
                    '[' => 0xDB, ']' => 0xDD, ';' => 0xBA, '\'' => 0xDE, ',' => 0xBC, '.' => 0xBE,
                    _ => char.ToUpperInvariant(en)
                };
                EnMap[(vk, false)] = en.ToString();
                EnMap[(vk, true)] = enShifted[i].ToString();
                RuMap[(vk, false)] = ruLetters[i].ToString();
                RuMap[(vk, true)] = char.ToUpperInvariant(ruLetters[i]).ToString();
            }
            // Клавиша /? : в EN - '/', '?'; в РУ - '.', ','.
            EnMap[(0xBF, false)] = "/"; EnMap[(0xBF, true)] = "?";
            RuMap[(0xBF, false)] = "."; RuMap[(0xBF, true)] = ",";
            // Дефис и равно - одинаковы в обеих раскладках (нужны для 'какой-то').
            EnMap[(0xBD, false)] = "-"; EnMap[(0xBD, true)] = "_";
            RuMap[(0xBD, false)] = "-"; RuMap[(0xBD, true)] = "_";
            // Цифровой ряд.
            const string digits = "1234567890", enSh = "!@#$%^&*()", ruSh = "!\"№;%:?*()";
            for (int i = 0; i < digits.Length; i++)
            {
                uint vk = digits[i];
                EnMap[(vk, false)] = digits[i].ToString(); EnMap[(vk, true)] = enSh[i].ToString();
                RuMap[(vk, false)] = digits[i].ToString(); RuMap[(vk, true)] = ruSh[i].ToString();
            }
            EnMap[(0x20, false)] = " "; RuMap[(0x20, false)] = " ";
            EnMap[(0x09, false)] = "	"; RuMap[(0x09, false)] = "	";
            EnMap[(0x0D, false)] = "\r"; RuMap[(0x0D, false)] = "\r";

            foreach (var kv in EnMap)
                if (kv.Value.Length == 1 && !EnRev.ContainsKey(kv.Value[0]))
                    EnRev[kv.Value[0]] = kv.Key;
        }

        private static string? Translate(uint vk, uint scan, IntPtr hkl, bool shift, bool caps)
        {
            var map = (hkl.ToInt64() & 0xFFFF) == 0x0419 ? RuMap : EnMap;
            return map.TryGetValue((vk, shift), out var s) ? s : null;
        }

        public FakeKeyboard(Analyzer analyzer, ExclusionManager? exclusions = null)
        {
            _proc = new InputProcessor(analyzer, dispatcher: null, exclusions, skipPasswordFields: exclusions != null)
            {
                RuLayout = Ru,
                EnLayout = En,
                CurrentLayout = () => Current,
                TranslateLive = (vk, scan, hkl) => Translate(vk, scan, hkl, Shift, false),
                TranslateWith = Translate,
                Modifiers = () => (Shift, false),
                CommandChordHeld = () => Chord,
                ForegroundWindow = () => Window,
            };
            _proc.ReplacementRequested += r => Apply(r.Backspaces, r.Text, r.Layout);
            _proc.ToggleRequested += t => Apply(t.Backspaces, t.Text, t.ActivateLayout);
        }

        private void Apply(int backspaces, string text, LayoutTarget lt)
        {
            if (lt == LayoutTarget.Ru) Current = Ru;
            else if (lt == LayoutTarget.En) Current = En;
            int n = Math.Min(backspaces, _screen.Length);
            _screen.Remove(_screen.Length - n, n);
            _screen.Append(text);
        }

        /// <summary>Нажать клавиши, заданные символами английской раскладки.</summary>
        public void Type(string enChars)
        {
            foreach (char c in enChars)
            {
                if (!EnRev.TryGetValue(c, out var k))
                    throw new InvalidOperationException($"FakeKeyboard: нет клавиши для '{c}'");
                Press(k.Vk, k.Shift);
            }
        }

        public void Press(uint vk, bool shift)
        {
            Shift = shift;
            var args = new KeyboardHook.KeyArgs { VkCode = vk, ScanCode = 0, IsKeyDown = true };
            _proc.OnKey(null, args);
            if (!args.Suppress)
            {
                string? ch = Translate(vk, 0, Current, shift, false);
                if (ch != null) _screen.Append(ch);
            }
        }
    }

    private static string Describe(Decision d) => d.Kind switch
    {
        ActionKind.None => "(без изменений)",
        ActionKind.Typo => $"опечатка => '{d.Replacement}'",
        ActionKind.Rule => $"правило  => '{d.Replacement}'",
        ActionKind.Layout => $"раскладка=> '{d.Replacement}' ({d.Layout})",
        _ => "?"
    };
}
