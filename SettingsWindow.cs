using System.Globalization;
using System.Runtime.InteropServices;
using static ActiveBorder.NativeMethods;

namespace ActiveBorder;

internal sealed class SettingsWindow : IDisposable
{
	private const string CLASS_NAME = "ActiveBorderSettings";
	private const string WINDOW_TITLE = "ActiveBorder Settings";
	private const int WS_CHILD = 0x40000000;
	private const int WS_VISIBLE = 0x10000000;
	private const int WS_TABSTOP = 0x00010000;
	private const int WS_BORDER = 0x00800000;
	private const int WS_CAPTION = 0x00C00000;
	private const int WS_SYSMENU = 0x00080000;
	private const int WS_EX_DLGMODALFRAME = 0x00000001;
	private const int BS_AUTOCHECKBOX = 0x00000003;
	private const int BS_DEFPUSHBUTTON = 0x00000001;
	private const int ES_NUMBER = 0x00002000;
	private const int UDS_ALIGNRIGHT = 0x0004;
	private const int UDS_SETBUDDYINT = 0x0002;
	private const int UDS_ARROWKEYS = 0x0020;
	private const uint WM_CREATE = 0x0001;
	private const uint WM_COMMAND = 0x0111;
	private const uint WM_CLOSE = 0x0010;
	private const uint WM_DPICHANGED = 0x02E0;
	private const uint WM_SETFONT = 0x0030;
	private const uint BM_GETCHECK = 0x00F0;
	private const uint BM_SETCHECK = 0x00F1;
	private const uint BM_CLICK = 0x00F5;
	private const uint UDM_SETRANGE32 = 0x046F;
	private const uint UDM_SETBUDDY = 0x0469;
	private const int BST_CHECKED = 1;
	private const int DEFAULT_GUI_FONT = 17;
	private const int WS_EX_TOOLWINDOW = 0x00000080;

	private const int COLOR_BTNFACE = 15;
	private const int SW_HIDE = 0;
	private const int SW_SHOW = 5;
	private const int SWP_NOZORDER = 0x0004;
	private const int SWP_NOACTIVATE = 0x0010;
	private const int ICC_UPDOWN_CLASS = 0x00000010;
	private const uint CC_RGBINIT = 0x00000001;
	private const uint CC_FULLOPEN = 0x00000002;
	private const int ID_SAVE = 1;
	private const int ID_CANCEL = 2;
	private const int ID_FOLLOW = 101;
	private const int ID_COLOR = 102;
	private const int ID_COLOR_PREVIEW = 103;
	private const int ID_WIDTH = 104;
	private const int ID_SPIN = 105;
	private const int ID_STARTUP = 106;

	private readonly IntPtr _owner;
	private readonly WindowProc _windowProc;
	private readonly Dictionary<int, IntPtr> _controls = new();
	private readonly uint[] _customColors = new uint[16];

	private ushort _classAtom;
	private IntPtr _window;
	private SettingsController? _controller;
	private uint _customColor = BorderSettings.Default.CustomColor;
	private bool _disposed;

	public SettingsWindow(IntPtr owner)
	{
		_owner = owner;
		_windowProc = WindowProcHandler;
		InitializeCommonControls();
		RegisterWindowClass();
	}

	public void AttachController(SettingsController controller)
	{
		ArgumentNullException.ThrowIfNull(controller);
		_controller = controller;
	}

	public void Open(BorderSettings settings, bool startupEnabled)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		ArgumentNullException.ThrowIfNull(settings);
		EnsureWindowCreated();

		_customColor = settings.CustomColor;
		SetChecked(ID_FOLLOW, settings.FollowSystemAccent);
		SetChecked(ID_STARTUP, startupEnabled);
		SetText(ID_WIDTH, settings.BorderWidth.ToString(CultureInfo.InvariantCulture));
		UpdateColorControls(settings.FollowSystemAccent);

		ShowWindow(_window, SW_SHOW);
		SetForegroundWindow(_window);
		SetFocus(Control(ID_FOLLOW));
	}

	public bool TryTranslateMessage(ref MSG message) =>
		_window != IntPtr.Zero && IsWindowVisible(_window) && IsDialogMessageW(_window, ref message);

	public void Dispose()
	{
		if (_disposed)
			return;
		_disposed = true;

		if (_window != IntPtr.Zero)
		{
			DestroyWindow(_window);
			_window = IntPtr.Zero;
		}
		if (_classAtom != 0)
		{
			UnregisterClassW(CLASS_NAME, GetModuleHandleW(null));
			_classAtom = 0;
		}
	}

	private void InitializeCommonControls()
	{
		var controls = new INITCOMMONCONTROLSEX
		{
			Size = (uint)Marshal.SizeOf<INITCOMMONCONTROLSEX>(),
			Classes = ICC_UPDOWN_CLASS,
		};
		if (!InitCommonControlsEx(ref controls))
			throw new InvalidOperationException("InitCommonControlsEx failed: " + Marshal.GetLastWin32Error());
	}

	private void RegisterWindowClass()
	{
		var windowClass = new WNDCLASSEXW
		{
			CbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
			LpfnWndProc = Marshal.GetFunctionPointerForDelegate(_windowProc),
			HInstance = GetModuleHandleW(null),

			HbrBackground = GetSysColorBrush(COLOR_BTNFACE),
			LpszClassName = CLASS_NAME,
		};
		_classAtom = RegisterClassExW(ref windowClass);
		if (_classAtom == 0)
			throw new InvalidOperationException("RegisterClassEx failed for the settings window: " + Marshal.GetLastWin32Error());
	}

	private void EnsureWindowCreated()
	{
		if (_window != IntPtr.Zero)
			return;

		_window = CreateWindowExW(
			WS_EX_TOOLWINDOW | WS_EX_DLGMODALFRAME,
			CLASS_NAME,
			WINDOW_TITLE,
			WS_CAPTION | WS_SYSMENU,
			unchecked((int)0x80000000),
			unchecked((int)0x80000000),
			460,
			330,
			_owner,
			IntPtr.Zero,
			GetModuleHandleW(null),
			IntPtr.Zero);
		if (_window == IntPtr.Zero)
			throw new InvalidOperationException("CreateWindowEx failed for the settings window: " + Marshal.GetLastWin32Error());
	}

	private void CreateControls(IntPtr parent)
	{
		CreateControl("Button", "Follow Windows accent color", WS_TABSTOP | BS_AUTOCHECKBOX, 16, 16, 360, 24, ID_FOLLOW);
		CreateControl("Static", "Border color:", 0, 16, 55, 90, 22, 0);
		CreateControl("Button", "Choose color...", WS_TABSTOP, 112, 51, 118, 26, ID_COLOR);
		CreateControl("Static", string.Empty, WS_BORDER, 244, 53, 150, 23, ID_COLOR_PREVIEW);
		CreateControl("Static", "Border width (1-20 px):", 0, 16, 96, 180, 22, 0);
		CreateControl("Edit", "5", WS_BORDER | WS_TABSTOP | ES_NUMBER, 198, 92, 48, 24, ID_WIDTH);
		var spin = CreateControl("msctls_updown32", string.Empty, WS_TABSTOP | UDS_ALIGNRIGHT | UDS_SETBUDDYINT | UDS_ARROWKEYS, 230, 92, 16, 24, ID_SPIN);
		SendMessageW(spin, UDM_SETRANGE32, new IntPtr(1), new IntPtr(20));
		SendMessageW(spin, UDM_SETBUDDY, Control(ID_WIDTH), IntPtr.Zero);
		CreateControl("Button", "Start with Windows", WS_TABSTOP | BS_AUTOCHECKBOX, 16, 139, 360, 24, ID_STARTUP);
		CreateControl("Button", "Save", WS_TABSTOP | BS_DEFPUSHBUTTON, 260, 250, 80, 30, ID_SAVE);
		CreateControl("Button", "Cancel", WS_TABSTOP, 348, 250, 80, 30, ID_CANCEL);
		UpdateColorControls(followSystemAccent: true);
	}

	private IntPtr CreateControl(string className, string text, int style, int x, int y, int width, int height, int id)
	{
		var control = CreateWindowExW(
			0,
			className,
			text,
			WS_CHILD | WS_VISIBLE | style,
			x,
			y,
			width,
			height,
			_window,
			new IntPtr(id),
			GetModuleHandleW(null),
			IntPtr.Zero);
		if (control == IntPtr.Zero)
			throw new InvalidOperationException($"Failed to create settings control '{className}': {Marshal.GetLastWin32Error()}");

		if (id != 0)
			_controls[id] = control;
		SendMessageW(control, WM_SETFONT, GetStockObject(DEFAULT_GUI_FONT), new IntPtr(1));
		return control;
	}

	private IntPtr WindowProcHandler(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
	{
		try
		{
			switch (message)
			{
				case WM_CREATE:
					_window = hwnd;
					CreateControls(hwnd);
					return IntPtr.Zero;

				case WM_COMMAND:
					HandleCommand((int)(wParam.ToInt64() & 0xFFFF));
					return IntPtr.Zero;

				case WM_CLOSE:
					ShowWindow(hwnd, SW_HIDE);
					return IntPtr.Zero;

				case WM_DPICHANGED:
					var suggested = Marshal.PtrToStructure<RECT>(lParam);
					SetWindowPos(hwnd, IntPtr.Zero, suggested.Left, suggested.Top, suggested.Width, suggested.Height, SWP_NOZORDER | SWP_NOACTIVATE);
					return IntPtr.Zero;
			}
		}
		catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or ExternalException)
		{
			MessageBoxW(hwnd, ex.Message, WINDOW_TITLE, MB_ICONERROR);
			if (message == WM_CREATE)
				return new IntPtr(-1);
		}

		return DefWindowProcW(hwnd, message, wParam, lParam);
	}

	private void HandleCommand(int id)
	{
		switch (id)
		{
			case ID_FOLLOW:
				UpdateColorControls(IsChecked(ID_FOLLOW));
				break;
			case ID_COLOR:
				ChooseCustomColor();
				break;
			case ID_SAVE:
				SaveDraft();
				break;
			case ID_CANCEL:
				ShowWindow(_window, SW_HIDE);
				break;
		}
	}

	private void SaveDraft()
	{
		if (_controller is null)
		{
			MessageBoxW(_window, "设置控制器尚未就绪。", WINDOW_TITLE, MB_ICONERROR);
			return;
		}

		if (!int.TryParse(GetText(ID_WIDTH), NumberStyles.None, CultureInfo.InvariantCulture, out var width) || width is < 1 or > 20)
		{
			MessageBoxW(_window, "边框宽度必须是 1 到 20 之间的整数。", WINDOW_TITLE, MB_ICONERROR);
			SetFocus(Control(ID_WIDTH));
			return;
		}

		var settings = BorderSettings.Create(IsChecked(ID_FOLLOW), _customColor, width);
		var error = _controller.Save(settings, IsChecked(ID_STARTUP));
		if (error is null)
		{
			ShowWindow(_window, SW_HIDE);
			return;
		}

		Open(_controller.CurrentSettings, _controller.ReadStartupEnabled());
		MessageBoxW(_window, error, WINDOW_TITLE, MB_ICONERROR);
	}

	private void ChooseCustomColor()
	{
		var color = _customColor;
		var chooseColor = new CHOOSECOLORW
		{
			Size = (uint)Marshal.SizeOf<CHOOSECOLORW>(),
			Owner = _window,
			InitialColor = ToColorRef(color),
			Flags = CC_RGBINIT | CC_FULLOPEN,
		};
		var pinnedColors = GCHandle.Alloc(_customColors, GCHandleType.Pinned);
		try
		{
			chooseColor.CustomColors = pinnedColors.AddrOfPinnedObject();
			if (!ChooseColorW(ref chooseColor))
				return;

			_customColor = FromColorRef(chooseColor.InitialColor);
			UpdateColorControls(IsChecked(ID_FOLLOW));
		}
		finally
		{
			pinnedColors.Free();
		}
	}

	private void UpdateColorControls(bool followSystemAccent)
	{
		if (Control(ID_COLOR) == IntPtr.Zero)
			return;
		EnableWindow(Control(ID_COLOR), !followSystemAccent);
		var color = followSystemAccent ? AccentColor.Current : _customColor;
		var red = (color >> 16) & 0xFF;
		var green = (color >> 8) & 0xFF;
		var blue = color & 0xFF;
		SetWindowTextW(Control(ID_COLOR_PREVIEW), $"#{red:X2}{green:X2}{blue:X2}");
	}

	private void SetChecked(int id, bool value) => SendMessageW(Control(id), BM_SETCHECK, new IntPtr(value ? BST_CHECKED : 0), IntPtr.Zero);
	private bool IsChecked(int id) => SendMessageW(Control(id), BM_GETCHECK, IntPtr.Zero, IntPtr.Zero).ToInt64() == BST_CHECKED;
	private void SetText(int id, string value) => SetWindowTextW(Control(id), value);

	private string GetText(int id)
	{
		var buffer = new System.Text.StringBuilder(32);
		GetWindowTextW(Control(id), buffer, buffer.Capacity);
		return buffer.ToString();
	}

	private IntPtr Control(int id) => _controls.TryGetValue(id, out var control) ? control : IntPtr.Zero;

	private static uint ToColorRef(uint argb) => ((argb & 0xFF) << 16) | (argb & 0x00FF00) | ((argb >> 16) & 0xFF);
	private static uint FromColorRef(uint colorRef) => 0xFF000000 | ((colorRef & 0xFF) << 16) | (colorRef & 0x00FF00) | ((colorRef >> 16) & 0xFF);

	[DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern IntPtr CreateWindowExW(int exStyle, string className, string? windowName, int style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
	[DllImport("user32.dll")]
	private static extern bool IsWindowVisible(IntPtr hwnd);
	[DllImport("user32.dll")]
	private static extern bool IsDialogMessageW(IntPtr hwnd, ref MSG message);
	[DllImport("user32.dll")]
	private static extern bool SetForegroundWindow(IntPtr hwnd);
	[DllImport("user32.dll")]
	private static extern IntPtr SetFocus(IntPtr hwnd);
	[DllImport("user32.dll")]
	private static extern bool EnableWindow(IntPtr hwnd, bool enabled);
	[DllImport("user32.dll")]
	private static extern IntPtr SendMessageW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern bool SetWindowTextW(IntPtr hwnd, string text);
	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern int GetWindowTextW(IntPtr hwnd, System.Text.StringBuilder text, int maxCount);
	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, int flags);
	[DllImport("comctl32.dll", SetLastError = true)]
	private static extern bool InitCommonControlsEx(ref INITCOMMONCONTROLSEX controls);
	[DllImport("user32.dll")]
	private static extern IntPtr GetSysColorBrush(int index);
	[DllImport("gdi32.dll")]
	private static extern IntPtr GetStockObject(int objectIndex);
	[DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern bool ChooseColorW(ref CHOOSECOLORW chooseColor);

	[UnmanagedFunctionPointer(CallingConvention.Winapi)]
	private delegate IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

	[StructLayout(LayoutKind.Sequential)]
	private struct INITCOMMONCONTROLSEX
	{
		public uint Size;
		public uint Classes;
	}

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct CHOOSECOLORW
	{
		public uint Size;
		public IntPtr Owner;
		public IntPtr Instance;
		public uint InitialColor;
		public IntPtr CustomColors;
		public uint Flags;
		public IntPtr UserData;
		public IntPtr Hook;
		public IntPtr TemplateName;
	}
}
