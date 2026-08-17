// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.SyncFileContents;

internal sealed class Settings : AppDataStorage.AppData<Settings>
{
	public string Username { get; set; } = string.Empty;
	public string Token { get; set; } = string.Empty;
}
