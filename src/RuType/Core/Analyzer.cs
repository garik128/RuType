using RuType.Config;

namespace RuType.Core;

public enum ActionKind { None, Typo, Rule, Layout }

public enum LayoutTarget { None, Ru, En }

public readonly record struct Decision(ActionKind Kind, string Replacement, LayoutTarget Layout = LayoutTarget.None)
{
    public static readonly Decision None = new(ActionKind.None, string.Empty);
}

/// <summary>
/// Логика принятия решения по дописанному слову. Порядок приоритетов из ТЗ
/// (раздел 6), первое сработавшее правило выигрывает:
/// стоп-слова -> правила -> мой словарь/базовый словарь -> SymSpell (опечатка).
/// Исключения (приложение/поле пароля) проверяются вызывающей стороной ДО анализа.
/// </summary>
public sealed class Analyzer
{
    private readonly Dictionaries _dict;
    private AppConfig _cfg;

    /// <summary>Детект раскладки активен (включён в конфиге И обе раскладки в системе).</summary>
    public bool LayoutDetectionEnabled { get; set; }

    /// <summary>Порог длины для авто-смены раскладки (нужен разбору сырого сегмента).</summary>
    public int LayoutMinWordLength => _cfg.Layout.MinWordLength;

    public Analyzer(Dictionaries dict, AppConfig cfg)
    {
        _dict = dict;
        _cfg = cfg;
    }

    public void UpdateConfig(AppConfig cfg) => _cfg = cfg;

    /// <summary>
    /// Проверка правила замены для произвольного токена (может содержать знаки),
    /// например "сырой" прогон клавиш ";jhbr". Стоп-слова имеют приоритет.
    /// </summary>
    public Decision MatchRule(string token)
    {
        if (string.IsNullOrEmpty(token)) return Decision.None;
        string lower = token.ToLowerInvariant();
        if (_dict.IsStopWord(lower)) return Decision.None;
        if (_dict.TryRule(lower, out var to))
        {
            string repl = CaseHelper.ApplyCasePattern(token, to);
            if (repl != token) return new Decision(ActionKind.Rule, repl);
        }
        return Decision.None;
    }

    public Decision Analyze(string word)
    {
        if (string.IsNullOrEmpty(word)) return Decision.None;

        // "ДВе заглавные" ('ДВе', 'GHbdtn'): дальше анализируем уже нормализованное
        // слово - так и перекладка раскладки получает 'Ghbdtn' -> 'Привет' (а 'GHbdtn'
        // защищался бы как camelCase-бренд). Если больше ничего не нашлось, правкой
        // считается сама смена регистра - при условии, что это словарное слово.
        string? twoCaps = _cfg.Case.FixTwoCapitals ? CaseHelper.FixTwoCapitals(word) : null;
        if (twoCaps == null) return AnalyzeCore(word);

        string lower = word.ToLowerInvariant();
        if (_dict.IsStopWord(lower)) return Decision.None;

        // Две заглавные задуманы, если хвост со второй буквы - сам слово: 'ВКонтакте'
        // (контакте), 'IPhone' (phone). У опечатки хвост - обрубок ('ривет', 'егодня').
        // От 4 букв: короткий хвост слишком часто случайно словарный ('СТол' -> 'тол').
        string tail = lower[1..];
        bool tailIsWord = tail.Length >= 4
            && (LayoutMap.IsAllCyrillic(word) ? _dict.IsValidRu(tail) : _dict.IsValidEn(tail));
        if (tailIsWord) return AnalyzeCore(word);

        Decision d = AnalyzeCore(twoCaps);
        if (d.Kind != ActionKind.None) return d;
        if (_dict.TryRule(lower, out _)) return Decision.None;  // правило совпало с регистром - не наше дело
        bool valid = LayoutMap.IsAllCyrillic(word)
            ? _dict.IsValidRu(lower)
            : _dict.IsValidEn(lower) && !_dict.IsEnExtra(lower);  // MHz, OAuth - канон регистра
        return valid ? new Decision(ActionKind.Typo, twoCaps) : Decision.None;
    }

    private Decision AnalyzeCore(string word)
    {
        string lower = word.ToLowerInvariant();

        // 2. Стоп-слова - руки прочь.
        if (_dict.IsStopWord(lower)) return Decision.None;

        // 3. Правило принудительной замены.
        if (_dict.TryRule(lower, out var ruleTo))
        {
            string repl = CaseHelper.ApplyCasePattern(word, ruleTo);
            if (repl != word) return new Decision(ActionKind.Rule, repl);
            return Decision.None;
        }

        // 4. Слово известно (мой словарь) - корректное, не трогаем.
        if (_dict.IsMyWord(lower)) return Decision.None;

        // 5a. Детект раскладки (ru<->en). Сильный сигнал - точное совпадение слова
        //     в целевой раскладке при невалидности в текущей. Идёт перед опечаткой,
        //     иначе слово вроде "ghbdtn" попало бы под нечёткую правку.
        if (LayoutDetectionEnabled && _cfg.Layout.Enabled)
        {
            Decision layout = DetectLayout(word, lower);
            if (layout.Kind != ActionKind.None) return layout;

            // 5a'. Смешанное по алфавиту слово ('привеn'): приводим буквы чужой
            //      раскладки к доминирующему алфавиту, если получается валидное слово.
            if (_cfg.Layout.NormalizeMixedScript)
            {
                Decision mixed = NormalizeMixedScript(word);
                if (mixed.Kind != ActionKind.None) return mixed;
            }
        }

        // 5b. Опечатка. Hunspell - вето: если слово существует, не трогаем вообще.
        //     Иначе берём лучшего кандидата (Hunspell suggest -> фильтр по
        //     расстоянию -> ранжир по n-gram/частоте с gap-гейтом).
        if (!_cfg.Typo.Enabled) return Decision.None;
        if (word.Length < _cfg.Typo.MinWordLength) return Decision.None;
        if (!IsPureCyrillic(word)) return Decision.None;        // только русские слова
        if (HasInnerCapital(word)) return Decision.None;        // 'ВКонтакте', 'МойОфис' - имя, не опечатка

        if (_dict.IsValidRu(word)) return Decision.None;        // морфология: слово существует

        string? suggestion = _dict.SuggestTypo(lower, TypoOpts());
        if (suggestion == null) return Decision.None;

        string corrected = CaseHelper.ApplyCasePattern(word, suggestion);
        if (corrected == word) return Decision.None;
        return new Decision(ActionKind.Typo, corrected);
    }

    /// <summary>
    /// Проверка гипотезы смены раскладки для уже отрендеренных строк (в т.ч. со
    /// знаками, которые в кириллице - буквы: ',' -> 'б'). cur - текущий вид слова,
    /// tgt - вид в целевой раскладке, target - язык целевой. Используется для слов
    /// с ведущим/иным знаком из "сырого" сегмента, который буквенный буфер теряет.
    /// </summary>
    public bool IsLayoutSwap(string cur, string tgt, LayoutTarget target)
    {
        if (!LayoutDetectionEnabled || !_cfg.Layout.Enabled) return false;
        if (tgt.Length < _cfg.Layout.MinWordLength) return false;
        string tgtLower = tgt.ToLowerInvariant();
        string curLower = cur.ToLowerInvariant();
        if (target == LayoutTarget.Ru)
            return _dict.IsValidRu(tgtLower) && !_dict.IsValidEn(curLower);
        if (target == LayoutTarget.En)
            return _dict.IsValidEn(tgtLower) && !_dict.IsValidRu(curLower);
        return false;
    }

    private TypoOptions TypoOpts() => new(
        _cfg.Typo.MaxEditDistance, _cfg.Typo.DominanceRatio, _cfg.Typo.MinFrequency,
        _cfg.Ngram.Enabled && _cfg.Typo.UseNgram, _cfg.Typo.NgramWeight, _cfg.Typo.FreqWeight, _cfg.Typo.ScoreGap);

    private Decision DetectLayout(string word, string lower)
    {
        if (word.Length < _cfg.Layout.MinWordLength) return Decision.None;

        // Технические токены (аббревиатуры, camelCase-бренды, слова с цифрами) не
        // перекладываем, даже если по буквам они совпадают со словом другой раскладки.
        if (_cfg.Layout.ProtectTechnical && TokenClassifier.IsTechnical(word)) return Decision.None;

        if (LayoutMap.IsAllLatinLetters(word))
        {
            // Набрано латиницей. Если это настоящее английское слово - не трогаем.
            if (_dict.IsValidEn(lower)) return Decision.None;
            string? ru = LayoutMap.MapEnToRu(word);
            if (ru == null) return Decision.None;
            string ruLower = ru.ToLowerInvariant();
            if (_dict.IsValidRu(ruLower))
                return new Decision(ActionKind.Layout, ru, LayoutTarget.Ru);

            // Раскладка верная, но в слове опечатка ('gbhdtn' -> 'пирвет' -> 'привет').
            // Не новый fuzzy: тот же SuggestTypo с теми же гейтами (dist<=1 + gap).
            // Порог длины отдельный и выше обычного: на 3-4 буквах эта ветка почти
            // всегда портит тех-токены (tcp -> 'есз' -> 'ест', url -> 'гкд' -> 'год'),
            // т.к. dist<=1 от короткого мусора почти всегда достаёт частотное слово.
            if (_cfg.Layout.FixTypoOnSwitch && _cfg.Typo.Enabled
                && word.Length >= _cfg.Layout.FixTypoMinLength)
            {
                string? fixedRu = _dict.SuggestTypo(ruLower, TypoOpts());
                if (fixedRu != null && fixedRu != ruLower)
                    return new Decision(ActionKind.Layout,
                        CaseHelper.ApplyCasePattern(word, fixedRu), LayoutTarget.Ru);
            }
            return Decision.None;
        }

        if (LayoutMap.IsAllCyrillic(word))
        {
            // Набрано кириллицей. Если это настоящее русское слово - не трогаем.
            if (_dict.IsValidRu(lower)) return Decision.None;
            string? en = LayoutMap.MapRuToEn(word);
            if (en != null && _dict.IsValidEn(en.ToLowerInvariant()))
                return new Decision(ActionKind.Layout, en, LayoutTarget.En);

            // Домены/имена файлов ('куфвьуюьв' -> 'readme.md', 'пщщпдуюсщь' ->
            // 'google.com'). Словарём не проверить (бренды/домены - не слова),
            // поэтому опираемся на структуру 'имя.ext' с известным TLD/расширением.
            if (_cfg.Layout.SwitchKnownDomainExt)
            {
                string? dom = LayoutMap.MapRuToEnDomain(word);
                if (dom != null && LooksLikeDomainOrFile(dom.ToLowerInvariant()))
                    return new Decision(ActionKind.Layout, dom, LayoutTarget.En);
            }
            return Decision.None;
        }

        return Decision.None;
    }

    /// <summary>
    /// Смешанное слово ('привеn', 'hellо'): буквы из чужой раскладки приводятся к
    /// доминирующему алфавиту слова. Меняем, только если результат - валидное слово.
    /// Раскладку не переключаем (доминирующий алфавит уже соответствует активной).
    /// </summary>
    private Decision NormalizeMixedScript(string word)
    {
        if (!TokenClassifier.IsMixedScript(word)) return Decision.None;
        if (_cfg.Layout.ProtectTechnical && TokenClassifier.IsTechnical(word)) return Decision.None;

        int cyr = 0, lat = 0;
        char lastKind = '\0';
        foreach (char c in word)
        {
            if (IsCyrCh(c)) { cyr++; lastKind = 'c'; }
            else if (IsLatCh(c)) { lat++; lastKind = 'l'; }
        }
        bool toCyr = cyr > lat || (cyr == lat && lastKind == 'c');

        string? norm = LayoutMap.NormalizeToScript(word, toCyr);
        if (norm == null || norm == word) return Decision.None;

        string lower = norm.ToLowerInvariant();
        bool valid = toCyr ? _dict.IsValidRu(lower) : _dict.IsValidEn(lower);
        if (!valid) return Decision.None;

        return new Decision(ActionKind.Typo, norm);
    }

    private static bool IsCyrCh(char c)
        => (c >= 'а' && c <= 'я') || (c >= 'А' && c <= 'Я') || c == 'ё' || c == 'Ё';

    private static bool IsLatCh(char c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');

    // Заглавная не в начале при наличии строчных (слово не целиком прописное): так
    // пишут бренды и названия, у них Hunspell предлагал бы "поправить" на обычное
    // слово ('ВКонтакте' -> 'Контакте').
    private static bool HasInnerCapital(string s)
    {
        bool inner = false, lower = false;
        for (int i = 0; i < s.Length; i++)
        {
            if (char.IsLower(s[i])) lower = true;
            else if (i > 0 && char.IsUpper(s[i])) inner = true;
        }
        return inner && lower;
    }

    private static bool IsPureCyrillic(string s)
    {
        foreach (char c in s)
        {
            if (!IsCyrCh(c)) return false;
        }
        return true;
    }

    // Известные TLD и расширения файлов - закрытый curated-список. Домены/имена
    // файлов не проверить словарём, но структура 'имя.ext' с известным хвостом -
    // сильный и узкий сигнал (реальные русские слова не оканчиваются на 'ю'+кластер
    // вроде 'юсщь'=.com, поэтому ложных срабатываний практически нет).
    private static readonly HashSet<string> KnownDomainSuffix = new(StringComparer.Ordinal)
    {
        // TLD
        "com", "ru", "org", "net", "io", "dev", "ai", "me", "app", "co", "info",
        "xyz", "biz", "pro", "su", "online", "site", "store", "tech", "cloud",
        // расширения файлов
        "txt", "md", "pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "csv",
        "log", "ini", "cfg", "json", "xml", "yaml", "yml", "html", "htm", "css",
        "scss", "js", "ts", "jsx", "tsx", "py", "cs", "cpp", "java", "go", "rs",
        "php", "rb", "sh", "bat", "ps1", "sql", "png", "jpg", "jpeg", "gif", "svg",
        "webp", "ico", "zip", "rar", "gz", "exe", "msi", "dll", "mp3", "mp4", "avi",
        "mkv", "wav",
    };

    // Латинская строка похожа на домен/имя файла: только буквы и точки, есть точка,
    // все сегменты непустые, последний сегмент - известный TLD/расширение.
    private static bool LooksLikeDomainOrFile(string latin)
    {
        int dot = latin.LastIndexOf('.');
        if (dot <= 0 || dot == latin.Length - 1) return false;   // нет точки / пустой стем / хвост
        if (!KnownDomainSuffix.Contains(latin[(dot + 1)..])) return false;
        foreach (char c in latin)
            if (!((c >= 'a' && c <= 'z') || c == '.')) return false;
        foreach (var seg in latin.Split('.'))
            if (seg.Length == 0) return false;                   // 'a..b', '.com', 'a.'
        return true;
    }
}
