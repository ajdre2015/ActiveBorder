using Microsoft.Win32;

namespace ActiveBorder;

internal sealed class StartupManager
{
	private const string RUN_KEY = @"Software\Microsoft\Windows\CurrentVersion\Run";
	private const string VALUE_NAME = "ActiveBorder";

	private readonly string _processPath;

	public StartupManager(string? processPath = null)
	{
		_processPath = processPath ?? Environment.ProcessPath ?? string.Empty;
	}

	public bool IsEnabled()
	{
		using var key = Registry.CurrentUser.OpenSubKey(RUN_KEY, writable: false);
		var value = key?.GetValue(VALUE_NAME) as string;
		return string.Equals(value, GetRunCommand(), StringComparison.OrdinalIgnoreCase);
	}

	public void SetEnabled(bool enabled)
	{
		if (enabled)
		{
			using var key = Registry.CurrentUser.CreateSubKey(RUN_KEY, writable: true)
				?? throw new InvalidOperationException("Unable to open the current user's Run registry key.");
			key.SetValue(VALUE_NAME, GetRunCommand(), RegistryValueKind.String);
			return;
		}

		using var existingKey = Registry.CurrentUser.OpenSubKey(RUN_KEY, writable: true);
		existingKey?.DeleteValue(VALUE_NAME, throwOnMissingValue: false);
	}

	private string GetRunCommand()
	{
		if (string.IsNullOrWhiteSpace(_processPath))
			throw new InvalidOperationException("The current executable path is unavailable; startup registration cannot be enabled.");

		return $"\"{_processPath}\"";
	}
}
