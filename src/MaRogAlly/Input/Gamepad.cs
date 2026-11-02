using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace MaRogAlly.Input;

/// <summary>Actions de navigation, communes à la manette et au clavier.</summary>
public enum NavAction
{
    Up, Down, Left, Right,
    Accept,      // A
    Back,        // B
    Option,      // X : filtres
    Secondary,   // Y : tri (bibliothèque)
    PrevTab,     // LB
    NextTab,     // RB
    PrevLetter,  // LT
    NextLetter,  // RT
    Menu,        // Start : menu système
}

/// <summary>
/// Lit la manette par XInput (la manette intégrée de la ROG Ally est vue comme une manette Xbox)
/// et émet des <see cref="NavAction"/> avec répétition automatique quand on maintient une direction.
/// </summary>
public sealed class Gamepad : IDisposable
{
    public event Action<NavAction>? Action;
    public event Action<bool>? ConnectionChanged;
    /// <summary>Select pressé alors que l'application n'est pas au premier plan : revenir dessus.</summary>
    public event Action? ReturnRequested;
    /// <summary>B pressé alors que l'application n'est pas au premier plan : réduire celle qui l'est.</summary>
    public event Action? MinimizeRequested;

    /// <summary>Ne lire la manette que si cette condition est vraie (ex. fenêtre au premier plan).</summary>
    public Func<bool> IsActive { get; set; } = () => true;

    public bool IsConnected { get; private set; }

    private readonly DispatcherTimer _timer;
    private int _slot = -1;
    private int _probeCountdown;
    private ushort _previousButtons;
    private bool _previousLt, _previousRt;
    private NavAction? _heldDirection;
    private DateTime _nextRepeat;
    private int _repeatCount;
    private bool _wasActive;

    private const short StickThreshold = 16000;
    private const byte TriggerThreshold = 100;
    private static readonly TimeSpan FirstRepeat = TimeSpan.FromMilliseconds(380);

    public Gamepad()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += (_, _) => Poll();
    }

    public void Start() => _timer.Start();
    public void Dispose() => _timer.Stop();

    private void Poll()
    {
        if (!FindController(out var state)) return;

        var active = IsActive();
        var pad = state.Gamepad;
        if (!active || !_wasActive)
        {
            // En arrière-plan (ou au retour), on ignore les boutons déjà enfoncés pour ne pas
            // déclencher d'action avec un appui fait dans le jeu.
            var newlyPressed = (ushort)(pad.Buttons & ~_previousButtons);
            if (!active && Has(newlyPressed, Buttons.Back)) ReturnRequested?.Invoke();
            if (!active && Has(newlyPressed, Buttons.B)) MinimizeRequested?.Invoke();
            _previousButtons = pad.Buttons;
            _previousLt = pad.LeftTrigger > TriggerThreshold;
            _previousRt = pad.RightTrigger > TriggerThreshold;
            _heldDirection = null;
            _wasActive = active;
            return;
        }

        var pressed = (ushort)(pad.Buttons & ~_previousButtons);
        _previousButtons = pad.Buttons;

        if (Has(pressed, Buttons.A)) Raise(NavAction.Accept);
        if (Has(pressed, Buttons.B)) Raise(NavAction.Back);
        if (Has(pressed, Buttons.X)) Raise(NavAction.Option);
        if (Has(pressed, Buttons.Y)) Raise(NavAction.Secondary);
        if (Has(pressed, Buttons.LeftShoulder)) Raise(NavAction.PrevTab);
        if (Has(pressed, Buttons.RightShoulder)) Raise(NavAction.NextTab);
        if (Has(pressed, Buttons.Start)) Raise(NavAction.Menu);

        var lt = pad.LeftTrigger > TriggerThreshold;
        var rt = pad.RightTrigger > TriggerThreshold;
        if (lt && !_previousLt) Raise(NavAction.PrevLetter);
        if (rt && !_previousRt) Raise(NavAction.NextLetter);
        _previousLt = lt;
        _previousRt = rt;

        HandleDirection(CurrentDirection(pad));
    }

    private void HandleDirection(NavAction? dir)
    {
        var now = DateTime.UtcNow;
        if (dir != _heldDirection)
        {
            _heldDirection = dir;
            _repeatCount = 0;
            if (dir is { } d)
            {
                Raise(d);
                _nextRepeat = now + FirstRepeat;
            }
            return;
        }
        if (dir is { } held && now >= _nextRepeat)
        {
            Raise(held);
            _repeatCount++;
            // La répétition accélère quand on maintient longtemps.
            _nextRepeat = now + TimeSpan.FromMilliseconds(_repeatCount < 6 ? 120 : 60);
        }
    }

    private static NavAction? CurrentDirection(XInputGamepad pad)
    {
        if (Has(pad.Buttons, Buttons.DPadUp)) return NavAction.Up;
        if (Has(pad.Buttons, Buttons.DPadDown)) return NavAction.Down;
        if (Has(pad.Buttons, Buttons.DPadLeft)) return NavAction.Left;
        if (Has(pad.Buttons, Buttons.DPadRight)) return NavAction.Right;

        int x = pad.ThumbLX, y = pad.ThumbLY;
        if (Math.Abs(x) < StickThreshold && Math.Abs(y) < StickThreshold) return null;
        if (Math.Abs(x) > Math.Abs(y)) return x > 0 ? NavAction.Right : NavAction.Left;
        return y > 0 ? NavAction.Up : NavAction.Down;
    }

    private bool FindController(out XInputState state)
    {
        state = default;
        if (_slot >= 0 && XInputGetState(_slot, out state) == 0) return true;

        if (_slot >= 0)
        {
            _slot = -1;
            SetConnected(false);
        }
        // Chercher une manette coûte cher quand aucune n'est branchée : une fois par seconde suffit.
        if (_probeCountdown-- > 0) return false;
        _probeCountdown = 60;
        for (var i = 0; i < 4; i++)
        {
            if (XInputGetState(i, out state) != 0) continue;
            _slot = i;
            _previousButtons = state.Gamepad.Buttons;
            SetConnected(true);
            return true;
        }
        return false;
    }

    private void SetConnected(bool value)
    {
        if (IsConnected == value) return;
        IsConnected = value;
        ConnectionChanged?.Invoke(value);
    }

    private void Raise(NavAction a) => Action?.Invoke(a);

    private static bool Has(ushort buttons, Buttons b) => (buttons & (ushort)b) != 0;

    [Flags]
    private enum Buttons : ushort
    {
        DPadUp = 0x0001, DPadDown = 0x0002, DPadLeft = 0x0004, DPadRight = 0x0008,
        Start = 0x0010, Back = 0x0020, LeftThumb = 0x0040, RightThumb = 0x0080,
        LeftShoulder = 0x0100, RightShoulder = 0x0200,
        A = 0x1000, B = 0x2000, X = 0x4000, Y = 0x8000,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputGamepad
    {
        public ushort Buttons;
        public byte LeftTrigger, RightTrigger;
        public short ThumbLX, ThumbLY, ThumbRX, ThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState
    {
        public uint PacketNumber;
        public XInputGamepad Gamepad;
    }

    [DllImport("xinput1_4.dll")]
    private static extern int XInputGetState(int userIndex, out XInputState state);
}
