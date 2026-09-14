namespace RuType.Config;

/// <summary>
/// Модель настроек (схема config.json из ТЗ, раздел 10).
/// Минимум хардкода: всё поведение - данные. Имена в JSON - snake_case.
/// </summary>
public sealed class AppConfig
{
    public GeneralCfg General { get; set; } = new();
    public TypoCfg Typo { get; set; } = new();
    public LayoutCfg Layout { get; set; } = new();
    public SoundCfg Sound { get; set; } = new();
    public TrayCfg Tray { get; set; } = new();
    public SuggestionsCfg Suggestions { get; set; } = new();
    public ExclusionsCfg Exclusions { get; set; } = new();
    public DictionariesCfg Dictionaries { get; set; } = new();
    public NgramCfg Ngram { get; set; } = new();
    public LearningCfg Learning { get; set; } = new();
    public UiCfg Ui { get; set; } = new();

    /// <summary>
    /// Привести загруженный конфиг к допустимому виду: секции, заданные в JSON как
    /// null, заменяются дефолтами (иначе NullReferenceException в потоке хука), числа
    /// зажимаются в разумные диапазоны, пустые пути возвращаются к дефолтным.
    /// Корректные значения не меняются.
    /// </summary>
    public void Normalize()
    {
        var d = new AppConfig();
        General ??= d.General;
        Typo ??= d.Typo;
        Layout ??= d.Layout;
        Sound ??= d.Sound;
        Tray ??= d.Tray;
        Suggestions ??= d.Suggestions;
        Exclusions ??= d.Exclusions;
        Dictionaries ??= d.Dictionaries;
        Ngram ??= d.Ngram;
        Learning ??= d.Learning;
        Ui ??= d.Ui;

        General.Theme = Str(General.Theme, d.General.Theme);
        General.Language = Str(General.Language, d.General.Language);

        Typo.MaxEditDistance = Math.Clamp(Typo.MaxEditDistance, 1, 2);
        Typo.MinWordLength = Math.Clamp(Typo.MinWordLength, 1, 50);
        Typo.DominanceRatio = Num(Typo.DominanceRatio, 1, 1e6, d.Typo.DominanceRatio);
        Typo.MinFrequency = Math.Max(0, Typo.MinFrequency);
        Typo.NgramWeight = Num(Typo.NgramWeight, 0, 100, d.Typo.NgramWeight);
        Typo.FreqWeight = Num(Typo.FreqWeight, 0, 100, d.Typo.FreqWeight);
        Typo.ScoreGap = Num(Typo.ScoreGap, 0, 100, d.Typo.ScoreGap);

        Layout.Aggressiveness = Str(Layout.Aggressiveness, d.Layout.Aggressiveness);
        Layout.MinWordLength = Math.Clamp(Layout.MinWordLength, 1, 50);
        Layout.FixTypoMinLength = Math.Clamp(Layout.FixTypoMinLength, 1, 50);
        Layout.HotkeyUndoVk = Math.Clamp(Layout.HotkeyUndoVk, 0, 255);
        Layout.HotkeySuggestVk = Math.Clamp(Layout.HotkeySuggestVk, 0, 255);

        Sound.TypoWav = Str(Sound.TypoWav, d.Sound.TypoWav);
        Sound.LayoutWav = Str(Sound.LayoutWav, d.Sound.LayoutWav);

        Tray.BlinkMs = Math.Clamp(Tray.BlinkMs, 0, 10000);
        Tray.IconIdle = Str(Tray.IconIdle, d.Tray.IconIdle);
        Tray.IconActive = Str(Tray.IconActive, d.Tray.IconActive);

        Suggestions.Threshold = Math.Clamp(Suggestions.Threshold, 1, 1000);

        Exclusions.AppBlacklist ??= new();
        Exclusions.AppBlacklist.RemoveAll(s => s == null);

        Dictionaries.RuHunspell = Str(Dictionaries.RuHunspell, d.Dictionaries.RuHunspell);
        Dictionaries.EnHunspell = Str(Dictionaries.EnHunspell, d.Dictionaries.EnHunspell);
        Dictionaries.RuFreq = Str(Dictionaries.RuFreq, d.Dictionaries.RuFreq);
        Dictionaries.RuExtra = Str(Dictionaries.RuExtra, d.Dictionaries.RuExtra);
        Dictionaries.RuExtraDic = Str(Dictionaries.RuExtraDic, d.Dictionaries.RuExtraDic);
        Dictionaries.EnExtra = Str(Dictionaries.EnExtra, d.Dictionaries.EnExtra);

        Ngram.CacheFile = Str(Ngram.CacheFile, d.Ngram.CacheFile);
        Learning.File = Str(Learning.File, d.Learning.File);

        Ui.SettingsWidth = Num(Ui.SettingsWidth, 300, 20000, d.Ui.SettingsWidth);
        Ui.SettingsHeight = Num(Ui.SettingsHeight, 300, 20000, d.Ui.SettingsHeight);
    }

    private static string Str(string? v, string def) => string.IsNullOrWhiteSpace(v) ? def : v;

    private static double Num(double v, double min, double max, double def)
        => double.IsFinite(v) ? Math.Clamp(v, min, max) : def;

    public sealed class UiCfg
    {
        // Размер окна настроек (запоминается при закрытии).
        public double SettingsWidth { get; set; } = 900;
        public double SettingsHeight { get; set; } = 660;
    }

    public sealed class GeneralCfg
    {
        public bool Autostart { get; set; } = false;
        public bool Enabled { get; set; } = true;
        public string Theme { get; set; } = "dark";
        public string Language { get; set; } = "ru";
    }

    public sealed class TypoCfg
    {
        public bool Enabled { get; set; } = true;
        // Расстояние редактирования кандидатов. 1 - консервативно (большинство опечаток),
        // 2 - агрессивнее, больше ложных.
        public int MaxEditDistance { get; set; } = 1;
        public int MinWordLength { get; set; } = 4;
        // Меняем только при явном доминанте: топ-кандидат частотнее ближайшего
        // конкурента минимум в DominanceRatio раз и не реже MinFrequency.
        // (Резервный путь ранжирования, когда n-gram выключен.)
        public double DominanceRatio { get; set; } = 5.0;
        public long MinFrequency { get; set; } = 50;
        // Ранжирование кандидатов через триграммную модель (естественность слова),
        // а не только по частоте. Решает "на что менять" точнее на редких формах.
        public bool UseNgram { get; set; } = true;
        // Вклад "естественности" (n-gram, средний лог/символ ~ -2..-12) и частоты
        // (ln(freq) ~ 0..14) в итоговый балл кандидата.
        public double NgramWeight { get; set; } = 1.0;
        public double FreqWeight { get; set; } = 1.0;
        // Применяем правку, только если лучший кандидат обгоняет второго на ScoreGap
        // (единый gap-гейт консервативности).
        public double ScoreGap { get; set; } = 1.5;
    }

    public sealed class LayoutCfg
    {
        public bool Enabled { get; set; } = true;
        public string Aggressiveness { get; set; } = "confident";
        // Короткие слова часто случайно совпадают с реальным словом в другой раскладке
        // (yt->не, go->пщ). Консервативный порог по длине отсекает их (ТЗ, раздел 7).
        public int MinWordLength { get; set; } = 4;
        // Виртуальный код клавиши хоткея (откат правки / принудительная смена раскладки).
        // По умолчанию Pause (0x13).
        public int HotkeyUndoVk { get; set; } = 0x13;
        // Хоткей вызова окна слова вручную. По умолчанию ScrollLock (0x91). 0 - выкл.
        public int HotkeySuggestVk { get; set; } = 0x91;
        // Не трогать технические токены (аббревиатуры, camelCase-бренды, слова с
        // цифрами) - защита от ложной перекладки токенов, случайно похожих на слово.
        public bool ProtectTechnical { get; set; } = true;
        // Чинить смешанные по алфавиту слова: 'привеn' -> 'привет' (одна буква из
        // чужой раскладки приводится к доминирующему алфавиту слова).
        public bool NormalizeMixedScript { get; set; } = true;
        // Если слово набрано в чужой раскладке И с опечаткой ('gbhdtn' -> 'пирвет'):
        // перекладка не даёт валидного слова, но кириллический вариант чинится обычным
        // корректором (тот же SuggestTypo, dist<=1 + gap-гейт) -> 'привет'. Переключаем
        // раскладку и сразу правим опечатку.
        public bool FixTypoOnSwitch { get; set; } = true;
        // Отдельный (более строгий) порог длины для FixTypoOnSwitch. На трёхбуквенных
        // токенах эта ветка почти всегда портит: от короткого мусора расстояние 1
        // достаёт какое-нибудь частотное слово (по corrections.jsonl: tcp->'ест',
        // url->'год', php->'зря', cpu->'сиг', tmp->'еьэ'). С четырёх букв она уже
        // окупается ('thkb'->'если', 'yeyj'->'нужно'), а тех-токены такой длины
        // (ctrl, cron, lang, dhcp) закрыты словарём en_extra.
        public int FixTypoMinLength { get; set; } = 4;
        // Переключать раскладку для доменов/имён файлов, набранных не в той раскладке
        // ('куфвьуюьв' -> 'readme.md', 'пщщпдуюсщь' -> 'google.com'). Словарём такие
        // токены не проверить, опора - структура 'имя.ext' с известным TLD/расширением.
        public bool SwitchKnownDomainExt { get; set; } = true;
    }

    public sealed class SoundCfg
    {
        public bool BeepOnTypo { get; set; } = true;
        public bool BeepOnLayout { get; set; } = true;
        public string TypoWav { get; set; } = "assets/spell.wav";
        public string LayoutWav { get; set; } = "assets/lang.wav";
    }

    public sealed class TrayCfg
    {
        public bool BlinkOnAction { get; set; } = true;
        public int BlinkMs { get; set; } = 200;
        public string IconIdle { get; set; } = "assets/icon_blue.png";
        public string IconActive { get; set; } = "assets/icon_red.png";
    }

    public sealed class SuggestionsCfg
    {
        public bool Enabled { get; set; } = true;
        public int Threshold { get; set; } = 3;
    }

    public sealed class ExclusionsCfg
    {
        public bool SkipPasswordFields { get; set; } = true;
        public List<string> AppBlacklist { get; set; } = new() { "mstsc.exe", "powershell.exe" };
    }

    public sealed class LearningCfg
    {
        // Копить локальный лог правок/откатов (data/corrections.jsonl) для анализа
        // качества и настройки порогов. Приватно, только события правок, не keylog.
        // Opt-in: по умолчанию выключено, включается пользователем в настройках.
        public bool Enabled { get; set; } = false;
        public string File { get; set; } = "corrections.jsonl";
    }

    public sealed class NgramCfg
    {
        // Триграммная модель "естественности" русского слова (кешируется в data/).
        // Общий рубильник ранжирования опечаток по n-gram.
        public bool Enabled { get; set; } = true;
        // Учить триграммы на частотном списке с весом по частоте (корпусная статистика
        // встречаемости буквосочетаний). Если false - на ru_RU.dic, каждая лемма 1 раз.
        public bool WeightByFrequency { get; set; } = true;
        // Имя файла кеша модели в каталоге данных.
        public string CacheFile { get; set; } = "ngram_ru.cache";
    }

    public sealed class DictionariesCfg
    {
        // Словари Hunspell (.dic; рядом должен лежать одноимённый .aff).
        public string RuHunspell { get; set; } = "dict/ru_RU.dic";
        public string EnHunspell { get; set; } = "dict/en_US.dic";
        // Частотный список "слово частота" для ранжирования кандидатов.
        public string RuFreq { get; set; } = "dict/ru-300k.txt";
        // Словарь-дополнение: современная лексика/англицизмы, которых нет в Hunspell.
        public string RuExtra { get; set; } = "dict/ru_extra.txt";
        // То же, но как Hunspell-словарь с аффиксными флагами ("модалка/I"): морфология
        // строит всю парадигму, поэтому склонение современных слов не требует ручного
        // перечисления форм. Файл .aff используется общий с ru_hunspell.
        public string RuExtraDic { get; set; } = "dict/ru_extra.dic";
        // Словарь-дополнение EN: тех-токены/аббревиатуры/бренды, которых нет в
        // en_US (tcp, ctrl, github). Расширяет IsValidEn: защищает такие токены от
        // перекладки en->ru И позволяет детекту чинить их набор в ru-раскладке
        // (пшерги -> github) без ручных правил.
        public string EnExtra { get; set; } = "dict/en_extra.txt";
    }
}
