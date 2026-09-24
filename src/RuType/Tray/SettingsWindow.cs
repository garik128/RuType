using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using RuType.Config;
using RuType.Interop;

namespace RuType.Tray;

/// <summary>
/// Окно настроек в стиле параметров Windows 11: слева навигация по разделам, справа
/// страница из карточек «заголовок + пояснение + контрол». Шаблоны контролов (тумблеры,
/// поля, скроллбары) - в DarkTheme.xaml. Сохранение пишет config.json и пользовательские
/// списки, затем вызывает onSaved для применения на лету.
/// </summary>
public sealed class SettingsWindow : Window
{
    private readonly ConfigStore _store;
    private readonly AppConfig _cfg;
    private readonly Action _onSaved;

    // Контролы, значения которых читаются при сохранении.
    private CheckBox _enabled = null!, _autostart = null!, _learnLog = null!;
    private CheckBox _beepTypo = null!, _beepLayout = null!;
    private CheckBox _suggestEnabled = null!;
    private TextBox _threshold = null!;
    private TextBox _hotkeyBox = null!, _suggestBox = null!, _caseBox = null!;
    private int _hotkeyVk, _suggestVk, _caseVk;
    private bool _caseShift;
    private CheckBox _twoCaps = null!;

    private CheckBox _typoEnabled = null!;
    private RadioButton _ed1 = null!, _ed2 = null!;
    private TextBox _minWordLen = null!, _dominance = null!, _minFreq = null!, _scoreGap = null!;
    private CheckBox _ngramEnabled = null!, _ngramFreq = null!;

    private CheckBox _layoutEnabled = null!, _protectTech = null!, _normMixed = null!, _fixTypoOnSwitch = null!, _domainExt = null!;
    private TextBox _layoutMinLen = null!, _fixTypoMinLen = null!;

    private CheckBox _skipPw = null!;
    private TextBox _blacklist = null!;
    private TextBox _myWords = null!, _stopWords = null!, _rules = null!;
    private TextBlock _status = null!;

    private readonly StackPanel _nav;
    private readonly TextBlock _pageTitle;
    private readonly ContentControl _pageHost;
    private readonly Dictionary<RadioButton, (string Title, UIElement Page)> _pages = new();

    public SettingsWindow(ConfigStore store, AppConfig cfg, Action onSaved)
    {
        _store = store;
        _cfg = cfg;
        _onSaved = onSaved;
        _hotkeyVk = cfg.Layout.HotkeyUndoVk;
        _suggestVk = cfg.Layout.HotkeySuggestVk;
        _caseVk = cfg.Case.HotkeyVk;
        _caseShift = cfg.Case.HotkeyShift;

        Title = "RuType — настройки";
        Width = Math.Max(cfg.Ui.SettingsWidth, 760);
        Height = Math.Max(cfg.Ui.SettingsHeight, 540);
        MinWidth = 760;
        MinHeight = 540;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Theme.WinBg;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        FontSize = 14;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;

        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // --- Навигация ---
        _nav = new StackPanel { Margin = new Thickness(12, 16, 12, 12) };
        _nav.Children.Add(new TextBlock
        {
            Text = "RuType",
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.Fg,
            Margin = new Thickness(12, 4, 0, 0)
        });
        _nav.Children.Add(new TextBlock
        {
            Text = "автокорректор и раскладка",
            FontSize = 12,
            Foreground = Theme.Muted,
            Margin = new Thickness(12, 0, 0, 20)
        });
        var navBorder = new Border { Background = Theme.NavBg, Child = _nav };
        Grid.SetColumn(navBorder, 0);
        root.Children.Add(navBorder);

        // --- Страница ---
        var right = new Grid();
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetColumn(right, 1);
        root.Children.Add(right);

        _pageTitle = new TextBlock
        {
            FontSize = 26,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.Fg,
            Margin = new Thickness(32, 22, 32, 14)
        };
        Grid.SetRow(_pageTitle, 0);
        right.Children.Add(_pageTitle);

        _pageHost = new ContentControl { Margin = new Thickness(32, 0, 32, 16), Focusable = false };
        Grid.SetRow(_pageHost, 1);
        right.Children.Add(_pageHost);

        var footer = BuildFooter();
        Grid.SetRow(footer, 2);
        right.Children.Add(footer);

        AddPage("Общее", BuildGeneral(), scroll: true);
        AddPage("Опечатки", BuildTypo(), scroll: true);
        AddPage("Раскладка", BuildLayout(), scroll: true);
        AddPage("Словари", BuildDictionaries(), scroll: false);
        AddPage("Правила", BuildRules(), scroll: false);
        AddPage("Исключения", BuildExclusions(), scroll: false);

        Content = root;
        _pages.Keys.First().IsChecked = true;

        Closing += (_, _) =>
        {
            // Запомнить размер окна (без сохранения остальных правок формы).
            if (WindowState == WindowState.Normal)
            {
                _cfg.Ui.SettingsWidth = Math.Round(Width);
                _cfg.Ui.SettingsHeight = Math.Round(Height);
                try { _store.Save(_cfg); } catch { /* не критично */ }
            }
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Тёмный заголовок окна (Win10 2004+/Win11).
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        int on = 1;
        if (NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int)) != 0)
            NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref on, sizeof(int));
    }

    // --- навигация ---

    private void AddPage(string title, UIElement page, bool scroll)
    {
        UIElement host = scroll
            ? new ScrollViewer { Content = page, Style = (Style)FindResource("PageScroll"), Focusable = false }
            : page;
        var item = new RadioButton
        {
            Content = title,
            GroupName = "nav",
            Style = (Style)FindResource("NavItem")
        };
        item.Checked += (_, _) => ShowPage(item);
        _pages[item] = (title, host);
        _nav.Children.Add(item);
    }

    /// <summary>Открыть страницу по индексу (порядок навигации).</summary>
    public void SelectPage(int index)
    {
        if (index < 0 || index >= _pages.Count) return;
        _pages.Keys.ElementAt(index).IsChecked = true;
    }

    private void ShowPage(RadioButton item)
    {
        var (title, page) = _pages[item];
        _pageTitle.Text = title;
        _pageHost.Content = page;
    }

    // --- страницы ---

    private UIElement BuildGeneral()
    {
        var p = Page();

        var main = Card(p, "Программа");
        _enabled = Toggle(main, "Программа включена", _cfg.General.Enabled,
            "Главный выключатель. Когда выключено, программа не вмешивается в ввод. То же делает клик по иконке в трее.");
        _autostart = Toggle(main, "Запускать вместе с Windows", _cfg.General.Autostart,
            "Автозапуск через реестр текущего пользователя, без прав администратора. При переносе папки включите заново.");

        var keys = Card(p, "Горячие клавиши");
        _hotkeyBox = HotkeyRow(keys, "Откат правки / смена раскладки", _hotkeyVk, false, false, (v, _) => _hotkeyVk = v,
            "Если слово только что исправлено, клавиша возвращает исходный вариант (и обратно). " +
            "Иначе переключает раскладку набираемого или последнего слова: буквы и знаки, хоть один символ.");
        _suggestBox = HotkeyRow(keys, "Окно слова", _suggestVk, false, false, (v, _) => _suggestVk = v,
            "Открывает для последнего слова окно действий: не трогать, правило, смена раскладки.");
        _caseBox = HotkeyRow(keys, "Смена регистра", _caseVk, _caseShift, true, (v, sh) => { _caseVk = v; _caseShift = sh; },
            "Меняет регистр набираемого или последнего слова по кругу: строчные, ПРОПИСНЫЕ, Первая прописная. " +
            "Можно назначить клавишу вместе с Shift.");

        var sound = Card(p, "Звук");
        _beepTypo = Toggle(sound, "Сигнал при исправлении опечатки", _cfg.Sound.BeepOnTypo, "");
        _beepLayout = Toggle(sound, "Сигнал при смене раскладки", _cfg.Sound.BeepOnLayout, "");

        var learn = Card(p, "Обучение");
        _suggestEnabled = Toggle(learn, "Показывать окно слова после откатов", _cfg.Suggestions.Enabled,
            "Если правку одного и того же слова несколько раз откатили, всплывает окно с предложением занести слово в стоп-слова или задать правило.");
        _threshold = Number(learn, "Сколько откатов до показа окна", _cfg.Suggestions.Threshold.ToString(), "Обычно 2-5.");
        _learnLog = Toggle(learn, "Копить локальный лог правок", _cfg.Learning.Enabled,
            "В data/corrections.jsonl пишутся только события автозамены и откатов, без потока набора, паролей и содержимого окон. " +
            "Помогает настраивать качество на реальных данных. Никуда не отправляется.");
        return p;
    }

    private UIElement BuildTypo()
    {
        var p = Page();

        var main = Card(p, "Исправление");
        _typoEnabled = Toggle(main, "Исправлять опечатки", _cfg.Typo.Enabled,
            "Слово, которого нет в словаре, заменяется ближайшим словарным, если выбор однозначен. Существующие слова не трогаются.");
        _minWordLen = Number(main, "Минимальная длина слова", _cfg.Typo.MinWordLength.ToString(),
            "Более короткие слова не исправляются: слишком велик риск ошибиться.");
        _twoCaps = Toggle(main, "Исправлять ДВе заглавные", _cfg.Case.FixTwoCapitals,
            "Две прописные в начале слова: ДВе становится Две, THe становится The. Только если получается словарное слово; " +
            "аббревиатуры (USB, IDs) и тех-токены (MHz, OAuth) не трогаются. Работает независимо от исправления опечаток.");

        var dist = new StackPanel();
        _ed1 = new RadioButton { Content = "1 отличие, консервативно", GroupName = "ed", IsChecked = _cfg.Typo.MaxEditDistance <= 1, Margin = new Thickness(0, 0, 0, 6) };
        _ed2 = new RadioButton { Content = "2 отличия, агрессивнее", GroupName = "ed", IsChecked = _cfg.Typo.MaxEditDistance >= 2 };
        dist.Children.Add(_ed1);
        dist.Children.Add(_ed2);
        Row(main, "Допустимое число отличий", "Вставка, удаление, замена или перестановка буквы считаются одним отличием. Два отличия чинят больше опечаток, но и ошибаются чаще.", dist);

        var rank = Card(p, "Выбор замены");
        _ngramEnabled = Toggle(rank, "Оценивать естественность слова", _cfg.Ngram.Enabled,
            "Кандидаты ранжируются по триграммной модели русского языка и частоте, а не только по частоте. Точнее на редких формах.");
        _ngramFreq = Toggle(rank, "Учить модель на частотах", _cfg.Ngram.WeightByFrequency,
            "Модель строится по частотному списку с весом по частоте, что заметно лучше отделяет живой язык от мусора. Смена пересобирает кеш модели.");
        _scoreGap = Number(rank, "Порог уверенности", Fmt(_cfg.Typo.ScoreGap),
            "Замена происходит, только если лучший кандидат обгоняет второго на столько баллов. Больше значит строже.");

        var fallback = Card(p, "Резервный режим (если естественность выключена)");
        _dominance = Number(fallback, "Порог доминирования по частоте", Fmt(_cfg.Typo.DominanceRatio),
            "Лучший кандидат должен быть частотнее второго минимум во столько раз.");
        _minFreq = Number(fallback, "Минимальная частота кандидата", _cfg.Typo.MinFrequency.ToString(),
            "Кандидат должен встречаться в частотном списке не реже этого.");
        return p;
    }

    private UIElement BuildLayout()
    {
        var p = Page();

        var main = Card(p, "Автопереключение");
        _layoutEnabled = Toggle(main, "Переключать раскладку автоматически", _cfg.Layout.Enabled,
            "Если слово набрано не в той раскладке (ghbdtn) и в другой раскладке это настоящее слово (привет), раскладка переключается, а слово перенабирается.");
        _layoutMinLen = Number(main, "Минимальная длина слова", _cfg.Layout.MinWordLength.ToString(),
            "Короткие слова часто случайно совпадают со словом другой раскладки (yt и не). На ручную клавишу не влияет.");

        var extra = Card(p, "Дополнительно");
        _fixTypoOnSwitch = Toggle(extra, "Чинить опечатку вместе с раскладкой", _cfg.Layout.FixTypoOnSwitch,
            "Слово набрано не в той раскладке и ещё с опечаткой (gbhdtn): переключить раскладку и сразу исправить (привет).");
        _fixTypoMinLen = Number(extra, "Минимальная длина для этого случая", _cfg.Layout.FixTypoMinLength.ToString(),
            "На коротких токенах (tcp, url, php) такая догадка портит текст, поэтому порог отдельный и выше обычного.");
        _normMixed = Toggle(extra, "Чинить слова со смешанными буквами", _cfg.Layout.NormalizeMixedScript,
            "Одна буква из чужой раскладки (привеn) приводится к остальным, если получается настоящее слово.");
        _domainExt = Toggle(extra, "Переключать домены и имена файлов", _cfg.Layout.SwitchKnownDomainExt,
            "Токены вида имя.ext с известным доменом или расширением (пщщпдуюсщь в google.com, куфвьуюьв в readme.md).");
        _protectTech = Toggle(extra, "Не трогать технические токены", _cfg.Layout.ProtectTechnical,
            "Аббревиатуры (USB, API), бренды с заглавными внутри (GitHub, iPhone) и слова с цифрами (win10) не перекладываются, даже если похожи на слово другой раскладки.");
        return p;
    }

    private UIElement BuildDictionaries()
    {
        var g = new Grid();
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        Add(g, 0, EditorHeader("Мой словарь",
            "Слова, которые программа должна считать правильными: не исправляются и участвуют в определении раскладки. По одному на строку."));
        _myWords = Editor(ReadFile(_store.MyWordsPath));
        Add(g, 1, _myWords);

        var h2 = EditorHeader("Стоп-слова",
            "Слова, которые нельзя трогать вообще: ни опечатки, ни раскладка, ни правила. По одному на строку.");
        h2.Margin = new Thickness(0, 16, 0, 8);
        Add(g, 2, h2);
        _stopWords = Editor(ReadFile(_store.StopWordsPath));
        Add(g, 3, _stopWords);
        return g;
    }

    private UIElement BuildRules()
    {
        var g = new Grid();
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        Add(g, 0, EditorHeader("Правила замены",
            "Жёсткая замена одного слова на другое, без учёта регистра. Формат: что = на что, по одному правилу на строку."));
        _rules = Editor(ReadFile(_store.RulesPath));
        Add(g, 1, _rules);

        var hint = new TextBlock
        {
            Foreground = Theme.Muted,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0),
            Text = "Примеры: «тлф = телефон», «;jhbr = жорик» (имя, набранное в английской раскладке: ключ может содержать знаки). " +
                   "Правило сильнее словаря, но стоп-слово сильнее правила."
        };
        Add(g, 2, hint);
        return g;
    }

    private UIElement BuildExclusions()
    {
        var g = new Grid();
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var top = new StackPanel();
        var card = Card(top, null);
        _skipPw = Toggle(card, "Не трогать поля паролей", _cfg.Exclusions.SkipPasswordFields,
            "В полях ввода пароля программа ничего не правит и не запоминает (определение через Win32 и UI Automation).");
        Add(g, 0, top);

        var h = EditorHeader("Чёрный список приложений",
            "В этих приложениях программа полностью бездействует. Имя процесса по одному на строку, например chrome.exe. Для терминала Windows это WindowsTerminal.exe.");
        h.Margin = new Thickness(0, 20, 0, 8);
        Add(g, 1, h);
        _blacklist = Editor(string.Join("\n", _cfg.Exclusions.AppBlacklist));
        Add(g, 2, _blacklist);
        return g;
    }

    // --- сохранение ---

    private UIElement BuildFooter()
    {
        var bar = new Border
        {
            BorderBrush = Theme.Divider,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(32, 12, 32, 14)
        };
        var dock = new DockPanel { LastChildFill = false };
        bar.Child = dock;

        _status = new TextBlock { Foreground = Theme.Muted, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(_status, Dock.Left);
        dock.Children.Add(_status);

        var close = new Button { Content = "Закрыть", Margin = new Thickness(8, 0, 0, 0) };
        close.Click += (_, _) => Close();
        var save = new Button { Content = "Сохранить", Style = (Style)FindResource("AccentButton"), IsDefault = true };
        save.Click += (_, _) => Save();

        DockPanel.SetDock(close, Dock.Right);
        DockPanel.SetDock(save, Dock.Right);
        dock.Children.Add(close);
        dock.Children.Add(save);
        return bar;
    }

    private void Save()
    {
        _cfg.General.Enabled = _enabled.IsChecked == true;
        _cfg.General.Autostart = _autostart.IsChecked == true;
        _cfg.Learning.Enabled = _learnLog.IsChecked == true;
        _cfg.Sound.BeepOnTypo = _beepTypo.IsChecked == true;
        _cfg.Sound.BeepOnLayout = _beepLayout.IsChecked == true;
        _cfg.Suggestions.Enabled = _suggestEnabled.IsChecked == true;
        _cfg.Suggestions.Threshold = ParseInt(_threshold.Text, _cfg.Suggestions.Threshold);
        _cfg.Layout.HotkeyUndoVk = _hotkeyVk;
        _cfg.Layout.HotkeySuggestVk = _suggestVk;
        _cfg.Case.HotkeyVk = _caseVk;
        _cfg.Case.HotkeyShift = _caseShift;
        _cfg.Case.FixTwoCapitals = _twoCaps.IsChecked == true;

        _cfg.Typo.Enabled = _typoEnabled.IsChecked == true;
        _cfg.Typo.MaxEditDistance = _ed2.IsChecked == true ? 2 : 1;
        _cfg.Typo.MinWordLength = ParseInt(_minWordLen.Text, _cfg.Typo.MinWordLength);
        _cfg.Ngram.Enabled = _ngramEnabled.IsChecked == true;
        _cfg.Ngram.WeightByFrequency = _ngramFreq.IsChecked == true;
        _cfg.Typo.ScoreGap = ParseDouble(_scoreGap.Text, _cfg.Typo.ScoreGap);
        _cfg.Typo.DominanceRatio = ParseDouble(_dominance.Text, _cfg.Typo.DominanceRatio);
        _cfg.Typo.MinFrequency = ParseLong(_minFreq.Text, _cfg.Typo.MinFrequency);

        _cfg.Layout.Enabled = _layoutEnabled.IsChecked == true;
        _cfg.Layout.MinWordLength = ParseInt(_layoutMinLen.Text, _cfg.Layout.MinWordLength);
        _cfg.Layout.FixTypoOnSwitch = _fixTypoOnSwitch.IsChecked == true;
        _cfg.Layout.FixTypoMinLength = ParseInt(_fixTypoMinLen.Text, _cfg.Layout.FixTypoMinLength);
        _cfg.Layout.ProtectTechnical = _protectTech.IsChecked == true;
        _cfg.Layout.NormalizeMixedScript = _normMixed.IsChecked == true;
        _cfg.Layout.SwitchKnownDomainExt = _domainExt.IsChecked == true;

        _cfg.Exclusions.SkipPasswordFields = _skipPw.IsChecked == true;
        _cfg.Exclusions.AppBlacklist = SplitLines(_blacklist.Text);

        // Пользовательские списки - в файлы.
        File.WriteAllText(_store.MyWordsPath, _myWords.Text);
        File.WriteAllText(_store.StopWordsPath, _stopWords.Text);
        File.WriteAllText(_store.RulesPath, _rules.Text);

        _store.Save(_cfg);
        _onSaved();

        // Окно остаётся открытым - показываем подтверждение.
        _status.Text = $"Сохранено в {DateTime.Now:HH:mm:ss}";
    }

    // --- хелперы построения ---

    private static StackPanel Page() => new() { Margin = new Thickness(0, 0, 8, 0) };

    /// <summary>Карточка настроек: заголовок группы (опционально) + строки с разделителями.</summary>
    private static StackPanel Card(Panel parent, string? title)
    {
        if (title != null)
        {
            parent.Children.Add(new TextBlock
            {
                Text = title,
                Foreground = Theme.Fg,
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(2, parent.Children.Count == 0 ? 0 : 22, 0, 8)
            });
        }
        var rows = new StackPanel();
        parent.Children.Add(new Border
        {
            Background = Theme.PanelBg,
            CornerRadius = new CornerRadius(8),
            Child = rows,
            Margin = new Thickness(0, title == null && parent.Children.Count > 0 ? 12 : 0, 0, 0)
        });
        return rows;
    }

    /// <summary>Строка карточки: заголовок и пояснение слева, контрол справа.</summary>
    private static void Row(StackPanel card, string title, string desc, UIElement control)
    {
        if (card.Children.Count > 0)
            card.Children.Add(new Border { Height = 1, Background = Theme.Divider, Margin = new Thickness(16, 0, 16, 0) });

        var g = new Grid { Margin = new Thickness(16, 12, 16, 12) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, Foreground = Theme.Fg, TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrEmpty(desc))
            text.Children.Add(new TextBlock
            {
                Text = desc,
                Foreground = Theme.Muted,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 0)
            });
        g.Children.Add(text);

        if (control is FrameworkElement fe)
        {
            fe.VerticalAlignment = VerticalAlignment.Center;
            fe.Margin = new Thickness(24, 0, 0, 0);
        }
        Grid.SetColumn(control, 1);
        g.Children.Add(control);
        card.Children.Add(g);
    }

    private static CheckBox Toggle(StackPanel card, string title, bool value, string desc)
    {
        var c = new CheckBox { IsChecked = value };
        Row(card, title, desc, c);
        return c;
    }

    private static TextBox Number(StackPanel card, string title, string value, string desc)
    {
        var tb = new TextBox { Text = value, Width = 90, TextAlignment = TextAlignment.Right };
        Row(card, title, desc, tb);
        return tb;
    }

    private TextBox HotkeyRow(StackPanel card, string title, int vk, bool shift, bool allowShift,
        Action<int, bool> onCaptured, string desc)
    {
        var box = new TextBox
        {
            Text = HotkeyName(vk, shift),
            IsReadOnly = true,
            Width = 160,
            TextAlignment = TextAlignment.Center,
            Cursor = Cursors.Hand,
            ToolTip = "Кликните и нажмите клавишу"
        };
        box.GotKeyboardFocus += (_, _) => { box.Text = "нажмите клавишу…"; };
        box.LostKeyboardFocus += (_, _) => { box.Text = HotkeyName(vk, shift); };
        box.PreviewKeyDown += (_, e) =>
        {
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            e.Handled = true;
            if (key is Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl
                or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
                return;
            vk = KeyInterop.VirtualKeyFromKey(key);
            shift = allowShift && (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            onCaptured(vk, shift);
            box.Text = HotkeyName(vk, shift);
            Keyboard.ClearFocus();
        };
        Row(card, title, desc, box);
        return box;
    }

    private static StackPanel EditorHeader(string title, string desc)
    {
        var p = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        p.Children.Add(new TextBlock { Text = title, Foreground = Theme.Fg, FontSize = 15, FontWeight = FontWeights.SemiBold });
        p.Children.Add(new TextBlock
        {
            Text = desc,
            Foreground = Theme.Muted,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 0, 0)
        });
        return p;
    }

    private TextBox Editor(string text) => new()
    {
        Text = text,
        Style = (Style)FindResource("Editor")
    };

    private static void Add(Grid g, int row, UIElement el)
    {
        Grid.SetRow(el, row);
        g.Children.Add(el);
    }

    private static string HotkeyName(int vk, bool shift) => shift ? "Shift+" + KeyName(vk) : KeyName(vk);

    private static string KeyName(int vk)
    {
        try
        {
            Key k = KeyInterop.KeyFromVirtualKey(vk);
            return k switch
            {
                Key.None => $"VK 0x{vk:X2}",
                Key.Scroll => "Scroll Lock",
                Key.Pause => "Pause",
                Key.Insert => "Insert",
                Key.Apps => "Menu",
                _ => k.ToString()
            };
        }
        catch { return $"VK 0x{vk:X2}"; }
    }

    private static string ReadFile(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : ""; }
        catch { return ""; }
    }

    private static string Fmt(double v) => v.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static List<string> SplitLines(string text) => text
        .Replace("\r", "")
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .ToList();

    private static int ParseInt(string s, int fallback) => int.TryParse(s.Trim(), out var v) ? v : fallback;
    private static long ParseLong(string s, long fallback) => long.TryParse(s.Trim(), out var v) ? v : fallback;
    private static double ParseDouble(string s, double fallback)
        => double.TryParse(s.Trim().Replace(',', '.'), System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;
}
