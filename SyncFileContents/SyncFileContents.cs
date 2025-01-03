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
using ktsu.Extensions;
using ktsu.StrongPaths;
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

		_ = await Parser.Default.ParseArguments<Arguments>(args).WithParsedAsync(Sync).ConfigureAwait(false);
	}

	[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "<Pending>")]
	internal static async Task Sync(Arguments args)
	{
		HashSet<string> filesToSync = [];
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

			if (!string.IsNullOrWhiteSpace(filename))
			{
				_ = filesToSync.Add(filename);
			}
			else
			{
				while (true)
				{
					Console.WriteLine($"Add Filename(s):");
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

					if (string.IsNullOrWhiteSpace(filename))
					{
						break;
					}

					foreach (string file in filename.Split(','))
					{
						string newFile = Path.GetFileName(file.Trim());
						_ = filesToSync.Add(newFile.Trim());
					}

					if (filename.Contains(','))
					{
						break;
					}
				}
			}

			if (!Directory.Exists(path))
			{
				Console.WriteLine($"Path does not exist. <{path}>");
				return;
			}

			if (filesToSync.Count < 1)
			{
				Console.WriteLine("No files specified.");
				return;
			}

			HashSet<string> commitDirectories = [];
			HashSet<string> expandedFilesToSync = [];
			foreach (string fileToSync in filesToSync)
			{
				Console.WriteLine();
				Console.WriteLine($"Scanning for: {fileToSync}");
				Console.WriteLine($"In: {path}");
				Console.WriteLine();

				var fileEnumeration = Directory.EnumerateFiles(path, fileToSync, SearchOption.AllDirectories)
					.Where(f => !IsRepoNested(f.As<AbsoluteFilePath>().DirectoryPath))
					.ToCollection();

				var uniqueFilenames = fileEnumeration.Select(f => Path.GetFileName(f)).Distinct();
				Console.WriteLine($"Found matches: {string.Join(", ", uniqueFilenames)}");

				expandedFilesToSync.UnionWith(uniqueFilenames);

				foreach (string uniqueFilename in uniqueFilenames)
				{
					var fileMatches = fileEnumeration.Where(f => Path.GetFileName(f) == uniqueFilename);

					var results = new Dictionary<string, Collection<string>>();

					using var sha256 = SHA256.Create();

					foreach (string file in fileMatches)
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

						result.Add(file.Replace(path, "").Replace(uniqueFilename, "").Trim(Path.DirectorySeparatorChar));
					}

					var allDirectories = results.SelectMany(r => r.Value);
					commitDirectories.UnionWith(allDirectories);
					if (results.Count > 1)
					{
						int padWidth = allDirectories.Max(d => d.Length) + 4;

						results = results.OrderBy(r => r.Value.Count).ToDictionary(r => r.Key, r => r.Value);

						foreach (var (hash, relativeDirectories) in results)
						{
							Console.WriteLine();
							Console.WriteLine($"{hash} {uniqueFilename}");
							foreach (string dir in relativeDirectories)
							{
								string filePath = Path.Combine(path, dir, uniqueFilename);
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
							Console.WriteLine($"Only one file was changed for {uniqueFilename}, assuming you want to propagate that one.");
							syncHash = firstResult.Key;
						}
						else
						{
							Console.WriteLine("Enter a hash to sync to, or return to continue:");
							syncHash = Console.ReadLine() ?? string.Empty;
						}

						if (!string.IsNullOrWhiteSpace(syncHash))
						{
							var destinationDirectories = results
								.Where(r => r.Key != syncHash)
								.SelectMany(r => r.Value)
								.ToCollection();

							if (results.TryGetValue(syncHash, out var sourceDirectories))
							{
								Debug.Assert(sourceDirectories.Count > 0);
								string sourceDir = sourceDirectories[0];
								string sourceFile = Path.Combine(path, sourceDir, uniqueFilename);

								foreach (string dir in destinationDirectories)
								{
									string destinationFile = Path.Combine(path, dir, uniqueFilename);
									Console.WriteLine($"Dry run: From {sourceDir} to {destinationFile}");
								}

								Console.WriteLine();
								Console.WriteLine("Enter Y to sync.");

								if (Console.ReadLine()?.ToUpperInvariant() == "Y")
								{
									Console.WriteLine();
									foreach (string dir in destinationDirectories)
									{
										string destinationFile = Path.Combine(path, dir, uniqueFilename);
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
						Console.WriteLine($"No outstanding files to sync for: {uniqueFilename}.");
					}
				}
			}

			Console.WriteLine();

			var commitFiles = new Collection<string>();

			foreach (string? dir in commitDirectories)
			{
				string directoryPath = Path.Combine(path, dir);
				string repoPath = Repository.Discover(directoryPath);
				if (repoPath?.EndsWith(".git\\", StringComparison.Ordinal) ?? false) // don't try commit submodules
				{
					using var repo = new Repository(repoPath);
					foreach (string uniqueFilename in expandedFilesToSync)
					{
						string filePath = Path.Combine(directoryPath, uniqueFilename);
						var fileStatus = repo.RetrieveStatus(filePath);
						if (fileStatus is FileStatus.ModifiedInWorkdir or FileStatus.NewInWorkdir)
						{
							commitFiles.Add(filePath);
							Console.WriteLine($"{filePath} has outstanding changes");
						}
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
						if (!string.IsNullOrEmpty(repoPath))
						{
							using var repo = new Repository(repoPath);
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
			}

			var pushDirectories = new Collection<string>();
			var commitRepos = commitDirectories.Select(f => Repository.Discover(Path.Combine(path, f))).Distinct();
			foreach (string repoPath in commitRepos)
			{
				if (!string.IsNullOrEmpty(repoPath) && repoPath.EndsWith(".git\\", StringComparison.Ordinal)) // don't try commit submodules
				{
					using var repo = new Repository(repoPath);
					string repoRoot = repoPath.Replace(".git\\", "", StringComparison.Ordinal);
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
						pushDirectories.Add(repoRoot);
						Console.WriteLine($"{repoRoot} can be pushed automatically");
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

						using var repo = new Repository(repoPath);
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

			filesToSync.Clear();
			expandedFilesToSync.Clear();
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

	private static bool IsRepoNested(AbsoluteDirectoryPath path)
	{
		var checkDir = path;
		do
		{
			if (checkDir.Contents.Any(f => f.As<AnyAbsolutePath>().IsFile && f.As<AbsoluteFilePath>().FileName == ".git"))
			{
				return true;
			}
			checkDir = checkDir.Parent;
		}
		while (Path.IsPathFullyQualified(checkDir));

		return false;
	}
}
