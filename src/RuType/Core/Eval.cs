using System.Text;
using RuType.Config;

namespace RuType.Core;

/// <summary>
/// Оценка качества ядра на golden-наборе. Два типа кейсов (идея из аналога lay):
///   fix  - вход -> ожидаемый выход (проверяем, что чиним правильно);
///   keep - вход, который НЕЛЬЗЯ трогать (ловит ложные срабатывания).
/// Метрики раздельно: accuracy на fix и, что важнее для автокорректора,
/// FP-rate (доля испорченного) на keep. Плюс layout-кейсы генерируются
/// комбинаторно из раскладочной таблицы. Запуск: RuType.exe --eval.
/// </summary>
public static class Eval
{
    private readonly record struct Case(string Category, string Input, string Expected, bool IsKeep);

    public static int Run()
    {
        var store = new ConfigStore();
        var cfg = store.Load();
        store.EnsureUserLists();

        var dict = new Dictionaries();
        string ruDic = store.ResolveAppPath(cfg.Dictionaries.RuHunspell);
        bool ruOk = dict.LoadRuHunspell(ruDic);
        bool enOk = dict.LoadEnHunspell(store.ResolveAppPath(cfg.Dictionaries.EnHunspell));
        dict.LoadFreq(store.ResolveAppPath(cfg.Dictionaries.RuFreq));
        dict.LoadExtra(store.ResolveAppPath(cfg.Dictionaries.RuExtra));
        dict.LoadRuExtraHunspell(store.ResolveAppPath(cfg.Dictionaries.RuExtraDic),
            System.IO.Path.ChangeExtension(ruDic, ".aff"));
        dict.LoadEnExtra(store.ResolveAppPath(cfg.Dictionaries.EnExtra));
        dict.LoadUserLists(store.MyWordsPath, store.StopWordsPath, store.RulesPath);
        if (cfg.Ngram.Enabled && ruOk)
            dict.SetNgram(NgramModel.BuildOrLoad(ruDic, store.ResolveAppPath(cfg.Dictionaries.RuFreq),
                System.IO.Path.Combine(store.DataDir, cfg.Ngram.CacheFile), cfg.Ngram.WeightByFrequency));

        var analyzer = new Analyzer(dict, cfg) { LayoutDetectionEnabled = enOk };

        var cases = new List<Case>();
        cases.AddRange(CuratedCases());
        cases.AddRange(GeneratedLayoutCases(dict));

        var sb = new StringBuilder();
        sb.AppendLine($"ru={ruOk} en={enOk} ngram={dict.NgramLoaded} extra={dict.ExtraWordsCount} freq={dict.FreqCount}");
        sb.AppendLine($"типо: ngram={cfg.Ngram.Enabled && cfg.Typo.UseNgram} weight_by_freq={cfg.Ngram.WeightByFrequency} " +
                      $"gap={cfg.Typo.ScoreGap} protect_tech={cfg.Layout.ProtectTechnical} mixed={cfg.Layout.NormalizeMixedScript}");
        sb.AppendLine(new string('-', 64));

        // Метрики по категориям и общие FP/FN.
        var byCat = new Dictionary<string, (int ok, int total)>();
        int fixTotal = 0, fixOk = 0, keepTotal = 0, keepFp = 0;
        var mistakes = new List<string>();

        foreach (var c in cases)
        {
            Decision d = analyzer.Analyze(c.Input);
            string output = d.Kind == ActionKind.None ? c.Input : d.Replacement;
            bool ok;
            if (c.IsKeep)
            {
                keepTotal++;
                ok = string.Equals(output, c.Input, StringComparison.Ordinal);
                if (!ok) { keepFp++; mistakes.Add($"[FP {c.Category}] '{c.Input}' -> '{output}' ({d.Kind})"); }
            }
            else
            {
                fixTotal++;
                ok = string.Equals(output, c.Expected, StringComparison.OrdinalIgnoreCase);
                if (!ok) { mistakes.Add($"[FN {c.Category}] '{c.Input}' -> '{output}' (ждали '{c.Expected}', {d.Kind})"); }
                else fixOk++;
            }

            var agg = byCat.TryGetValue(c.Category, out var v) ? v : (0, 0);
            byCat[c.Category] = (agg.Item1 + (ok ? 1 : 0), agg.Item2 + 1);
        }

        sb.AppendLine("ПО КАТЕГОРИЯМ:");
        foreach (var kv in byCat.OrderBy(k => k.Key))
        {
            double acc = kv.Value.total == 0 ? 0 : 100.0 * kv.Value.ok / kv.Value.total;
            sb.AppendLine($"  {kv.Key,-16} {kv.Value.ok,4}/{kv.Value.total,-4} {acc,6:0.0}%");
        }
        sb.AppendLine(new string('-', 64));
        double fixAcc = fixTotal == 0 ? 0 : 100.0 * fixOk / fixTotal;
        double fpRate = keepTotal == 0 ? 0 : 100.0 * keepFp / keepTotal;
        sb.AppendLine($"FIX accuracy : {fixOk}/{fixTotal} = {fixAcc:0.0}%   (FN = {fixTotal - fixOk})");
        sb.AppendLine($"KEEP FP-rate : {keepFp}/{keepTotal} = {fpRate:0.0}%   (ложные правки нормального текста)");
        sb.AppendLine(new string('-', 64));

        if (mistakes.Count > 0)
        {
            sb.AppendLine($"ОШИБКИ ({mistakes.Count}):");
            foreach (var m in mistakes.Take(80)) sb.AppendLine("  " + m);
            if (mistakes.Count > 80) sb.AppendLine($"  ... и ещё {mistakes.Count - 80}");
        }
        else sb.AppendLine("Ошибок нет.");

        string report = sb.ToString();
        string outPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rutype_eval.txt");
        System.IO.File.WriteAllText(outPath, report);
        Console.WriteLine(report);
        Console.WriteLine($"[report -> {outPath}]");
        // Ненулевой код, если есть ложные правки или провалы fix - удобно для CI.
        return (keepFp == 0 && fixOk == fixTotal) ? 0 : 1;
    }

    // Курируемые кейсы. fix: вход -> ожидание; keep: не трогать.
    private static IEnumerable<Case> CuratedCases()
    {
        // Опечатки (должны исправиться).
        var typos = new (string, string)[]
        {
            ("привт", "привет"), ("докумен", "документ"), ("сообщене", "сообщение"),
            ("програма", "программа"), ("сегдня", "сегодня"), ("ошбка", "ошибка"),
            ("десктп", "десктоп"), ("компютер", "компьютер"), ("рабта", "работа"),
            ("вопрс", "вопрос"), ("клавиатра", "клавиатура"), ("телефн", "телефон"),
            ("разрабтчик", "разработчик"), ("сдлать", "сделать"), ("нескольо", "несколько"),
            ("спсибо", "спасибо"), ("пожалуйст", "пожалуйста"), ("здраствуйте", "здравствуйте"),
            ("приложене", "приложение"), ("оптимзация", "оптимизация"),
        };
        foreach (var (i, e) in typos) yield return new Case("typo", i, e, false);

        // Смешанные по алфавиту (одна буква из чужой раскладки).
        var mixed = new (string, string)[]
        {
            ("привеn", "привет"), ("hellщ", "hello"),
        };
        foreach (var (i, e) in mixed) yield return new Case("mixed", i, e, false);

        // "ДВе заглавные" в начале слова (case.fix_two_capitals).
        var twoCaps = new (string, string)[]
        {
            ("ДВе", "Две"), ("ПРивет", "Привет"), ("СЕгодня", "Сегодня"), ("THe", "The"),
            ("HEllo", "Hello"), ("GHbdtn", "Привет"), ("ПРивт", "Привет"), ("СТол", "Стол"),
        };
        foreach (var (i, e) in twoCaps) yield return new Case("two_caps", i, e, false);
        // Аббревиатуры, их множественное и тех-токены с каноничным регистром - не трогать.
        foreach (var w in new[] { "IDs", "PCs", "MHz", "GHz", "OAuth", "USB", "ООО", "СССР", "ВКонтакте", "IPhone", "МойОфис" })
            yield return new Case("keep_caps", w, w, true);

        // Нормальные русские слова - не трогать (в т.ч. редкие формы, англицизмы).
        foreach (var w in new[] { "кошка", "документ", "сегодня", "привет", "программа",
                                  "работает", "вкладки", "вкладок", "ноутбук", "деплой",
                                  "фронтенд", "бэкенд", "гаджет", "лайфхак",
                                  "компьютер", "телефон", "человек", "система", "сообщение",
                                  "настройки", "приложение", "браузер", "интернет", "сервер",
                                  "клавиатура", "мышка", "экран", "окно", "файл", "папка",
                                  "здравствуйте", "пожалуйста", "спасибо", "который", "которые",
                                  "сделать", "несколько", "вопрос", "ответ", "хорошо",
                                  "работать", "смотреть", "думаю", "знаю", "хочу",
                                  "будет", "может", "нужно", "можно", "потому",
                                  "мороженое", "кофе", "чай", "улица", "город",
                                  "разработчик", "тестирование", "оптимизация", "конфигурация" })
            yield return new Case("keep_ru", w, w, true);

        // Современные приставочные слова, которых нет в бедном Hunspell ru - НЕ разбивать
        // на два (болячка: "переиспользовать" -> "пере использовать").
        foreach (var w in new[] { "переиспользовать", "переиспользование", "подытожить",
                                  "недооценить", "сверхприбыль", "мегаполис", "антивирус",
                                  // Настоящие склейки двух слов тоже НЕ трогаем: без контекста
                                  // не отличить их от валидных слитных слов, а порча хуже пропуска.
                                  "приветдруг", "здравствуймир" })
            yield return new Case("keep_split", w, w, true);

        // Английские слова - не превращать в кириллицу.
        foreach (var w in new[] { "hello", "world", "google", "windows", "please", "message",
                                  "computer", "internet", "browser", "settings", "keyboard",
                                  "download", "software", "update", "email", "password",
                                  "system", "server", "client", "project", "manager" })
            yield return new Case("keep_en", w, w, true);

        // Технические токены - защищены (аббревиатуры, бренды, слова с цифрами).
        foreach (var w in new[] { "USB", "API", "NTFS", "AmoCRM", "GitHub", "iPhone", "win10", "x64" })
            yield return new Case("keep_tech", w, w, true);

        // Чужая раскладка И опечатка одновременно: перекладка не даёт валидного слова,
        // но кириллический вариант чинится обычным корректором (FixTypoOnSwitch).
        // 'gbhdtn' -> перекладка 'пирвет' -> опечатка -> 'привет'.
        yield return new Case("layout_typo", "gbhdtn", "привет", false);

        // Домены/имена файлов в чужой раскладке (SwitchKnownDomainExt). 'ю' = клавиша
        // точки в русской раскладке. Проверка по структуре 'имя.ext' + известный TLD.
        yield return new Case("domain", "куфвьуюьв", "readme.md", false);
        yield return new Case("domain", "пщщпдуюсщь", "google.com", false);
        yield return new Case("domain", "штвучюреьд", "index.html", false);

        // НЕ трогать: русские слова с буквой 'ю' (валидны -> None); неизвестное
        // расширение ('...юфис' = '.abc') не переключаем.
        foreach (var w in new[] { "юмор", "союз", "каюта", "меню", "убираю", "рисую" })
            yield return new Case("keep_yu", w, w, true);
        yield return new Case("keep_ext", "куфвьуюфис", "куфвьуюфис", true); // readme.abc

        // Современная лексика во ВСЕХ формах: раньше каждая форма, не попавшая в
        // плоский ru_extra.txt, ловила ложную правку ('модалку' -> 'мочалку',
        // 'админку' -> 'админу', 'навбар' -> 'навар'). Лечится аффиксным словарём
        // ru_extra.dic, где лемма склоняется морфологией. Список - реальные слова из
        // corrections.jsonl, которые пользователь откатывал вручную.
        foreach (var w in new[] { "модалка", "модалку", "модалки", "модалок", "модалкой", "модалках",
                                  "партнерка", "партнерку", "партнерке", "партнерок",
                                  "админка", "админку", "админке", "навбар", "навбара", "навбаре",
                                  "промт", "промты", "промтов", "промту", "конфиг", "конфиги", "конфига",
                                  "пикер", "пикера", "пикером", "бекап", "бэкап", "бекапы",
                                  "визуал", "визуала", "дизайнов", "дизайнах", "пасхалка", "пасхалках",
                                  "коммент", "комменты", "инфа", "инфу", "чекбокс", "хоткей", "хоткею",
                                  "тултип", "футер", "поповер", "ховер", "лендинг", "лендингов",
                                  "чатбот", "чатбота", "чатботов", "оркестратор", "оркестратора",
                                  "юзеру", "копирайтера", "хардкод", "кодинг", "батч", "бабл",
                                  "проду", "бэке", "таба", "табе", "суперагента", "вебхук",
                                  "фейковый", "фейковую", "дефолтной", "кастомная", "акционную",
                                  "эмодзи", "портабл", "вживую", "сорри", "яндекс", "гугл", "телеграм",
                                  "сгенерировать", "задеплоить", "закоммитить" })
            yield return new Case("keep_modern", w, w, true);

        // Тех-токены латиницей: en_US Hunspell их не знает, поэтому детект раскладки
        // превращал их в русский мусор ('tcp' -> 'ест', 'ctrl' -> 'секс', 'url' ->
        // 'год', 'dir' -> 'век'). Закрыто словарём en_extra.
        foreach (var w in new[] { "tcp", "udp", "url", "ctrl", "dir", "gui", "cdn", "vps", "amd",
                                  "lan", "gif", "cron", "dhcp", "xhttp", "mcp", "api", "cpu", "ram",
                                  "ssh", "dns", "nas", "npm", "git", "github", "docker", "nginx",
                                  "redis", "json", "yaml", "html", "css", "svg", "webp", "jwt" })
            yield return new Case("keep_tech_en", w, w, true);

        // Латинские НЕ-английские токены, которые НЕ должны переключаться/правиться
        // (новая FP-поверхность FixTypoOnSwitch). Перекладка даёт мусор -> SuggestTypo null.
        foreach (var w in new[] { "asdfgh", "qwerty", "zxcvbn", "lorem", "ipsum", "dolor",
                                  "recieve", "seperate", "occured", "wrapper", "payload",
                                  "kubectl", "nginx", "kernel", "buffer", "cursor" })
            yield return new Case("keep_lat", w, w, true);
    }

    // Комбинаторная генерация layout-кейсов: берём валидные ru-слова, печатаем их
    // "в латинице" (MapRuToEn) - это wrong-layout вход, ожидаем возврат к ru-слову.
    // Плюс обратная проверка: реальные ru-слова остаются на месте (keep).
    private static IEnumerable<Case> GeneratedLayoutCases(Dictionaries dict)
    {
        // Слова только из букв, у которых есть пара в раскладочной таблице (без х/ж/э/ю/ё/ъ/б).
        string[] words =
        {
            "привет", "спасибо", "работа", "человек", "письмо", "город", "система",
            "слово", "стена", "пример", "правило", "клавиша", "сегодня", "минута",
            "спасена", "картина", "магазин", "правда", "погода", "процент",
            "капитан", "апрель", "радость", "статуя",
        };
        foreach (var w in words)
        {
            if (!dict.IsValidRu(w)) continue;
            string? en = LayoutMap.MapRuToEn(w);
            if (en == null || en.Length < 4) continue;
            yield return new Case("layout_gen", en, w, false);   // ghbdtn -> привет
            yield return new Case("keep_ru_gen", w, w, true);    // привет остаётся
        }
    }
}
