using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using static RuType.Interop.NativeMethods;

namespace RuType.Interop;

/// <summary>
/// Определение исключённого контекста: активное приложение в чёрном списке, окно
/// процесса с более высоким уровнем целостности или фокус в поле пароля. В таком
/// контексте программа не трогает (и не логирует) ввод.
///
/// Поле пароля определяется тремя путями:
/// 1. ES_PASSWORD у нативного Edit - на каждую клавишу (IsPasswordFast);
/// 2. подписка на смену фокуса UIA (фоновый поток UIA): флаг "фокус в пароле"
///    для браузеров/Electron, тоже читается на каждую клавишу за O(1);
/// 3. прямой запрос UIA FocusedElement.IsPassword на границе слова
///    (IsPasswordThorough) - страховка, если событие не пришло.
/// Результат запроса 3 кешируется, но кеш сбрасывается всем, что может увести
/// фокус: событием UIA, Tab/Enter, кликом мыши (InvalidateFocusCache).
/// </summary>
public sealed class ExclusionManager : IDisposable
{
    private volatile HashSet<string> _blacklist = new(StringComparer.OrdinalIgnoreCase);

    // Тестовые швы (боевые значения - Win32/UIA). SelfTest подменяет их, чтобы
    // проверить логику кеша без реальных окон.
    public Func<IntPtr> ForegroundWindow { get; set; } = GetForegroundWindow;
    public Func<IntPtr> FocusedControl { get; set; } = GetFocusedControl;
    public Func<IntPtr, bool> NativePasswordCheck { get; set; } = IsNativePasswordEdit;
    public Func<bool> UiaFocusedIsPassword { get; set; } = QueryUiaFocusedIsPassword;
    public Func<IntPtr, bool> IsOwnProcessWindow { get; set; } = OwnProcessWindow;
    public Func<long> Clock { get; set; } = () => Environment.TickCount64;

    /// <summary>Сколько раз выполнялся прямой запрос UIA (для самопроверки).</summary>
    public int UiaQueryCount { get; private set; }

    public void SetBlacklist(IEnumerable<string> apps)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in apps)
        {
            var name = a.Trim();
            if (name.Length == 0) continue;
            set.Add(name);
            if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                set.Add(name + ".exe");
        }
        _blacklist = set;
    }

    /// <summary>Процесс активного окна входит в чёрный список.</summary>
    public bool IsBlacklistedApp(IntPtr foreground)
    {
        var blacklist = _blacklist;
        if (blacklist.Count == 0 || foreground == IntPtr.Zero) return false;
        try
        {
            GetWindowThreadProcessId(foreground, out uint pid);
            if (pid == 0) return false;

            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return false;
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                if (!QueryFullProcessImageName(h, 0, sb, ref size)) return false;
                string exe = System.IO.Path.GetFileName(sb.ToString());
                return blacklist.Contains(exe);
            }
            finally
            {
                CloseHandle(h);
            }
        }
        catch
        {
            return false;
        }
    }

    // --- уровень целостности (UIPI) ---

    private static readonly Lazy<uint> OwnIntegrity = new(() =>
        TokenIntegrity(GetCurrentProcess()) ?? SECURITY_MANDATORY_MEDIUM_RID);

    /// <summary>Уровень целостности нашего процесса (RID: 0x2000 medium, 0x3000 high).</summary>
    public static uint OwnIntegrityLevel => OwnIntegrity.Value;

    /// <summary>
    /// Окно принадлежит процессу с уровнем целостности выше нашего (например, запущенному
    /// от администратора). UIPI не пропустит туда SendInput, а подавленную границу мы
    /// уже не вернём - такой контекст исключается целиком. Не удалось прочитать уровень
    /// (токен закрыт) - тоже исключаем: безопаснее промолчать, чем потерять ввод.
    /// </summary>
    public bool IsAboveOwnIntegrity(IntPtr foreground)
    {
        if (foreground == IntPtr.Zero) return false;
        GetWindowThreadProcessId(foreground, out uint pid);
        if (pid == 0 || pid == (uint)Environment.ProcessId) return false;

        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return true;
        try
        {
            uint? level = TokenIntegrity(h);
            return level == null || level.Value > OwnIntegrity.Value;
        }
        finally
        {
            CloseHandle(h);
        }
    }

    private static uint? TokenIntegrity(IntPtr process)
    {
        if (!OpenProcessToken(process, TOKEN_QUERY, out IntPtr token)) return null;
        IntPtr buf = IntPtr.Zero;
        try
        {
            GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out int len);
            if (len <= 0) return null;
            buf = Marshal.AllocHGlobal(len);
            if (!GetTokenInformation(token, TokenIntegrityLevel, buf, len, out _)) return null;
            // TOKEN_MANDATORY_LABEL { SID_AND_ATTRIBUTES Label } - первым полем указатель на SID.
            IntPtr sid = Marshal.ReadIntPtr(buf);
            byte count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
            if (count == 0) return null;
            return (uint)Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)(count - 1)));
        }
        catch
        {
            return null;
        }
        finally
        {
            if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
            CloseHandle(token);
        }
    }

    // --- поле пароля ---

    // Последнее событие смены фокуса UIA: активное окно в момент события и признак
    // пароля. Пишется потоком UIA, читается потоком хука - неизменяемый объект по ссылке.
    private sealed record FocusState(IntPtr Foreground, bool IsPassword);
    private volatile FocusState? _focusState;
    private long _focusVersion;

    /// <summary>Быстрый детект на каждую клавишу: нативный ES_PASSWORD или флаг из событий UIA.</summary>
    public bool IsPasswordFast()
    {
        try
        {
            IntPtr focus = FocusedControl();
            if (focus != IntPtr.Zero && NativePasswordCheck(focus)) return true;
        }
        catch
        {
            // Win32-детект не сработал - остаётся флаг из событий.
        }

        var s = _focusState;
        return s is { IsPassword: true } && s.Foreground == ForegroundWindow();
    }

    // Кеш прямого UIA-запроса: кросс-процессный COM-вызов дорог (десятки мс в браузерах),
    // а граница слова - в синхронном пути хука. Ключ - (окно, hwnd фокуса, версия событий
    // фокуса) плюс TTL. Одного ключа мало: в браузере hwnd фокуса не меняется при переходе
    // между полями страницы, и закешированное "не пароль" от поля логина жило бы в поле
    // пароля. Поэтому кеш сбрасывается событием UIA, Tab/Enter и кликом (InvalidateFocusCache).
    // Поля ниже трогает только поток хука.
    private bool _uiaValid;
    private IntPtr _uiaForeground;
    private IntPtr _uiaFocus;
    private long _uiaVersion;
    private bool _uiaResult;
    private long _uiaAtMs;
    private const int UiaTtlMs = 1500;

    /// <summary>Широкий детект через UIA (браузеры/Electron). Вызывать на границе слова.</summary>
    public bool IsPasswordThorough()
    {
        if (IsPasswordFast()) return true;

        IntPtr fg = ForegroundWindow();
        // Свои окна (настройки, окно слова) полей пароля не имеют, а UIA-запрос к ним
        // маршалится в наш же UI-поток и ждал бы его, если тот занят.
        if (fg != IntPtr.Zero && IsOwnProcessWindow(fg)) return false;

        IntPtr focus = FocusedControl();
        long version = Interlocked.Read(ref _focusVersion);
        long now = Clock();
        if (_uiaValid && focus != IntPtr.Zero && focus == _uiaFocus && fg == _uiaForeground
            && version == _uiaVersion && now - _uiaAtMs < UiaTtlMs)
            return _uiaResult;

        bool result;
        try
        {
            UiaQueryCount++;
            result = UiaFocusedIsPassword();
        }
        catch
        {
            result = false; // UIA ненадёжен в некоторых окнах - не блокируем ввод
        }

        _uiaValid = true;
        _uiaForeground = fg;
        _uiaFocus = focus;
        _uiaVersion = version;
        _uiaResult = result;
        _uiaAtMs = now;
        return result;
    }

    /// <summary>
    /// Фокус мог уйти (Tab, Enter, клик мышью): следующий IsPasswordThorough спросит UIA
    /// заново, а не вернёт результат, относящийся к прежнему полю.
    /// </summary>
    public void InvalidateFocusCache() => _uiaValid = false;

    /// <summary>Смена фокуса (из события UIA; открыт для самопроверки).</summary>
    public void ReportFocusChanged(IntPtr foreground, bool isPassword)
    {
        _focusState = new FocusState(foreground, isPassword);
        Interlocked.Increment(ref _focusVersion);
    }

    private AutomationFocusChangedEventHandler? _focusHandler;
    private int _trackingStarted;

    /// <summary>
    /// Подписаться на смену фокуса UIA. Регистрация - из фонового (MTA) потока: из
    /// STA-потока UIA-подписки склонны к взаимоблокировкам. Обработчик вызывается на
    /// потоке UIA и хук не задерживает. Повторный вызов ничего не делает.
    /// </summary>
    public void StartFocusTracking()
    {
        if (Interlocked.Exchange(ref _trackingStarted, 1) != 0) return;
        Task.Run(() =>
        {
            try
            {
                _focusHandler = OnUiaFocusChanged;
                Automation.AddAutomationFocusChangedEventHandler(_focusHandler);
                RuType.Core.Log.Line("UIA: отслеживание фокуса включено");
            }
            catch (Exception ex)
            {
                _focusHandler = null;
                RuType.Core.Log.Line($"UIA: отслеживание фокуса не включилось: {ex.Message}");
            }
        });
    }

    private void OnUiaFocusChanged(object sender, AutomationFocusChangedEventArgs e)
    {
        bool isPassword = false;
        try
        {
            if (sender is AutomationElement el)
                isPassword = el.GetCurrentPropertyValue(AutomationElement.IsPasswordProperty) is true;
        }
        catch
        {
            // Элемент уже исчез или UIA не отвечает - считаем "не пароль", прямой запрос
            // на границе слова всё равно выполнится: версия фокуса ниже меняется.
        }
        ReportFocusChanged(GetForegroundWindow(), isPassword);
    }

    public void Dispose()
    {
        var handler = _focusHandler;
        if (handler == null) return;
        _focusHandler = null;
        try
        {
            Task.Run(() => Automation.RemoveAutomationFocusChangedEventHandler(handler))
                .Wait(TimeSpan.FromMilliseconds(500));
        }
        catch
        {
            // Выход из программы - подписка умрёт вместе с процессом.
        }
    }

    // --- боевые реализации швов ---

    private static bool IsNativePasswordEdit(IntPtr focus)
    {
        var sb = new StringBuilder(64);
        GetClassName(focus, sb, sb.Capacity);
        if (sb.ToString().IndexOf("Edit", StringComparison.OrdinalIgnoreCase) < 0) return false;
        long style = GetWindowLongPtr(focus, GWL_STYLE).ToInt64();
        return (style & ES_PASSWORD) != 0;
    }

    private static bool QueryUiaFocusedIsPassword()
    {
        AutomationElement? focused = AutomationElement.FocusedElement;
        return focused != null
            && focused.GetCurrentPropertyValue(AutomationElement.IsPasswordProperty) is true;
    }

    private static bool OwnProcessWindow(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out uint pid);
        return pid == (uint)Environment.ProcessId;
    }

    private static IntPtr GetFocusedControl()
    {
        var gti = new GUITHREADINFO();
        gti.cbSize = Marshal.SizeOf<GUITHREADINFO>();
        uint tid = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        if (tid == 0) return IntPtr.Zero;
        if (!GetGUIThreadInfo(tid, ref gti)) return IntPtr.Zero;
        return gti.hwndFocus;
    }
}
