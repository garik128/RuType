using System.IO;
using System.Text.Json;

namespace RuType.Config;

/// <summary>
/// Каталог данных data\ рядом с exe и чтение/запись config.json.
/// Пользовательские списки (my_words/stopwords/rules) - текстовые файлы рядом.
/// </summary>
public sealed class ConfigStore
{
    public string DataDir { get; }
    public string ConfigPath => Path.Combine(DataDir, "config.json");
    public string MyWordsPath => Path.Combine(DataDir, "my_words.txt");
    public string StopWordsPath => Path.Combine(DataDir, "stopwords.txt");
    public string RulesPath => Path.Combine(DataDir, "rules.txt");

    /// <summary>Каталог приложения (там лежат assets/ и dict/).</summary>
    public string AppDir { get; }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Последняя загрузка не удалась: config.json не прочитан и программа работает на
    /// дефолтах. Текст - для пользователя (куда сохранена копия испорченного файла).
    /// null - загрузка прошла нормально.
    /// </summary>
    public string? LoadProblem { get; private set; }

    private readonly object _saveLock = new();

    public ConfigStore()
        : this(Path.Combine(AppContext.BaseDirectory, "data"))
    {
    }

    /// <summary>Хранилище в произвольном каталоге данных (для самопроверки).</summary>
    public ConfigStore(string dataDir)
    {
        // Портабл: все данные пользователя в подпапке data рядом с exe.
        AppDir = AppContext.BaseDirectory;
        DataDir = dataDir;
        Directory.CreateDirectory(DataDir);
    }

    public AppConfig Load()
    {
        LoadProblem = null;
        if (!File.Exists(ConfigPath))
        {
            var fresh = new AppConfig();
            Save(fresh);
            return fresh;
        }

        try
        {
            string json = File.ReadAllText(ConfigPath);
            var cfg = JsonSerializer.Deserialize<AppConfig>(json, JsonOpts)
                      ?? throw new JsonException("config.json пуст (null)");
            cfg.Normalize();
            return cfg;
        }
        catch (Exception ex)
        {
            // Битый конфиг не должен ронять программу, но и молча затираться дефолтами
            // тоже: копия испорченного файла откладывается рядом, а вызывающий код по
            // LoadProblem не пересохраняет конфиг на старте и предупреждает пользователя.
            string? backup = BackupBroken();
            LoadProblem = backup != null
                ? $"config.json не прочитан ({ex.Message}). Копия сохранена как {Path.GetFileName(backup)}; загружены настройки по умолчанию."
                : $"config.json не прочитан ({ex.Message}), и сделать его копию не удалось. Загружены настройки по умолчанию; файл пока не перезаписан.";
            Core.Log.Line($"config: {LoadProblem}");
            return new AppConfig();
        }
    }

    // Копия нечитаемого config.json: config.json.broken-ГГГГММДД-ЧЧММСС.
    private string? BackupBroken()
    {
        try
        {
            string path = $"{ConfigPath}.broken-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Copy(ConfigPath, path, overwrite: false);
            return path;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Атомарная запись: во временный файл с flush на диск, затем переименование поверх
    /// config.json. Сбой питания или падение посреди записи оставляет либо старый, либо
    /// новый файл целиком - а не полупустой, который при старте превратился бы в дефолты.
    /// </summary>
    public void Save(AppConfig config)
    {
        config.Normalize(); // значения из формы настроек - в допустимые диапазоны
        string json = JsonSerializer.Serialize(config, JsonOpts);
        string tmp = ConfigPath + ".tmp";
        lock (_saveLock)
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var w = new StreamWriter(fs, new System.Text.UTF8Encoding(false)))
            {
                w.Write(json);
                w.Flush();
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmp, ConfigPath, overwrite: true);
        }
    }

    /// <summary>Независимая копия настроек (снимок для потока хука).</summary>
    public static AppConfig Clone(AppConfig config)
        => JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(config, JsonOpts), JsonOpts)!;

    /// <summary>Гарантирует существование пользовательских текстовых списков.</summary>
    public void EnsureUserLists()
    {
        if (!File.Exists(MyWordsPath))
            File.WriteAllText(MyWordsPath, "# Мой словарь: корректные слова, по одному на строку\n");
        if (!File.Exists(StopWordsPath))
            File.WriteAllText(StopWordsPath, "# Стоп-слова: не трогать, по одному на строку\n");
        if (!File.Exists(RulesPath))
            File.WriteAllText(RulesPath, "# Правила замены: опечатка = исправление\n");
    }

    /// <summary>Дозаписать слово в список (если его там ещё нет). Возвращает true, если добавлено.</summary>
    public bool AppendWord(string path, string word)
    {
        word = word.Trim();
        if (word.Length == 0) return false;

        if (File.Exists(path))
        {
            foreach (var raw in File.ReadLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                if (string.Equals(line, word, StringComparison.OrdinalIgnoreCase))
                    return false; // уже есть
            }
        }

        // Гарантируем перевод строки перед добавлением.
        if (File.Exists(path))
        {
            string existing = File.ReadAllText(path);
            if (existing.Length > 0 && !existing.EndsWith('\n'))
                File.AppendAllText(path, "\n");
        }
        File.AppendAllText(path, word + "\n");
        return true;
    }

    /// <summary>Дозаписать правило замены "from = to" (если такого ещё нет).</summary>
    public void AppendRule(string from, string to)
    {
        from = from.Trim();
        to = to.Trim();
        if (from.Length == 0 || to.Length == 0) return;

        if (File.Exists(RulesPath))
        {
            foreach (var raw in File.ReadLines(RulesPath))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                if (string.Equals(line[..eq].Trim(), from, StringComparison.OrdinalIgnoreCase))
                    return; // правило для этого слова уже есть
            }
            string existing = File.ReadAllText(RulesPath);
            if (existing.Length > 0 && !existing.EndsWith('\n'))
                File.AppendAllText(RulesPath, "\n");
        }
        File.AppendAllText(RulesPath, $"{from} = {to}\n");
    }

    /// <summary>Превращает относительный путь из конфига в абсолютный (от каталога приложения).</summary>
    public string ResolveAppPath(string relativeOrAbsolute)
    {
        if (Path.IsPathRooted(relativeOrAbsolute))
            return relativeOrAbsolute;
        return Path.Combine(AppDir, relativeOrAbsolute);
    }
}
