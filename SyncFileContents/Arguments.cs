namespace ktsu.SyncFileContents;

using CommandLine;

internal class Arguments
{
	[Value(0, HelpText = "The path to recursively scan in.")]
	public string Path { get; set; } = string.Empty;

	[Value(1, HelpText = "The filename to scan for.")]
	public string Filename { get; set; } = string.Empty;
}
