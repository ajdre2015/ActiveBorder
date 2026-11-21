using System.Globalization;

namespace ActiveBorder;

internal sealed record BorderSettings
{
	private const uint OPAQUE_ALPHA = 0xFF000000;

	public static BorderSettings Default { get; } = new(true, 0xFF0078D4, 5);

	public bool FollowSystemAccent { get; }
	public uint CustomColor { get; }
	public int BorderWidth { get; }

	private BorderSettings(bool followSystemAccent, uint customColor, int borderWidth)
	{
		FollowSystemAccent = followSystemAccent;
		CustomColor = customColor;
		BorderWidth = borderWidth;
	}

	public static BorderSettings Create(bool followSystemAccent, uint customColor, int borderWidth)
	{
		if ((customColor & OPAQUE_ALPHA) != OPAQUE_ALPHA)
			throw new ArgumentOutOfRangeException(nameof(customColor), "Custom color must be fully opaque ARGB.");
		if (borderWidth is < 1 or > 20)
			throw new ArgumentOutOfRangeException(nameof(borderWidth), "Border width must be between 1 and 20 pixels.");

		return new BorderSettings(followSystemAccent, customColor, borderWidth);
	}

	public static BorderSettings Canonicalize(bool followSystemAccent, uint customColor, int borderWidth)
	{
		var safeColor = (customColor & OPAQUE_ALPHA) == OPAQUE_ALPHA ? customColor : Default.CustomColor;
		return new BorderSettings(followSystemAccent, safeColor, Math.Clamp(borderWidth, 1, 20));
	}

	public uint ResolveEffectiveColor(uint systemAccent) => FollowSystemAccent
		? systemAccent | OPAQUE_ALPHA
		: CustomColor;

	public string CustomColorHex => $"#{CustomColor.ToString("X8", CultureInfo.InvariantCulture)}";

	public static bool TryParseCustomColor(string? value, out uint color)
	{
		color = Default.CustomColor;
		if (value is null || value.Length != 9 || value[0] != '#'
			|| !uint.TryParse(value.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed)
			|| (parsed & OPAQUE_ALPHA) != OPAQUE_ALPHA)
			return false;

		color = parsed;
		return true;
	}
}
