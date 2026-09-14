using static RuType.Interop.NativeMethods;

namespace RuType.Interop;

/// <summary>
/// Определение установленных раскладок и переключение языка ввода для окна
/// в фокусе. В версии 1 поддерживается только пара ru &lt;-&gt; en (ТЗ, раздел 7):
/// автопереключение активно, лишь если в системе есть обе раскладки.
/// </summary>
public sealed class LayoutSwitcher
{
    private const ushort LANGID_RU = 0x0419;
    private const ushort LANGID_EN = 0x0409;

    public IntPtr RuLayout { get; private set; } = IntPtr.Zero;
    public IntPtr EnLayout { get; private set; } = IntPtr.Zero;

    /// <summary>В системе есть и русская, и английская раскладки.</summary>
    public bool BothPresent => RuLayout != IntPtr.Zero && EnLayout != IntPtr.Zero;

    /// <summary>
    /// Перечитать раскладки системы. Каждый вызов определяет обе заново: раскладку
    /// могли не только добавить, но и удалить - старый HKL не должен переживать
    /// повторный Detect (иначе BothPresent врёт). Свойства присваиваются в конце,
    /// одним шагом каждое: их читает поток хука, промежуточный ноль ему не виден.
    /// </summary>
    public void Detect()
    {
        IntPtr ru = IntPtr.Zero, en = IntPtr.Zero;
        uint count = GetKeyboardLayoutList(0, null);
        if (count > 0)
        {
            var list = new IntPtr[count];
            uint got = GetKeyboardLayoutList((int)count, list);
            for (int i = 0; i < got && i < list.Length; i++)
            {
                IntPtr hkl = list[i];
                // Младшее слово HKL - LANGID активной раскладки.
                ushort langId = (ushort)(hkl.ToInt64() & 0xFFFF);
                if (langId == LANGID_RU && ru == IntPtr.Zero) ru = hkl;
                else if (langId == LANGID_EN && en == IntPtr.Zero) en = hkl;
            }
        }
        RuLayout = ru;
        EnLayout = en;
    }

    /// <summary>Переключает раскладку окна в фокусе на указанную.</summary>
    public void Activate(IntPtr hkl)
    {
        if (hkl == IntPtr.Zero) return;
        IntPtr hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return;
        PostMessage(hwnd, WM_INPUTLANGCHANGEREQUEST, (IntPtr)INPUTLANGCHANGE_SYSCHARSET, hkl);
    }
}
