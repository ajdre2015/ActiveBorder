using System.Runtime.InteropServices;
using static ActiveBorder.NativeMethods;
// ReSharper disable CommentTypo

namespace ActiveBorder;

/**
 * The focus border itself: four thin, layered, click-through popup windows
 * forming a rectangle around the target.
 *
 * Four strips rather than one big frame-shaped window, because a single overlay
 * covering the whole target would need an UpdateLayeredWindow bitmap the size
 * of that window (~33 MB for a maximized 4K window) and a same-sized DWM
 * redirection surface. Four strips only ever need two small solid bitmaps,
 * reused until the accent colour or the desktop size changes, and they leave
 * the interior of the target completely untouched rather than merely
 * transparent.
 */
internal sealed class OverlayWindow : IDisposable
{
	/**
	 * Border thickness in physical pixels. Fixed rather than configurable:
	 * the process is per-monitor-v2 DPI aware, so this is five real pixels
	 * on every monitor whatever its scaling.
	 */

	private const string CLASS_NAME = "ActiveBorderOverlay";

	private const int TOP = 0, BOTTOM = 1, LEFT = 2, RIGHT = 3;

	// The walk has to clear four strips plus the invisible helper
	// windows (IME hosts and friends) interleaved among them.
	private const int Z_ORDER_PROBE_LIMIT = 32;

	private readonly IntPtr[] _edges = new IntPtr[4];
	private readonly RECT[] _edgeRects = new RECT[4];

	// Outer rectangle of the border as last drawn; used to decide
	// whether another window actually overlaps it.
	private RECT _bounds;

	// Two solid-colour DIB sections, each permanently selected into its own
	// memory DC: one long-and-short for the horizontal edges, one
	// short-and-tall for the vertical edges. UpdateLayeredWindow is happy to
	// take a source bitmap larger than the destination window. They are only
	// rebuilt when the desktop grows or the accent colour changes.
	private IntPtr _horizontalDc, _horizontalBitmap, _horizontalOldBitmap;
	private IntPtr _verticalDc, _verticalBitmap, _verticalOldBitmap;
	private int _stripLength;

	// The accent colour the bitmaps above were filled with.
	private uint _color;

	private int _width = 5;

	private bool _visible;
	private bool _topmost;
	private bool _disposed;

	// The window procedure is handed to Win32 inside a WNDCLASSEX, so the
	// delegate must outlive every window of the class. A static field keeps
	// it rooted; a local would be collected and the next message would fault.
	private static readonly WndProc _sWndProc = OverlayWndProc;
	private static ushort _sClassAtom;

	internal OverlayWindow()
	{
		var instance = GetModuleHandleW(null);
		EnsureClassRegistered(instance);
		_color = AccentColor.Current;
		CreateStripBitmaps();

		for (var i = 0; i < _edges.Length; i++)
		{
			// Layered for per-pixel alpha via UpdateLayeredWindow, transparent so
			// hit-testing falls through to whatever is below, no-activate so a
			// click never activates it, and tool-window to stay out of the taskbar
			// and Alt-Tab. WS_POPUP: no caption, no border, no owner.
			_edges[i] = CreateWindowExW(WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW, CLASS_NAME, null, WS_POPUP, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);

			if (_edges[i] == IntPtr.Zero)
				throw new InvalidOperationException($"CreateWindowEx failed for the overlay: {Marshal.GetLastWin32Error()}");
		}
	}

	/** True if this handle belongs to the overlay. */
	internal bool Owns(IntPtr hwnd) => hwnd == _edges[0] || hwnd == _edges[1] || hwnd == _edges[2] || hwnd == _edges[3];

	/**
     * Draw the border on the outermost Thickness pixels inside bounds,
     * directly above target in the z-order. bounds is the visible frame of
     * the target, in physical pixels on the virtual screen.
     *
     * Z-order is only re-applied when reassertZOrder is set, because a
     * SetWindowPos that moves windows in the z-order on every mouse-move of a
     * drag is both wasteful and a source of flicker.
     */
	internal void Show(IntPtr target, in RECT bounds, bool reassertZOrder)
	{
		// A window narrower or shorter than two borders has nowhere sensible
		// to put a rectangle.
		if (bounds.Width < _width * 2 || bounds.Height < _width * 2)
		{
			Hide();
			return;
		}

		// The border is drawn just inside the visible frame rather than just
		// outside it. Outside would fall off the screen (or under the
		// taskbar) for maximized and snapped windows, which is exactly where
		// a focus indicator is most needed.
		var top = Rect(bounds.Left, bounds.Top, bounds.Right, bounds.Top + _width);
		var bottom = Rect(bounds.Left, bounds.Bottom - _width, bounds.Right, bounds.Bottom);
		var left = Rect(bounds.Left, bounds.Top + _width, bounds.Left + _width, bounds.Bottom - _width);
		var right = Rect(bounds.Right - _width, bounds.Top + _width, bounds.Right, bounds.Bottom - _width);

		_bounds = bounds;

		UpdateEdge(TOP, top, horizontal: true);
		UpdateEdge(BOTTOM, bottom, horizontal: true);
		UpdateEdge(LEFT, left, horizontal: false);
		UpdateEdge(RIGHT, right, horizontal: false);

		if (!_visible)
		{
			foreach (var edge in _edges)
				ShowWindow(edge, SW_SHOWNOACTIVATE);

			_visible = true;
			reassertZOrder = true;
		}

		if (reassertZOrder)
			ApplyZOrder(target);
	}

	internal void Hide()
	{
		if (!_visible)
			return;

		foreach (var edge in _edges)
			ShowWindow(edge, SW_HIDE);

		_visible = false;
	}

	/**
     * True when nothing has slipped between the target and the border.
     *
     * Deliberately not a plain GW_HWNDPREV comparison. The z-order is full of
     * windows that are never drawn - IME hosts such as "MSCTFIME UI", 0x0
     * helper windows, hidden shell surfaces - and any of them can sit between
     * the target and our strips. Treating those as an obstruction would
     * re-order four windows on every timer tick forever.
     */
	internal bool IsAbove(IntPtr target)
	{
		if (!_visible)
			return false;

		// Every strip has to be above the target, not just one of them. An
		// earlier version returned on the first hit, which meant a target
		// that raised itself part-way through ApplyZOrder could leave three
		// strips stranded below it and still be reported healthy - so the
		// border showed only one of its four edges, permanently.
		var found = 0;
		var walker = target;
		for (var i = 0; i < Z_ORDER_PROBE_LIMIT; i++)
		{
			walker = GetWindow(walker, GW_HWNDPREV);
			if (walker == IntPtr.Zero)
				break;

			if (Owns(walker))
			{
				if (++found == _edges.Length)
					return true;
				continue;
			}

			if (Obscures(walker))
				return false;
		}
		return found == _edges.Length;
	}

	/** True if this window is actually painted over the border. */
	private bool Obscures(IntPtr hwnd) => IsWindowVisible(hwnd) && GetWindowRect(hwnd, out var r) && !r.IsEmpty && r.Left < _bounds.Right && r.Right > _bounds.Left && r.Top < _bounds.Bottom && r.Bottom > _bounds.Top;

	private void UpdateEdge(int index, in RECT rect, bool horizontal)
	{
		if (rect.IsEmpty)
			return;

		// Guard against a window somehow larger than the bitmaps we sized to
		// the virtual screen; clamping is better than a failed draw.
		var width = Math.Min(rect.Width, horizontal ? _stripLength : _width);
		var height = Math.Min(rect.Height, horizontal ? _width : _stripLength);
		if (width <= 0 || height <= 0)
			return;

		if (_edgeRects[index].SameAs(rect) && _visible)
			return;

		_edgeRects[index] = rect;

		var destination = new POINT(rect.Left, rect.Top);
		var size = new SIZE(width, height);
		// The bitmaps are one flat colour, so any origin would do; take the
		// first pixel.
		var source = new POINT(0, 0);
		var blend = new BLENDFUNCTION
		{
			BlendOp = AC_SRC_OVER,
			BlendFlags = 0,
			SourceConstantAlpha = 255,
			AlphaFormat = AC_SRC_ALPHA,
		};

		// One call moves, resizes and repaints the strip atomically, so there
		// is no window in which a stale or half-positioned border is visible.
		UpdateLayeredWindow(_edges[index], IntPtr.Zero, ref destination, ref size, horizontal ? _horizontalDc : _verticalDc, ref source, 0, ref blend, ULW_ALPHA);
	}

	/**
     * Put the strips directly above the target instead of making them globally
     * topmost, so they never cover unrelated windows.
     *
     * Note the direction of SetWindowPos: hWndInsertAfter names the window the
     * positioned window is placed *behind*. Passing the target itself therefore
     * puts the border underneath it, where - since the border is drawn inside
     * the frame - it is completely invisible. The anchor has to be the window
     * immediately above the target instead.
     */
	private void ApplyZOrder(IntPtr target)
	{
		if (!IsWindow(target))
			return;

		var targetTopmost = WindowBounds.IsTopmost(target);

		// Topmost is a separate z-order band, and membership is sticky. Match
		// the target's band, so a border on an ordinary window never floats
		// over everything else.
		if (_topmost != targetTopmost)
		{
			var band = targetTopmost ? HWND_TOPMOST : HWND_NOTOPMOST;
			foreach (var edge in _edges)
			{
				SetWindowPos(edge, band, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_NOSENDCHANGING);
			}
			_topmost = targetTopmost;
		}

		var insertAfter = FindAnchorAbove(target, targetTopmost);

		// Chain the strips to each other rather than inserting all four at
		// the same anchor. These are four separate calls, and an application
		// that raises itself in between them - Teams and other WebView2 and
		// Electron shells do - would otherwise leave the remaining strips
		// stranded below the target while the first stayed above it. Chained,
		// the four always stay contiguous and move as one group.
		var after = insertAfter;
		foreach (var edge in _edges)
		{
			SetWindowPos(edge, after, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_NOSENDCHANGING);
			after = edge;
		}
	}

	/**
     * The window to sit behind so that we land immediately above target, or
     * HWND_TOP when there is nothing usable.
     */
	private IntPtr FindAnchorAbove(IntPtr target, bool targetTopmost)
	{
		var walker = target;
		for (var i = 0; i < Z_ORDER_PROBE_LIMIT; i++)
		{
			walker = GetWindow(walker, GW_HWNDPREV);

			// Nothing above the target at all: the top of the band will do.
			if (walker == IntPtr.Zero)
				break;

			// Skip the strips themselves, or we would anchor to where we
			// already are and never actually move.
			if (Owns(walker))
				continue;

			// An anchor in the other z-order band is unusable: the system
			// would clamp us to the edge of our own band anyway, and which
			// side of the target that lands on is not defined.
			if (WindowBounds.IsTopmost(walker) != targetTopmost)
				break;

			return walker;
		}

		// Top of our band. The focused window is normally already at the top
		// of the non-topmost band, so this puts the border just above it.
		return HWND_TOP;
	}

	// ------------------------------------------------------------------
	// Bitmaps
	// ------------------------------------------------------------------

	/**
     * Rebuild the strip bitmaps if the virtual screen has grown (monitor
     * hot-plug or resolution change).
     */
	internal void OnDisplayChanged()
	{
		if (RequiredStripLength() <= _stripLength)
			return;

		Rebuild();
	}

	/**
     * Apply the committed color and border width. Returns true only when the
     * rendered appearance actually changed.
     */
	internal bool ApplyAppearance(uint colorArgb, int widthPx)
	{
		if (widthPx is < 1 or > 20)
			throw new ArgumentOutOfRangeException(nameof(widthPx));
		if (colorArgb >> 24 != 0xFF)
			throw new ArgumentOutOfRangeException(nameof(colorArgb), "Border color must be opaque.");
		if (_color == colorArgb && _width == widthPx)
			return false;

		_color = colorArgb;
		_width = widthPx;
		Rebuild();
		return true;
	}

	/**
     * Discard the bitmaps and make them again. Clearing the cached edge
     * rectangles is what forces UpdateEdge to actually repaint: it otherwise
     * skips an edge whose geometry has not moved.
     */
	private void Rebuild()
	{
		Array.Clear(_edgeRects);
		DestroyStripBitmaps();
		CreateStripBitmaps();
	}

	private static int RequiredStripLength()
	{
		var width = GetSystemMetrics(SM_CXVIRTUALSCREEN);
		var height = GetSystemMetrics(SM_CYVIRTUALSCREEN);

		// A little slack covers windows that overhang the virtual screen.
		return Math.Max(Math.Max(width, height), 1024) + 256;
	}

	private void CreateStripBitmaps()
	{
		_stripLength = RequiredStripLength();

		_horizontalDc = CreateCompatibleDC(IntPtr.Zero);
		_horizontalBitmap = CreateSolidDib(_stripLength, _width, _color);
		_horizontalOldBitmap = SelectObject(_horizontalDc, _horizontalBitmap);

		_verticalDc = CreateCompatibleDC(IntPtr.Zero);
		_verticalBitmap = CreateSolidDib(_width, _stripLength, _color);
		_verticalOldBitmap = SelectObject(_verticalDc, _verticalBitmap);
	}

	private static IntPtr CreateSolidDib(int width, int height, uint color)
	{
		var header = new BITMAPINFOHEADER
		{
			// negative: top-down rows
			BiSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
			BiWidth = width,
			BiHeight = -height,
			BiPlanes = 1,
			BiBitCount = 32,
			BiCompression = BI_RGB,
		};

		var bitmap = CreateDIBSection(IntPtr.Zero, ref header, DIB_RGB_COLORS, out var bits, IntPtr.Zero, 0);

		if (bitmap == IntPtr.Zero || bits == IntPtr.Zero)
			throw new InvalidOperationException("CreateDIBSection failed for the border bitmap.");

		var pixelCount = width * height;
		var pixels = new int[pixelCount];
		Array.Fill(pixels, unchecked((int)color));
		Marshal.Copy(pixels, 0, bits, pixelCount);

		return bitmap;
	}

	private void DestroyStripBitmaps()
	{
		if (_horizontalDc != IntPtr.Zero)
		{
			SelectObject(_horizontalDc, _horizontalOldBitmap);
			DeleteDC(_horizontalDc);
			_horizontalDc = IntPtr.Zero;
		}

		if (_horizontalBitmap != IntPtr.Zero)
		{
			DeleteObject(_horizontalBitmap);
			_horizontalBitmap = IntPtr.Zero;
		}

		if (_verticalDc != IntPtr.Zero)
		{
			SelectObject(_verticalDc, _verticalOldBitmap);
			DeleteDC(_verticalDc);
			_verticalDc = IntPtr.Zero;
		}

		if (_verticalBitmap != IntPtr.Zero)
		{
			DeleteObject(_verticalBitmap);
			_verticalBitmap = IntPtr.Zero;
		}
	}

	// ------------------------------------------------------------------
	// Window class
	// ------------------------------------------------------------------

	private static void EnsureClassRegistered(IntPtr instance)
	{
		if (_sClassAtom != 0)
			return;

		var wc = new WNDCLASSEXW
		{
			// layered content only; never erased
			CbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
			Style = 0,
			LpfnWndProc = Marshal.GetFunctionPointerForDelegate(_sWndProc),
			HInstance = instance,
			HbrBackground = IntPtr.Zero,
			LpszClassName = CLASS_NAME,
		};

		_sClassAtom = RegisterClassExW(ref wc);
		if (_sClassAtom == 0)
			throw new InvalidOperationException("RegisterClassEx failed for the overlay class: " + Marshal.GetLastWin32Error());
	}

	private static IntPtr OverlayWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
	{
		switch (msg)
		{
			// Belt and braces alongside WS_EX_TRANSPARENT / WS_EX_NOACTIVATE:
			// the overlay must never take a click and must never activate.
			case WM_NCHITTEST:
				return new IntPtr(HTTRANSPARENT);

			case WM_MOUSEACTIVATE:
				return new IntPtr(MA_NOACTIVATE);

			case WM_ERASEBKGND:
				return new IntPtr(1);
		}
		return DefWindowProcW(hwnd, msg, wParam, lParam);
	}

	private static RECT Rect(int left, int top, int right, int bottom) => new() { Left = left, Top = top, Right = right, Bottom = bottom };

	public void Dispose()
	{
		if (_disposed)
			return;

		_disposed = true;

		for (var i = 0; i < _edges.Length; i++)
			if (_edges[i] != IntPtr.Zero)
			{
				DestroyWindow(_edges[i]);
				_edges[i] = IntPtr.Zero;
			}

		DestroyStripBitmaps();

		if (_sClassAtom != 0)
		{
			UnregisterClassW(CLASS_NAME, GetModuleHandleW(null));
			_sClassAtom = 0;
		}
	}
}
