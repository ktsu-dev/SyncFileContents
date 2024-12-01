// Ignore Spelling: sha

[assembly: CLSCompliant(true)]
[assembly: System.Runtime.InteropServices.ComVisible(false)]

namespace ktsu.SyncFileContents;

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

		GlobalSettings.LogConfiguration = new(LogLevel.Info, new((level, message) =>
		{
			string logMessage = $"[{level}] {message}";
			Console.WriteLine($"Git: {logMessage}");
		}));

		_ = await Parser.Default.ParseArguments<Arguments>(args).WithParsedAsync(Sync);
	}

	internal static async Task Sync(Arguments args)
	{
		string filename = args.Filename;
		string path = args.Path;

		do
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

			string applicationDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), nameof(SyncFileContents));
			_ = Directory.CreateDirectory(applicationDataPath);
			if (string.IsNullOrWhiteSpace(path))
			{
				Console.WriteLine($"Path:");
				await using var prompt = new Prompt(persistentHistoryFilepath: $"{applicationDataPath}/history-path");

				while (true)
				{
					var response = await prompt.ReadLineAsync().ConfigureAwait(false);
					if (response.IsSuccess)
					{
						path = response.Text;
						break;
					}

					if (response.CancellationToken.IsCancellationRequested)
					{
						Console.WriteLine("Aborted.");
						return;
					}
				}
			}

			if (string.IsNullOrWhiteSpace(filename))
			{
				Console.WriteLine($"Filename:");
				await using var prompt = new Prompt(persistentHistoryFilepath: $"{applicationDataPath}/history-filename");

				while (true)
				{
					var response = await prompt.ReadLineAsync().ConfigureAwait(false);
					if (response.IsSuccess)
					{
						filename = response.Text;
						break;
					}

					if (response.CancellationToken.IsCancellationRequested)
					{
						Console.WriteLine("Aborted.");
						return;
					}
				}

				filename = Path.GetFileName(filename);
			}

			if (!Directory.Exists(path))
			{
				Console.WriteLine($"Path does not exist. <{path}>");
				return;
			}

			if (string.IsNullOrWhiteSpace(filename))
			{
				Console.WriteLine("Filename is empty.");
				return;
			}

			Console.WriteLine();
			Console.WriteLine($"Scanning for: {filename}");
			Console.WriteLine($"In: {path}");
			Console.WriteLine();

			var fileEnumeration = Directory.EnumerateFiles(path, filename, SearchOption.AllDirectories);

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

				result.Add(file.Replace(path, "").Replace(filename, "").Trim(Path.DirectorySeparatorChar));
			}

			var allDirectories = results.SelectMany(r => r.Value);

			if (results.Count > 1)
			{
				int padWidth = allDirectories.Max(d => d.Length) + 4;

				results = results.OrderBy(r => r.Value.Count).ToDictionary(r => r.Key, r => r.Value);

				foreach (var (hash, relativeDirectories) in results)
				{
					Console.WriteLine();
					Console.WriteLine(hash);
					foreach (string dir in relativeDirectories)
					{
						string filePath = Path.Combine(path, dir, filename);
						var fileInfo = new FileInfo(filePath);
						var created = fileInfo.CreationTime;
						var modified = fileInfo.LastWriteTime;
						Console.WriteLine($"{dir.PadLeft(padWidth)} {created,22} {modified,22}");
					}
				}

				Console.WriteLine();

				string syncHash;

				var firstResult = results.First();
				if (results.Count == 2 && firstResult.Value.Count == 1)
				{
					Console.WriteLine("Only one file was changed, assuming you want to propagate that one.");
					syncHash = firstResult.Key;
				}
				else
				{
					Console.WriteLine("Enter a hash to sync to, or return to quit:");
					syncHash = Console.ReadLine() ?? string.Empty;
				}

				if (!string.IsNullOrWhiteSpace(syncHash))
				{
					var destinationDirectories = results.Where(r => r.Key != syncHash).SelectMany(r => r.Value);
					if (results.TryGetValue(syncHash, out var sourceDirectories))
					{
						Debug.Assert(sourceDirectories.Count > 0);
						string sourceDir = sourceDirectories[0];
						string sourceFile = Path.Combine(path, sourceDir, filename);

						foreach (string dir in destinationDirectories)
						{
							string destinationFile = Path.Combine(path, dir, filename);
							Console.WriteLine($"Dry run: From {sourceDir} to {destinationFile}");
						}

						Console.WriteLine();
						Console.WriteLine("Enter Y to sync.");

						if (Console.ReadLine()?.ToUpperInvariant() == "Y")
						{
							Console.WriteLine();
							foreach (string dir in destinationDirectories)
							{
								string destinationFile = Path.Combine(path, dir, filename);
								Console.WriteLine($"Copying: From {sourceDir} to {destinationFile}");
								File.Copy(sourceFile, destinationFile, true);
							}
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

			foreach (string? dir in allDirectories)
			{
				string directoryPath = Path.Combine(path, dir);
				string filePath = Path.Combine(directoryPath, filename);
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
						catch (UnmergedIndexEntriesException)
						{
							continue;
						}
					}
				}
			}

			var pushDirectories = new Collection<string>();

			foreach (string dir in allDirectories)
			{
				string directoryPath = Path.Combine(path, dir);
				string filePath = Path.Combine(directoryPath, filename);
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
						pushDirectories.Add(dir);
						Console.WriteLine($"{dir} can be pushed automatically");
					}
				}
			}

			if (pushDirectories.Count > 0)
			{
				Console.WriteLine();
				Console.WriteLine("Enter Y to push.");

				if (Console.ReadLine()?.ToUpperInvariant() == "Y")
				{
					Console.WriteLine();
					foreach (string dir in pushDirectories)
					{
						Console.WriteLine($"Pushing: {dir}");
						string directoryPath = Path.Combine(path, dir);
						string repoPath = Repository.Discover(directoryPath);

						var pushOptions = new PushOptions
						{
							CredentialsProvider = (url, user, credentials) => new UsernamePasswordCredentials
							{
								Username = Settings.Username,
								Password = Settings.Token,
							},
							OnPushStatusError = (pushStatusErrors) =>
							{
								Console.WriteLine($"Error pushing: {pushStatusErrors.Message}");
							},
							OnPushTransferProgress = (current, total, bytes) =>
							{
								Console.WriteLine($"Progress: {current} / {total} ({bytes} bytes)");
								return true;
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

			filename = string.Empty;
			path = string.Empty;
		}
		while (string.IsNullOrWhiteSpace(args.Path) || string.IsNullOrWhiteSpace(args.Filename));
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
