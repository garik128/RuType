using System.IO;
using WeCantSpell.Hunspell;

namespace RuType.Core;

/// <summary>Параметры выбора кандидата исправления опечатки.</summary>
public readonly record struct TypoOptions(
    int MaxEditDistance, double DominanceRatio, long MinFrequency,
    bool UseNgram, double NgramWeight, double FreqWeight, double ScoreGap);

/// <summary>
/// Оракул валидности слов на Hunspell (морфология решает "ошибка/не ошибка") +
/// частотный список и триграммная модель для ранжирования кандидатов ("на что
/// менять"). Плюс пользовательские списки: мой словарь, стоп-слова, правила.
/// </summary>
public sealed class Dictionaries
{
    private WordList? _ruHun;
    private WordList? _enHun;
    private WordList? _ruExtraHun;
    private volatile NgramModel? _ngram;

    /// <summary>Подключить триграммную модель для ранжирования кандидатов (с любого потока).</summary>
    public void SetNgram(NgramModel? ngram) => _ngram = ngram;
    public bool NgramLoaded => _ngram is { Loaded: true };

    private readonly Dictionary<string, long> _freq = new(StringComparer.Ordinal);
    // Индекс частотных форм по 2-символьному префиксу - источник fuzzy-кандидатов
    // там, где Hunspell.Suggest не предлагает очевидное частотное слово.
    private readonly Dictionary<string, List<string>> _freqPrefix = new(StringComparer.Ordinal);
    private bool _freqIndexBuilt;
    // Пользовательские списки перечитываются из UI-потока, а читаются потоком хука:
    // коллекции не правятся на месте, а собираются заново и подменяются ссылкой.
    private volatile HashSet<string> _myWords = new(StringComparer.Ordinal);
    private volatile HashSet<string> _stopWords = new(StringComparer.Ordinal);
    private readonly HashSet<string> _extraWords = new(StringComparer.Ordinal);
    private readonly HashSet<string> _enExtraWords = new(StringComparer.Ordinal);
    private volatile Dictionary<string, string> _rules = new(StringComparer.Ordinal);

    public bool RuLoaded => _ruHun != null;
    public bool EnLoaded => _enHun != null;
    public bool FreqLoaded => _freq.Count > 0;
    public int FreqCount => _freq.Count;
    public int MyWordsCount => _myWords.Count;
    public int StopWordsCount => _stopWords.Count;
    public int ExtraWordsCount => _extraWords.Count;
    public int EnExtraWordsCount => _enExtraWords.Count;
    public int RulesCount => _rules.Count;

    public bool LoadRuHunspell(string dicPath)
    {
        _ruHun = TryLoadHunspell(dicPath);
        return _ruHun != null;
    }

    public bool LoadEnHunspell(string dicPath)
    {
        _enHun = TryLoadHunspell(dicPath);
        return _enHun != null;
    }

    /// <summary>
    /// Словарь-дополнение как НАСТОЯЩИЙ Hunspell-словарь: леммы с аффиксными флагами
    /// ru_RU.aff ("модалка/I"), поэтому морфология генерирует всю парадигму сама.
    /// Плоский список ru_extra.txt этого не умеет, и неполная парадигма там не просто
    /// не помогала, а активно портила: 'пасхалках' -> 'пасхалка' (форма из списка
    /// оказывалась единственным кандидатом на расстоянии 1). Файл .aff берём общий
    /// с основным словарём - дублировать его не нужно.
    /// </summary>
    public bool LoadRuExtraHunspell(string dicPath, string affPath)
    {
        _ruExtraHun = null;
        try
        {
            if (!File.Exists(dicPath) || !File.Exists(affPath)) return false;

            // Формат .dic комментариев не знает, а словарь курируется руками, и без
            // пояснений (какой флаг что склоняет) он нечитаем. Поэтому '#'-строки
            // вырезаем на лету и подаём Hunspell уже чистый поток; счётчик слов в
            // первой строке пересчитываем сами, чтобы не ловить рассинхрон при правках.
            using var dicStream = BuildCleanDic(dicPath);
            using var affStream = File.OpenRead(affPath);
            _ruExtraHun = WordList.CreateFromStreams(dicStream, affStream);
        }
        catch
        {
            _ruExtraHun = null;
        }
        return _ruExtraHun != null;
    }

    private static MemoryStream BuildCleanDic(string dicPath)
    {
        var entries = new List<string>();
        foreach (var raw in File.ReadLines(dicPath))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (entries.Count == 0 && long.TryParse(line, out _)) continue; // старый счётчик
            entries.Add(line);
        }

        var sb = new System.Text.StringBuilder();
        sb.Append(entries.Count).Append('\n');
        foreach (var e in entries) sb.Append(e).Append('\n');
        return new MemoryStream(System.Text.Encoding.UTF8.GetBytes(sb.ToString()));
    }

    private static WordList? TryLoadHunspell(string dicPath)
    {
        try
        {
            if (!File.Exists(dicPath)) return null;
            string affPath = Path.ChangeExtension(dicPath, ".aff");
            if (!File.Exists(affPath)) return null;
            return WordList.CreateFromFiles(dicPath, affPath);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Частотный список "слово частота" для ранжирования кандидатов.</summary>
    public bool LoadFreq(string path)
    {
        _freq.Clear();
        if (!File.Exists(path)) return false;
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            int sp = line.IndexOf(' ');
            if (sp <= 0) continue;
            var word = line[..sp].Trim().ToLowerInvariant();
            if (word.Length == 0) continue;
            if (long.TryParse(line[(sp + 1)..].Trim(), out long count))
                _freq[word] = count;
        }
        return _freq.Count > 0;
    }

    public long Frequency(string lowerWord) => _freq.TryGetValue(lowerWord, out var v) ? v : 0;

    /// <summary>
    /// Заранее построить префиксный индекс частот (306k форм). Вызывать на старте,
    /// вне потока клавиатурного хука: ленивое построение в SuggestTypo дало бы разовую
    /// задержку прямо в LL-хуке, что Windows может расценить как зависание и снять хук.
    /// </summary>
    public void PrewarmIndexes() => EnsureFreqIndex();

    /// <summary>Ленивое построение префиксного индекса частотных форм (для fuzzy-кандидатов).</summary>
    private void EnsureFreqIndex()
    {
        if (_freqIndexBuilt) return;
        _freqIndexBuilt = true;
        foreach (var w in _freq.Keys)
        {
            if (w.Length < 2) continue;
            string key = w[..2];
            if (!_freqPrefix.TryGetValue(key, out var list)) _freqPrefix[key] = list = new List<string>();
            list.Add(w);
        }
    }

    /// <summary>
    /// Словарь-дополнение (ru_extra): современная лексика/англицизмы, которых нет
    /// в Hunspell. Формат строк "слово [частота]". Расширяет оракул валидности и
    /// служит источником кандидатов при исправлении опечаток.
    /// </summary>
    public bool LoadExtra(string path)
    {
        _extraWords.Clear();
        if (!File.Exists(path)) return false;
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            int sp = line.IndexOf(' ');
            string word = (sp <= 0 ? line : line[..sp]).Trim().ToLowerInvariant();
            if (word.Length == 0) continue;
            _extraWords.Add(word);
            // Частоту (если есть) кладём в общий список - для ранжирования кандидатов.
            if (sp > 0 && long.TryParse(line[(sp + 1)..].Trim(), out long count) && !_freq.ContainsKey(word))
                _freq[word] = count;
        }
        return _extraWords.Count > 0;
    }

    /// <summary>
    /// Словарь-дополнение EN (en_extra): тех-токены/аббревиатуры/бренды, которых нет
    /// в en_US Hunspell (tcp, ctrl, github). Формат: слово на строку. Расширяет
    /// IsValidEn в обе стороны: защита от перекладки en->ru (tcp больше не 'ест')
    /// и автоперекладка ru->en без ручного правила ('пшерги' -> github).
    /// </summary>
    public bool LoadEnExtra(string path)
    {
        _enExtraWords.Clear();
        if (!File.Exists(path)) return false;
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            _enExtraWords.Add(line.ToLowerInvariant());
        }
        return _enExtraWords.Count > 0;
    }

    /// <summary>Слово - валидное русское (Hunspell, мой словарь либо словарь-дополнение).</summary>
    public bool IsValidRu(string word)
    {
        string lower = word.ToLowerInvariant();
        if (_myWords.Contains(lower)) return true;
        if (_extraWords.Contains(lower)) return true;
        if (_ruExtraHun != null && _ruExtraHun.Check(lower)) return true;
        if (_ruHun == null) return false;
        return _ruHun.Check(word) || _ruHun.Check(lower);
    }

    /// <summary>Слово - валидное английское (Hunspell либо словарь-дополнение en_extra).</summary>
    public bool IsValidEn(string word)
    {
        if (_enExtraWords.Contains(word.ToLowerInvariant())) return true;
        if (_enHun == null) return false;
        return _enHun.Check(word) || _enHun.Check(word.ToLowerInvariant());
    }

    /// <summary>
    /// Кандидаты исправления опечатки от Hunspell (+ словарь-дополнение),
    /// отфильтрованные по расстоянию и ранжированные. При включённой n-gram модели
    /// выбор идёт по "естественности" слова с gap-гейтом; иначе - по частотному
    /// доминанту. Возвращает исправление (нижний регистр) или null.
    /// </summary>
    public string? SuggestTypo(string lowerWord, in TypoOptions o)
    {
        if (_ruHun == null) return null;

        var scored = new List<(string term, int dist, long freq)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var cand in _ruHun.Suggest(lowerWord))
        {
            string cl = cand.ToLowerInvariant();
            if (cl == lowerWord) continue;
            // Не расщепляем слово на два: Hunspell для неизвестного современного слова
            // предлагает разбивку на известные части ("переиспользовать" -> "пере
            // использовать"). Пробел = правка расстояния 1, но это почти всегда порча.
            if (cl.IndexOf(' ') >= 0) continue;
            int d = EditDistance.DamerauLevenshtein(lowerWord, cl, o.MaxEditDistance);
            if (d < 0 || d > o.MaxEditDistance) continue; // вне допустимого расстояния
            if (seen.Add(cl)) scored.Add((cl, d, Frequency(cl)));
        }

        // Кандидаты из аффиксного словаря-дополнения: он знает всю парадигму
        // ("модалку", "модалкой"), поэтому опечатка в любой форме чинится в ту же
        // форму, а не в единственную попавшую в плоский список.
        if (_ruExtraHun != null)
        {
            foreach (var cand in _ruExtraHun.Suggest(lowerWord))
            {
                string cl = cand.ToLowerInvariant();
                if (cl == lowerWord || cl.IndexOf(' ') >= 0) continue;
                int d = EditDistance.DamerauLevenshtein(lowerWord, cl, o.MaxEditDistance);
                if (d < 0 || d > o.MaxEditDistance) continue;
                if (seen.Add(cl)) scored.Add((cl, d, Frequency(cl)));
            }
        }

        // Кандидаты из словаря-дополнения (Hunspell их не знает, потому и не предложит):
        // напр. "десктп" -> "десктоп". Перебор дёшев - только при неизвестном слове.
        foreach (var cand in _extraWords)
        {
            if (cand == lowerWord) continue;
            if (Math.Abs(cand.Length - lowerWord.Length) > o.MaxEditDistance) continue; // быстрый отсев
            if (seen.Contains(cand)) continue;
            int d = EditDistance.DamerauLevenshtein(lowerWord, cand, o.MaxEditDistance);
            if (d < 0 || d > o.MaxEditDistance) continue;
            if (seen.Add(cand)) scored.Add((cand, d, Frequency(cand)));
        }

        // Fuzzy-кандидаты из частотного списка по 2-символьному префиксу. Hunspell.Suggest
        // нередко не предлагает очевидное частотное слово ("програма"->"программа");
        // частотные формы (306k) закрывают этот пробел. Отбор по префиксу + длине дёшев.
        EnsureFreqIndex();
        string pfx = lowerWord.Length >= 2 ? lowerWord[..2] : lowerWord;
        if (_freqPrefix.TryGetValue(pfx, out var bucket))
        {
            foreach (var cand in bucket)
            {
                if (cand == lowerWord || seen.Contains(cand)) continue;
                if (Math.Abs(cand.Length - lowerWord.Length) > o.MaxEditDistance) continue;
                int d = EditDistance.DamerauLevenshtein(lowerWord, cand, o.MaxEditDistance);
                if (d < 0 || d > o.MaxEditDistance) continue;
                if (seen.Add(cand)) scored.Add((cand, d, Frequency(cand)));
            }
        }

        if (scored.Count == 0) return null;

        // Ближайшие по расстоянию - самый сильный приор (опечатка = малая правка).
        int bestDist = scored.Min(s => s.dist);
        var top = scored.Where(s => s.dist == bestDist).ToList();

        // Основной путь: ранжир по триграммной "естественности" + частота, gap-гейт.
        if (o.UseNgram && _ngram is { Loaded: true } ng)
        {
            double ngramW = o.NgramWeight, freqW = o.FreqWeight, gap = o.ScoreGap;
            double Score((string term, int dist, long freq) c)
                => ngramW * ng.Margin(c.term, lowerWord) + freqW * Math.Log(c.freq + 1);

            return CandidateRanker.TryChooseBestWithGap(top, gap, Score, out var best)
                ? best.term : null;
        }

        // Резервный путь (n-gram выключен): частотный доминант.
        top.Sort((a, b) => b.freq.CompareTo(a.freq));
        var first = top[0];
        bool dominant =
            top.Count == 1 ||
            (first.freq >= o.MinFrequency && first.freq >= o.DominanceRatio * top[1].freq);
        return dominant ? first.term : null;
    }

    public bool IsMyWord(string lowerWord) => _myWords.Contains(lowerWord);
    public bool IsStopWord(string lowerWord) => _stopWords.Contains(lowerWord);
    public bool TryRule(string lowerWord, out string replacement) => _rules.TryGetValue(lowerWord, out replacement!);

    public void LoadUserLists(string myWordsPath, string stopWordsPath, string rulesPath)
    {
        _myWords = LoadWordSet(myWordsPath);
        _stopWords = LoadWordSet(stopWordsPath);
        _rules = LoadRules(rulesPath);
    }

    private static HashSet<string> LoadWordSet(string path)
    {
        var target = new HashSet<string>(StringComparer.Ordinal);
        if (!File.Exists(path)) return target;
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            target.Add(line.ToLowerInvariant());
        }
        return target;
    }

    private static Dictionary<string, string> LoadRules(string path)
    {
        var target = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(path)) return target;
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var from = line[..eq].Trim().ToLowerInvariant();
            var to = line[(eq + 1)..].Trim();
            if (from.Length == 0 || to.Length == 0) continue;
            target[from] = to;
        }
        return target;
    }
}
