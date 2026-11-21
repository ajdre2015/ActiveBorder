using System.Security;

namespace ActiveBorder;

internal sealed class SettingsController : IDisposable
{
	private readonly SettingsStore _store;
	private readonly StartupManager _startup;
	private readonly FocusTracker _tracker;
	private readonly TrayIcon _tray;
	private readonly SettingsWindow _window;

	private BorderSettings _currentSettings;
	private bool _disposed;

	public BorderSettings CurrentSettings => _currentSettings;

	public SettingsController(BorderSettings initialSettings, SettingsStore store, StartupManager startup, FocusTracker tracker, TrayIcon tray, SettingsWindow window)
	{
		_currentSettings = initialSettings;
		_store = store;
		_startup = startup;
		_tracker = tracker;
		_tray = tray;
		_window = window;

		_tracker.AppearanceColorChanged += _tray.RefreshIcon;
		_tray.SettingsRequested += OpenSettings;
		_window.AttachController(this);
	}

	public void OpenSettings()
	{
		_window.Open(_currentSettings, ReadStartupEnabled());
	}

	public string? Save(BorderSettings settings, bool startupEnabled)
	{
		ArgumentNullException.ThrowIfNull(settings);
		var previousSettings = _currentSettings;
		_tracker.ApplySettings(settings);

		try
		{
			_store.Save(settings);
		}
		catch (IOException ex)
		{
			_tracker.ApplySettings(previousSettings);
			return $"设置文件写入失败，已恢复原外观：{ex.Message}";
		}
		catch (UnauthorizedAccessException ex)
		{
			_tracker.ApplySettings(previousSettings);
			return $"没有权限写入设置文件，已恢复原外观：{ex.Message}";
		}

		_currentSettings = settings;
		try
		{
			_startup.SetEnabled(startupEnabled);
			return null;
		}
		catch (IOException ex)
		{
			return $"外观设置已保存，但自启动选项未能更新：{ex.Message}";
		}
		catch (UnauthorizedAccessException ex)
		{
			return $"外观设置已保存，但自启动选项未能更新：{ex.Message}";
		}
		catch (SecurityException ex)
		{
			return $"外观设置已保存，但自启动选项未能更新：{ex.Message}";
		}
		catch (InvalidOperationException ex)
		{
			return $"外观设置已保存，但自启动选项未能更新：{ex.Message}";
		}
	}

	public bool ReadStartupEnabled()
	{
		try
		{
			return _startup.IsEnabled();
		}
		catch (UnauthorizedAccessException)
		{
			return false;
		}
		catch (SecurityException)
		{
			return false;
		}
	}

	public void Dispose()
	{
		if (_disposed)
			return;
		_disposed = true;
		_tray.SettingsRequested -= OpenSettings;
		_tracker.AppearanceColorChanged -= _tray.RefreshIcon;
	}
}
