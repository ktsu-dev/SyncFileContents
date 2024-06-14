[assembly: CLSCompliant(true)]
[assembly: System.Runtime.InteropServices.ComVisible(false)]

namespace ktsu.io.SyncFileContents;

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using CommandLine;
using LibGit2Sharp;
using PrettyPrompt;

internal static class SyncFileContents
{
	internal static Settings Settings { get; set; } = new();

	private static async Task Main(string[] args)
	{
		Settings = Settings.LoadOrCreate();

		GlobalSettings.LogConfiguration = new(LogLevel.Debug, new((level, message) =>
		{
			string logMessage = $"[{level}] {message}";
			Console.WriteLine($"Git: {logMessage}");
		}));

		_ = await Parser.Default.ParseArguments<Arguments>(args).WithParsedAsync(Sync);
	}

	internal static async Task Sync(Arguments args)
	{
		if (string.IsNullOrEmpty(Settings.Username))
		{
			Console.WriteLine("Enter your git username:");
			await using var prompt = new Prompt();

			while (true)
			{
				var response = await prompt.ReadLineAsync().ConfigureAwait(false);
				if (response.IsSuccess)
				{
					Settings.Username = response.Text;
					Settings.Save();
					break;
				}

				if (response.CancellationToken.IsCancellationRequested)
				{
					Console.WriteLine("Aborted.");
					return;
				}
			}
		}

		if (string.IsNullOrEmpty(Settings.Token))
		{
			Console.WriteLine("Enter your git token:");
			await using var prompt = new Prompt();

			while (true)
			{
				var response = await prompt.ReadLineAsync().ConfigureAwait(false);
				if (response.IsSuccess)
				{
					Settings.Token = response.Text;
					Settings.Save();
					break;
				}

				if (response.CancellationToken.IsCancellationRequested)
				{
					Console.WriteLine("Aborted.");
					return;
				}
			}
		}

		string appdataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), nameof(SyncFileContents));
		Directory.CreateDirectory(appdataPath);
		if (string.IsNullOrEmpty(args.Path))
		{
			Console.WriteLine($"Path:");
			await using var prompt = new Prompt(persistentHistoryFilepath: $"{appdataPath}/history-path");

			while (true)
			{
				var response = await prompt.ReadLineAsync().ConfigureAwait(false);
				if (response.IsSuccess)
				{
					args.Path = response.Text;
					break;
				}

				if (response.CancellationToken.IsCancellationRequested)
				{
					Console.WriteLine("Aborted.");
					return;
				}
			}
		}

		if (string.IsNullOrEmpty(args.Filename))
		{
			Console.WriteLine($"Filename:");
			await using var prompt = new Prompt(persistentHistoryFilepath: $"{appdataPath}/history-filename");

			while (true)
			{
				var response = await prompt.ReadLineAsync().ConfigureAwait(false);
				if (response.IsSuccess)
				{
					args.Filename = response.Text;
					break;
				}

				if (response.CancellationToken.IsCancellationRequested)
				{
					Console.WriteLine("Aborted.");
					return;
				}
			}

			args.Filename = Path.GetFileName(args.Filename);
		}

		if (!Directory.Exists(args.Path))
		{
			Console.WriteLine($"Path does not exist. <{args.Path}>");
			return;
		}

		if (string.IsNullOrEmpty(args.Filename))
		{
			Console.WriteLine("Filename is empty.");
			return;
		}

		Console.WriteLine();
		Console.WriteLine($"Scanning for: {args.Filename}");
		Console.WriteLine($"In: {args.Path}");
		Console.WriteLine();

		var fileEnumeration = Directory.EnumerateFiles(args.Path, args.Filename, SearchOption.AllDirectories);

		var results = new Dictionary<string, Collection<string>>();

		using var sha256 = SHA256.Create();

		foreach (string file in fileEnumeration)
		{
			using var fileStream = new FileStream(file, FileMode.Open);
			fileStream.Position = 0;
			byte[] hash = await sha256.ComputeHashAsync(fileStream);
			string hashStr = HashToString(hash);
			if (!results.TryGetValue(hashStr, out var result))
			{
				result = [];
				results.Add(hashStr, result);
			}

			result.Add(file.Replace(args.Path, "").Replace(args.Filename, "").Trim(Path.DirectorySeparatorChar));
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
					string filepath = Path.Combine(args.Path, dir, args.Filename);
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
					string sourceFile = Path.Combine(args.Path, sourceDir, args.Filename);

					foreach (string dir in destinationDirs)
					{
						string destinationFile = Path.Combine(args.Path, dir, args.Filename);
						Console.WriteLine($"Dry run: From {sourceDir} to {destinationFile}");
					}

					Console.WriteLine();
					Console.WriteLine("Enter Y to sync.");

					if (Console.ReadLine()?.ToUpperInvariant() == "Y")
					{
						Console.WriteLine();
						foreach (string dir in destinationDirs)
						{
							string destinationFile = Path.Combine(args.Path, dir, args.Filename);
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
			string directoryPath = Path.Combine(args.Path, dir);
			string filePath = Path.Combine(directoryPath, args.Filename);
			string repoPath = Repository.Discover(filePath);
			if (repoPath?.EndsWith(".git\\", StringComparison.Ordinal) ?? false) // don't try commit submodules
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



		if (commitFiles.Count > 0)
		{
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
					repo.Index.Write();
					try
					{
						_ = repo.Commit($"Sync {relativeFilePath}", new Signature(nameof(SyncFileContents), nameof(SyncFileContents), DateTimeOffset.Now), new Signature(nameof(SyncFileContents), nameof(SyncFileContents), DateTimeOffset.Now));
					}
					catch (EmptyCommitException)
					{
						continue;
					}
				}
			}
		}

		var pushDirs = new Collection<string>();

		foreach (string dir in allDirs)
		{
			string directoryPath = Path.Combine(args.Path, dir);
			string filePath = Path.Combine(directoryPath, args.Filename);
			string repoPath = Repository.Discover(filePath);
			if (repoPath?.EndsWith(".git\\", StringComparison.Ordinal) ?? false) // don't try commit submodules
			{
				var repo = new Repository(repoPath);

				// check how far ahead we are
				var localBranch = repo.Branches[repo.Head.FriendlyName];
				int aheadBy = localBranch?.TrackingDetails.AheadBy ?? 0;

				// check if all outstanding commits were made by this tool
				int commitIndex = 0;
				bool canPush = true;
				foreach (var commit in repo.Head.Commits)
				{
					if (commitIndex < aheadBy)
					{
						if (commit.Author.Name != nameof(SyncFileContents))
						{
							canPush = false;
							break;
						}
					}
					else
					{
						break;
					}
					++commitIndex;
				}

				if (aheadBy > 0 && canPush)
				{
					pushDirs.Add(dir);
					Console.WriteLine($"{dir} can be pushed automatically");
				}
			}
		}

		if (pushDirs.Count > 0)
		{
			Console.WriteLine();
			Console.WriteLine("Enter Y to push.");

			if (Console.ReadLine()?.ToUpperInvariant() == "Y")
			{
				Console.WriteLine();
				foreach (string dir in pushDirs)
				{
					Console.WriteLine($"Pushing: {dir}");
					string directoryPath = Path.Combine(args.Path, dir);
					string repoPath = Repository.Discover(directoryPath);

					var pushOptions = new PushOptions
					{
						CredentialsProvider = (url, user, cred) => new UsernamePasswordCredentials
						{
							Username = Settings.Username,
							Password = Settings.Token,
						},
					};

					var repo = new Repository(repoPath);
					try
					{
						repo.Network.Push(repo.Head, pushOptions);
					}
					catch (LibGit2SharpException e)
					{
						Console.WriteLine($"Error pushing: {e.Message}");
					}
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
