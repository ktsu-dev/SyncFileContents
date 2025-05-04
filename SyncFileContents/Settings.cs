// Copyright (c) ktsu.dev
// All rights reserved.
// Licensed under the MIT license.

namespace ktsu.SyncFileContents;

internal class Settings : AppDataStorage.AppData<Settings>
{
	public string Username { get; set; } = string.Empty;
	public string Token { get; set; } = string.Empty;
}
