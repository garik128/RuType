namespace RuType.Core;

/// <summary>
/// Подгонка регистра результата под исходный ввод (ТЗ, раздел 10):
/// ВСЕ ПРОПИСНЫЕ -> результат прописными; Первая прописная -> капитализация;
/// иначе как есть (нижний регистр результата).
/// </summary>
public static class CaseHelper
{
    public static string ApplyCasePattern(string source, string replacementLower)
    {
        if (string.IsNullOrEmpty(replacementLower)) return replacementLower;
        if (string.IsNullOrEmpty(source)) return replacementLower;

        if (IsAllUpper(source))
            return replacementLower.ToUpperInvariant();

        if (char.IsUpper(source[0]))
            return char.ToUpperInvariant(replacementLower[0]) + replacementLower[1..];

        return replacementLower;
    }

    /// <summary>
    /// Опечатка регистра "ДВе заглавные": 'ДВе' -> 'Две', 'THe' -> 'The'. Слово из одной
    /// письменности, от 3 букв, первые две прописные, остальные строчные. null - не наш
    /// случай. Латинское 'XYs' не трогаем - это множественное аббревиатуры (IDs, PCs).
    /// Проверку словарём (результат должен быть словом) делает вызывающая сторона.
    /// </summary>
    public static string? FixTwoCapitals(string word)
    {
        if (word.Length < 3) return null;
        bool cyr = IsCyr(word[0]);
        foreach (char c in word)
        {
            if (cyr ? !IsCyr(c) : !IsLat(c)) return null;   // одна письменность, только буквы
        }
        if (!char.IsUpper(word[0]) || !char.IsUpper(word[1])) return null;
        for (int i = 2; i < word.Length; i++)
            if (!char.IsLower(word[i])) return null;
        if (!cyr && word.Length == 3 && word[2] == 's') return null;
        return word[0] + word[1..].ToLowerInvariant();
    }

    /// <summary>
    /// Следующий вариант регистра по кругу: строчные -> ПРОПИСНЫЕ -> Первая прописная ->
    /// строчные. Смешанный регистр ('ПРивет') сначала становится строчным. Вариант,
    /// совпадающий с текущим (однобуквенное 'A': прописные = первая прописная),
    /// пропускается. null - в слове нет букв.
    /// </summary>
    public static string? NextCase(string word)
    {
        bool hasLetter = false, anyUpper = false, anyLower = false;
        foreach (char c in word)
        {
            if (!char.IsLetter(c)) continue;
            hasLetter = true;
            if (char.IsUpper(c)) anyUpper = true;
            else if (char.IsLower(c)) anyLower = true;
        }
        if (!hasLetter) return null;

        string lower = word.ToLowerInvariant();
        string upper = word.ToUpperInvariant();
        string title = Title(lower);

        string next;
        if (!anyUpper) next = upper;                 // строчные -> ПРОПИСНЫЕ
        else if (!anyLower) next = title;            // ПРОПИСНЫЕ -> Первая прописная
        else if (word == title) next = lower;        // Первая прописная -> строчные
        else next = lower;                           // смешанный -> строчные

        if (next == word) next = next == title ? lower : title;
        return next == word ? null : next;
    }

    private static string Title(string lower)
    {
        for (int i = 0; i < lower.Length; i++)
            if (char.IsLetter(lower[i]))
                return lower[..i] + char.ToUpperInvariant(lower[i]) + lower[(i + 1)..];
        return lower;
    }

    private static bool IsCyr(char c)
        => (c >= 'а' && c <= 'я') || (c >= 'А' && c <= 'Я') || c == 'ё' || c == 'Ё';

    private static bool IsLat(char c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');

    private static bool IsAllUpper(string s)
    {
        bool hasLetter = false;
        foreach (char c in s)
        {
            if (char.IsLetter(c))
            {
                hasLetter = true;
                if (!char.IsUpper(c)) return false;
            }
        }
        return hasLetter;
    }
}
