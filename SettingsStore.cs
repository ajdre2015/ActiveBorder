using System.Text.Json;

namespace ActiveBorder;

internal sealed class SettingsStore
{
	private readonly string _path;

	public SettingsStore(string? path = null)
	{
		_path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ActiveBorder", "settings.json");
	}

	public BorderSettings Load()
	{
		try
		{
			using var document = JsonDocument.Parse(File.ReadAllText(_path));
			if (document.RootElement.ValueKind != JsonValueKind.Object)
				return BorderSettings.Default;

			var root = document.RootElement;
			var followSystemAccent = GetBoolean(root, "followSystemAccent", BorderSettings.Default.FollowSystemAccent);
			var customColor = GetColor(root, "customColor", BorderSettings.Default.CustomColor);
			var borderWidth = GetWidth(root, "borderWidth", BorderSettings.Default.BorderWidth);

			return BorderSettings.Canonicalize(followSystemAccent, customColor, borderWidth);
		}
		catch (IOException)
		{
			return BorderSettings.Default;
		}
		catch (UnauthorizedAccessException)
		{
			return BorderSettings.Default;
		}
		catch (JsonException)
		{
			return BorderSettings.Default;
		}
	}

	public void Save(BorderSettings settings)
	{
		ArgumentNullException.ThrowIfNull(settings);
		Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

		var temporaryPath = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
		try
		{
			using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
			using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
			{
				writer.WriteStartObject();
				writer.WriteNumber("version", 1);
				writer.WriteBoolean("followSystemAccent", settings.FollowSystemAccent);
				writer.WriteString("customColor", settings.CustomColorHex);
				writer.WriteNumber("borderWidth", settings.BorderWidth);
				writer.WriteEndObject();
				writer.Flush();
				stream.Flush(true);
			}

			if (File.Exists(_path))
				File.Replace(temporaryPath, _path, null);
			else
				File.Move(temporaryPath, _path);
		}
		finally
		{
			if (File.Exists(temporaryPath))
				File.Delete(temporaryPath);
		}
	}

	private static bool GetBoolean(JsonElement root, string name, bool fallback) =>
		root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
			? value.GetBoolean()
			: fallback;

	private static uint GetColor(JsonElement root, string name, uint fallback) =>
		root.TryGetProperty(name, out var value)
			&& value.ValueKind == JsonValueKind.String
			&& BorderSettings.TryParseCustomColor(value.GetString(), out var color)
				? color
				: fallback;

	private static int GetWidth(JsonElement root, string name, int fallback) =>
		root.TryGetProperty(name, out var value) && value.TryGetInt32(out var width)
			? Math.Clamp(width, 1, 20)
			: fallback;
}
