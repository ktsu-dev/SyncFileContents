// Ignore Spelling: Username

namespace ktsu.io.SyncFileContents;

internal class Settings : AppDataStorage.AppData<Settings>
{
	public string Username { get; set; } = string.Empty;
	public string Token { get; set; } = string.Empty;
}
