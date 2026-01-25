// Copyright (c) ktsu.dev
// All rights reserved.
// Licensed under the MIT license.

namespace ktsu.SyncFileContents;

using CommandLine;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1812:Avoid uninstantiated internal classes", Justification = "Instantiated by CommandLineParser via reflection")]
internal sealed class Arguments
{
	[Value(0, HelpText = "The path to recursively scan in.")]
	public string Path { get; set; } = string.Empty;

	[Value(1, HelpText = "The filename to scan for.")]
	public string Filename { get; set; } = string.Empty;
}
