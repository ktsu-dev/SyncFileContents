[assembly: CLSCompliant(true)]
[assembly: System.Runtime.InteropServices.ComVisible(false)]

namespace SyncFileContents;

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using CommandLine;
using LibGit2Sharp;

internal class SyncFileContents
{
	internal class Options
	{
		[Value(0, HelpText = "The path to recursively scan in.")]
		public string Path { get; set; } = "c:\\dev\\ktsu-io";

		[Value(1, HelpText = "The filename to scan for.")]
		public string Filename { get; set; } = "Directory.Build.targets";
	}

	private static void Main(string[] args) =>
		Parser.Default.ParseArguments<Options>(args).WithParsed(Sync);

	internal static void Sync(Options options)
	{

		if (string.IsNullOrEmpty(options.Path))
		{
			Console.WriteLine("Path:");
			options.Path = Console.ReadLine() ?? string.Empty;
		}

		if (string.IsNullOrEmpty(options.Filename))
		{
			Console.WriteLine("Filename:");
			options.Filename = Console.ReadLine() ?? string.Empty;
		}

		if (!Directory.Exists(options.Path))
		{
			Console.WriteLine($"Path does not exist. <{options.Path}>");
			return;
		}

		if (string.IsNullOrEmpty(options.Filename))
		{
			Console.WriteLine("Filename is empty.");
			return;
		}

		var fileEnumeration = Directory.EnumerateFiles(options.Path, options.Filename, SearchOption.AllDirectories);

		var results = new Dictionary<string, Collection<string>>();

		using var sha256 = SHA256.Create();

		foreach (string file in fileEnumeration)
		{
			using var fileStream = new FileStream(file, FileMode.Open);
			fileStream.Position = 0;
			byte[] hash = sha256.ComputeHash(fileStream);
			string hashStr = HashToString(hash);
			if (!results.TryGetValue(hashStr, out var result))
			{
				result = [];
				results.Add(hashStr, result);
			}

			result.Add(file.Replace(options.Path, "").Replace(options.Filename, "").Trim(Path.DirectorySeparatorChar));
		}

		var allDirs = results.SelectMany(r => r.Value);

		if (results.Count > 1)
		{
			int padWidth = allDirs.Max(d => d.Length) + 4;

			results = results.OrderBy(r => r.Value.Count).ToDictionary(r => r.Key, r => r.Value);

			foreach (var (hash, relativeDirs) in results)
			{
				Console.WriteLine();
				Console.WriteLine(hash);
				foreach (string dir in relativeDirs)
				{
					string filepath = Path.Combine(options.Path, dir, options.Filename);
					var fileInfo = new FileInfo(filepath);
					var created = fileInfo.CreationTime;
					var modified = fileInfo.LastWriteTime;
					Console.WriteLine($"{dir.PadLeft(padWidth)} {created,22} {modified,22}");
				}
			}

			Console.WriteLine();


			Console.WriteLine("Enter a hash to sync to, or return to quit:");
			string syncHash = Console.ReadLine() ?? string.Empty;
			if (!string.IsNullOrWhiteSpace(syncHash))
			{
				var destinationDirs = results.Where(r => r.Key != syncHash).SelectMany(r => r.Value);
				if (results.TryGetValue(syncHash, out var sourceDirs))
				{
					Debug.Assert(sourceDirs.Count > 0);
					string sourceDir = sourceDirs[0];
					string sourceFile = Path.Combine(options.Path, sourceDir, options.Filename);

					foreach (string dir in destinationDirs)
					{
						string destinationFile = Path.Combine(options.Path, dir, options.Filename);
						Console.WriteLine($"Dry run: From {sourceDir} to {destinationFile}");
					}

					Console.WriteLine();
					Console.WriteLine("Enter Y to sync.");

					if (Console.ReadLine()?.ToUpperInvariant() == "Y")
					{
						Console.WriteLine();
						foreach (string dir in destinationDirs)
						{
							string destinationFile = Path.Combine(options.Path, dir, options.Filename);
							Console.WriteLine($"Copying: From {sourceDir} to {destinationFile}");
							File.Copy(sourceFile, destinationFile, true);
						}
					}
					else
					{
						Console.WriteLine("Aborted.");
					}
				}
				else
				{
					Console.WriteLine("Hash not found.");
				}
			}
		}

		if (results.Count == 1)
		{
			Console.WriteLine("No outstanding files to sync.");
		}

		Console.WriteLine();

		var commitFiles = new Collection<string>();

		foreach (string? dir in allDirs)
		{
			string directoryPath = Path.Combine(options.Path, dir);
			string filePath = Path.Combine(directoryPath, options.Filename);
			string repoPath = Repository.Discover(filePath);
			if (repoPath?.EndsWith(".git\\", StringComparison.Ordinal) ?? false) // dont try commit submodules
			{
				var repo = new Repository(repoPath);
				var fileStatus = repo.RetrieveStatus(filePath);
				if (fileStatus != FileStatus.Unaltered)
				{
					commitFiles.Add(filePath);
					Console.WriteLine($"{filePath} has outstanding changes");
				}
			}
		}

		Console.WriteLine();
		Console.WriteLine("Enter Y to commit.");

		if (Console.ReadLine()?.ToUpperInvariant() == "Y")
		{
			Console.WriteLine();
			foreach (string filePath in commitFiles)
			{
				Console.WriteLine($"Committing: {filePath}");
				string repoPath = Repository.Discover(filePath);
				var repo = new Repository(repoPath);
				string relativeFilePath = filePath.Replace(repoPath.Replace(".git\\", "", StringComparison.Ordinal), "", StringComparison.Ordinal);
				repo.Index.Add(relativeFilePath);
				try
				{
					_ = repo.Commit($"Sync {relativeFilePath}", new Signature("SyncFileContents", "SyncFileContents", DateTimeOffset.Now), new Signature("SyncFileContents", "SyncFileContents", DateTimeOffset.Now));
				}
				catch (EmptyCommitException)
				{
					continue;
				}
			}
		}

		Console.WriteLine();
		Console.WriteLine("Press any key...");
		_ = Console.ReadKey();
	}

	internal static string HashToString(byte[] array)
	{
		var builder = new StringBuilder();
		for (int i = 0; i < array.Length; i++)
		{
			_ = builder.Append(array[i].ToString("X2", CultureInfo.InvariantCulture));
		}

		return builder.ToString();
	}
}
