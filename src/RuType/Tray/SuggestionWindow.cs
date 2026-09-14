using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using RuType.Core;
using RuType.Interop;

namespace RuType.Tray;

public enum SuggestionChoice { MyWord, StopWord, Rule, LayoutRule, Dismiss }

/// <summary>
/// Ненавязчивое окно обучения: появляется у курсора, не крадёт фокус ввода
/// (ShowActivated=false), предлагает действия по слову, которое пользователь
/// систематически откатывает (ТЗ, раздел 9).
/// </summary>
public sealed class SuggestionWindow : Window
{
    public event Action<SuggestionChoice>? Chosen;

    private static readonly Brush Bg = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x30));
    private static readonly Brush Fg = new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xE6));
    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0x9C, 0x9C, 0xA0));
    private static readonly Brush BtnBg = new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46));
    private static readonly Brush BtnHover = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x5A));
    private static readonly Brush BtnPressed = new SolidColorBrush(Color.FromRgb(0x2A, 0x6A, 0xB0));
    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x5A));

    private readonly Style _btnStyle = BuildButtonStyle();
    private readonly int _cursorX;
    private readonly int _cursorY;

    // Пассивный хук мыши: клик вне окна закрывает его. Окно не активируется
    // (ShowActivated=false), поэтому событие Deactivated не приходит, а Mouse.Capture
    // съел бы клик, адресованный приложению пользователя. Хук клик не перехватывает.
    private MouseHook? _outsideClickHook;

    public SuggestionWindow(string word)
    {
        NativeMethods.GetCursorPos(out var p);
        _cursorX = p.X;
        _cursorY = p.Y;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false; // не отбирать фокус у активного окна
        SizeToContent = SizeToContent.WidthAndHeight;
        Background = Bg;
        BorderBrush = Accent;
        BorderThickness = new Thickness(1);

        var root = new StackPanel { Margin = new Thickness(14, 12, 14, 12) };

        root.Children.Add(new TextBlock
        {
            Text = "RuType",
            Foreground = Muted,
            FontSize = 11,
            Margin = new Thickness(0, 0, 0, 6)
        });

        var msg = new TextBlock
        {
            Foreground = Fg,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 340,
            Margin = new Thickness(0, 0, 0, 10)
        };
        msg.Inlines.Add("Слово ");
        msg.Inlines.Add(new Run($"«{word}»") { FontWeight = FontWeights.SemiBold });
        msg.Inlines.Add(" вы часто возвращаете. Что с ним сделать?");
        root.Children.Add(msg);

        var buttons = new WrapPanel { MaxWidth = 360 };

        // Сменить раскладку одним кликом (правило word -> противоположная раскладка),
        // если слово целиком из букв одной раскладки. Как у Punto Switcher.
        string? other = LayoutMap.ToOtherLayout(word);
        if (other != null)
            buttons.Children.Add(MakeButton($"Менять на «{other}»", SuggestionChoice.LayoutRule,
                "Всегда менять это слово на вариант в другой раскладке (создаёт правило в один клик)."));

        buttons.Children.Add(MakeButton("Не трогать это слово", SuggestionChoice.StopWord,
            "Никогда не трогать это слово: ни смены раскладки, ни исправления опечатки, ни правил."));
        buttons.Children.Add(MakeButton("Правило…", SuggestionChoice.Rule,
            "Задать произвольную замену (X = Y) в диалоге."));
        buttons.Children.Add(MakeButton("Закрыть", SuggestionChoice.Dismiss,
            "Ничего не делать сейчас."));
        root.Children.Add(buttons);

        Content = root;

        Loaded += OnLoadedPlaceNearCursor;
        Loaded += OnLoadedInstallOutsideClick;
        Closed += OnClosedUninstallHook;
    }

    private void OnLoadedInstallOutsideClick(object sender, RoutedEventArgs e)
    {
        _outsideClickHook = new MouseHook();
        _outsideClickHook.ButtonDown += OnGlobalMouseDown;
        // Без хука окно закрывается только кнопками - не повод ронять программу.
        try { _outsideClickHook.Install(); }
        catch (Exception ex) { RuType.Core.Log.Line($"suggestion window: {ex.Message}"); }
    }

    private void OnClosedUninstallHook(object? sender, EventArgs e)
    {
        if (_outsideClickHook == null) return;
        _outsideClickHook.ButtonDown -= OnGlobalMouseDown;
        _outsideClickHook.Dispose();
        _outsideClickHook = null;
    }

    private bool _dismissing;

    private void OnGlobalMouseDown()
    {
        // Клик по физическим координатам курсора. Сравниваем с рамкой окна в тех же
        // физических пикселях (GetWindowRect), чтобы не возиться с DPI. Внутри - клик
        // по кнопке отработает сам; снаружи - закрываем (клик проходит в приложение).
        IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        if (!NativeMethods.GetCursorPos(out var p) || !NativeMethods.GetWindowRect(hwnd, out var r)) return;

        bool inside = p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom;
        if (inside || _dismissing) return;
        _dismissing = true;

        // Как кнопка "Закрыть": отмечаем показ обработанным (сброс счётчика откатов),
        // иначе окно тут же выскочит снова при следующем откате.
        Dispatcher.BeginInvoke(() =>
        {
            try { Chosen?.Invoke(SuggestionChoice.Dismiss); Close(); }
            catch { /* уже закрыто */ }
        });
    }

    private void OnLoadedPlaceNearCursor(object sender, RoutedEventArgs e)
    {
        // Физические пиксели курсора -> единицы WPF с учётом DPI.
        double dpiX = 1, dpiY = 1;
        var src = PresentationSource.FromVisual(this);
        if (src?.CompositionTarget != null)
        {
            dpiX = src.CompositionTarget.TransformToDevice.M11;
            dpiY = src.CompositionTarget.TransformToDevice.M22;
        }

        double x = _cursorX / dpiX + 12;
        double y = _cursorY / dpiY + 16;

        var wa = SystemParameters.WorkArea;
        if (x + ActualWidth > wa.Right) x = wa.Right - ActualWidth - 4;
        if (y + ActualHeight > wa.Bottom) y = _cursorY / dpiY - ActualHeight - 8; // над курсором
        if (x < wa.Left) x = wa.Left + 4;
        if (y < wa.Top) y = wa.Top + 4;

        Left = x;
        Top = y;
    }

    private Button MakeButton(string text, SuggestionChoice choice, string tooltip)
    {
        var b = new Button
        {
            Content = text,
            Style = _btnStyle,
            Margin = new Thickness(0, 0, 6, 6),
            ToolTip = tooltip,
            Cursor = System.Windows.Input.Cursors.Hand
        };
        b.Click += (_, _) =>
        {
            Chosen?.Invoke(choice);
            Close();
        };
        return b;
    }

    private static Style BuildButtonStyle()
    {
        var border = new FrameworkElementFactory(typeof(Border), "bd");
        border.SetValue(Border.BackgroundProperty, BtnBg);
        border.SetValue(Border.BorderBrushProperty, Accent);
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
        border.SetValue(Border.PaddingProperty, new Thickness(11, 5, 11, 5));

        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        cp.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        cp.SetValue(TextElement.ForegroundProperty, Fg);
        border.AppendChild(cp);

        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };

        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, BtnHover, "bd"));
        template.Triggers.Add(hover);

        var pressed = new Trigger { Property = ButtonBase_IsPressed(), Value = true };
        pressed.Setters.Add(new Setter(Border.BackgroundProperty, BtnPressed, "bd"));
        template.Triggers.Add(pressed);

        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        style.Setters.Add(new Setter(Control.ForegroundProperty, Fg));
        style.Setters.Add(new Setter(Control.FontSizeProperty, 12.0));
        return style;
    }

    private static System.Windows.DependencyProperty ButtonBase_IsPressed()
        => System.Windows.Controls.Primitives.ButtonBase.IsPressedProperty;
}
