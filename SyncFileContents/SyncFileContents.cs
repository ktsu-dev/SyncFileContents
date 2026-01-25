// Copyright (c) ktsu.dev
// All rights reserved.
// Licensed under the MIT license.

[assembly: CLSCompliant(true)]
[assembly: System.Runtime.InteropServices.ComVisible(false)]

namespace ktsu.SyncFileContents;

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

using CommandLine;

using ktsu.Extensions;
using ktsu.Semantics.Paths;

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
	[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1502: Avoid excessive complexity", Justification = "<Pending>")]
	[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1506: Avoid excessive class coupling", Justification = "<Pending>")]
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
				await using Prompt prompt = new();

				while (true)
				{
					PromptResult response = await prompt.ReadLineAsync().ConfigureAwait(false);
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
				await using Prompt prompt = new();

				while (true)
				{
					PromptResult response = await prompt.ReadLineAsync().ConfigureAwait(false);
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
				await using Prompt prompt = new(persistentHistoryFilepath: $"{applicationDataPath}/history-path");

				while (true)
				{
					PromptResult response = await prompt.ReadLineAsync().ConfigureAwait(false);
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
					await using Prompt prompt = new(persistentHistoryFilepath: $"{applicationDataPath}/history-filename");

					while (true)
					{
						PromptResult response = await prompt.ReadLineAsync().ConfigureAwait(false);
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

				Collection<string> fileEnumeration = Directory.EnumerateFiles(path, fileToSync, SearchOption.AllDirectories)
					.Where(f => !IsRepoNested(AbsoluteFilePath.Create<AbsoluteFilePath>(f).AbsoluteDirectoryPath))
					.ToCollection();

				IEnumerable<string> uniqueFilenames = fileEnumeration.Select(f => Path.GetFileName(f)).Distinct();
				Console.WriteLine($"Found matches: {string.Join(", ", uniqueFilenames)}");

				expandedFilesToSync.UnionWith(uniqueFilenames);

				foreach (string uniqueFilename in uniqueFilenames)
				{
					IEnumerable<string> fileMatches = fileEnumeration.Where(f => Path.GetFileName(f) == uniqueFilename);

					Dictionary<string, Collection<string>> results = [];

					using SHA256 sha256 = SHA256.Create();

					foreach (string file in fileMatches)
					{
						using FileStream fileStream = new(file, FileMode.Open);
						fileStream.Position = 0;
						byte[] hash = await sha256.ComputeHashAsync(fileStream);
						string hashStr = HashToString(hash);
						if (!results.TryGetValue(hashStr, out Collection<string>? result))
						{
							result = [];
							results.Add(hashStr, result);
						}

						result.Add(file.Replace(path, "").Replace(uniqueFilename, "").Trim(Path.DirectorySeparatorChar));
					}

					IEnumerable<string> allDirectories = results.SelectMany(r => r.Value);
					commitDirectories.UnionWith(allDirectories);
					if (results.Count > 1)
					{
						int padWidth = allDirectories.Max(d => d.Length) + 4;

						// Calculate oldest modification date for each hash group
						Dictionary<string, DateTime> oldestModificationDates = [];
						foreach ((string? hash, Collection<string>? relativeDirectories) in results)
						{
							DateTime oldestModified = DateTime.MaxValue;
							foreach (string dir in relativeDirectories)
							{
								string filePath = Path.Combine(path, dir, uniqueFilename);
								FileInfo fileInfo = new(filePath);
								DateTime modified = fileInfo.LastWriteTime;
								if (modified < oldestModified)
								{
									oldestModified = modified;
								}
							}
							oldestModificationDates[hash] = oldestModified;
						}

						// Sort by oldest modification date (most recent first)
						results = results.OrderByDescending(r => oldestModificationDates[r.Key]).ToDictionary(r => r.Key, r => r.Value);

						foreach ((string? hash, Collection<string>? relativeDirectories) in results)
						{
							Console.WriteLine();
							Console.WriteLine($"{hash} {uniqueFilename} ({oldestModificationDates[hash]})");
							foreach (string dir in relativeDirectories)
							{
								Console.WriteLine($"{dir.PadLeft(padWidth)}");
							}
						}

						Console.WriteLine();

						string syncHash;

						if (results.Count == 2)
						{
							// Suggest the most recent hash first
							KeyValuePair<string, Collection<string>> firstResult = results.First();
							Console.WriteLine($"Suggest most recent hash: {firstResult.Key}? (Y/n)");
							string? response = Console.ReadLine();

							if (response?.ToUpperInvariant() is "Y" or "")
							{
								syncHash = firstResult.Key;
							}
							else
							{
								// Suggest the older hash
								KeyValuePair<string, Collection<string>> secondResult = results.Skip(1).First();
								Console.WriteLine($"Suggest older hash: {secondResult.Key}? (Y/n)");
								response = Console.ReadLine();

								if (response?.ToUpperInvariant() is "Y" or "")
								{
									syncHash = secondResult.Key;
								}
								else
								{
									Console.WriteLine("Enter a hash to sync to, or return to continue:");
									syncHash = Console.ReadLine() ?? string.Empty;
								}
							}
						}
						else
						{
							Console.WriteLine("Enter a hash to sync to, or return to continue:");
							syncHash = Console.ReadLine() ?? string.Empty;
						}

						if (!string.IsNullOrWhiteSpace(syncHash))
						{
							Collection<string> destinationDirectories = results
								.Where(r => r.Key != syncHash)
								.SelectMany(r => r.Value)
								.ToCollection();

							if (results.TryGetValue(syncHash, out Collection<string>? sourceDirectories))
							{
								Debug.Assert(sourceDirectories.Count > 0);
								string sourceDir = sourceDirectories[0];
								string sourceFile = Path.Combine(path, sourceDir, uniqueFilename);

								foreach (string? dir in destinationDirectories)
								{
									string destinationFile = Path.Combine(path, dir, uniqueFilename);
									Console.WriteLine($"Dry run: From {sourceDir} to {destinationFile}");
								}

								Console.WriteLine();
								Console.WriteLine("Enter Y to sync.");

								if (Console.ReadLine()?.ToUpperInvariant() == "Y")
								{
									Console.WriteLine();
									foreach (string? dir in destinationDirectories)
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

			Collection<string> commitFiles = [];

			foreach (string dir in commitDirectories)
			{
				string directoryPath = Path.Combine(path, dir);
				string repoPath = Repository.Discover(directoryPath);
				if (repoPath?.EndsWith(".git\\", StringComparison.Ordinal) ?? false) // don't try commit submodules
				{
					using Repository repo = new(repoPath);
					foreach (string uniqueFilename in expandedFilesToSync)
					{
						string filePath = Path.Combine(directoryPath, uniqueFilename);
						FileStatus fileStatus = repo.RetrieveStatus(filePath);
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
							using Repository repo = new(repoPath);
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

			Collection<string> pushDirectories = [];
			IEnumerable<string> commitRepos = commitDirectories.Select(f => Repository.Discover(Path.Combine(path, f))).Distinct();
			foreach (string repoPath in commitRepos)
			{
				if (!string.IsNullOrEmpty(repoPath) && repoPath.EndsWith(".git\\", StringComparison.Ordinal)) // don't try commit submodules
				{
					using Repository repo = new(repoPath);
					string repoRoot = repoPath.Replace(".git\\", "", StringComparison.Ordinal);
					// check how far ahead we are
					Branch localBranch = repo.Branches[repo.Head.FriendlyName];
					int aheadBy = localBranch?.TrackingDetails.AheadBy ?? 0;

					// check if all outstanding commits were made by this tool
					int commitIndex = 0;
					bool canPush = true;
					foreach (Commit? commit in repo.Head.Commits)
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

						// For GitHub PATs, use the token as the password with the username as-is
						// For other providers, this should also work as they typically ignore username when token is valid
						UsernamePasswordCredentials credentials = new()
						{
							Username = Settings.Username,
							Password = Settings.Token,
						};

						PushOptions pushOptions = new()
						{
							CredentialsProvider = (url, user, creds) => credentials,
							OnPushStatusError = (pushStatusErrors) => Console.WriteLine($"Error pushing: {pushStatusErrors.Message}"),
							OnPushTransferProgress = (current, total, bytes) =>
							{
								Console.WriteLine($"Progress: {current} / {total} ({bytes} bytes)");
								return true;
							},
						};

						using Repository repo = new(repoPath);
						try
						{
							// Try to pull updates first before pushing
							Console.WriteLine("Checking for remote changes...");
							Remote remote = repo.Network.Remotes["origin"];
							IEnumerable<string> refSpecs = remote.FetchRefSpecs.Select(x => x.Specification);

							// Use the same credentials for fetching
							FetchOptions fetchOptions = new()
							{
								CredentialsProvider = (url, user, creds) => credentials,
							};

							try
							{
								// Fetch changes
								Commands.Fetch(repo, remote.Name, refSpecs, fetchOptions, "Fetched latest changes");

								// Get the tracking branch
								Branch trackingBranch = repo.Head.TrackedBranch;
								if (trackingBranch != null)
								{
									// Check if we need to merge
									Commit remoteBranchTip = trackingBranch.Tip;
									MergeResult mergeResult = repo.Merge(trackingBranch, new Signature(nameof(SyncFileContents), nameof(SyncFileContents), DateTimeOffset.Now));

									if (mergeResult.Status == MergeStatus.UpToDate)
									{
										Console.WriteLine("Local branch is up to date with remote.");
									}
									else if (mergeResult.Status == MergeStatus.FastForward)
									{
										Console.WriteLine("Fast-forwarded local branch to remote changes.");
									}
									else if (mergeResult.Status == MergeStatus.NonFastForward)
									{
										Console.WriteLine("Merged remote changes with local branch (non-fast-forward).");
									}
									else if (mergeResult.Status == MergeStatus.Conflicts)
									{
										Console.WriteLine("Cannot automatically merge due to conflicts. Please resolve conflicts manually.");
										continue; // Skip the push if there are conflicts
									}
								}
							}
							catch (LibGit2SharpException ex)
							{
								Console.WriteLine($"Error during pull: {ex.Message}");
								if (ex.InnerException != null)
								{
									Console.WriteLine($"Inner error: {ex.InnerException.Message}");
								}

								Console.WriteLine("Continuing with push...");
							}

							// Now push our changes
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
		StringBuilder builder = new();
		for (int i = 0; i < array.Length; i++)
		{
			_ = builder.Append(array[i].ToString("X2", CultureInfo.InvariantCulture));
		}

		return builder.ToString();
	}

	private static bool IsRepoNested(AbsoluteDirectoryPath path)
	{
		AbsoluteDirectoryPath checkDir = path;
		bool foundFirstRepo = false;

		while (!checkDir.IsRoot)
		{
			string gitDirPath = Path.Combine(checkDir.ToString(), ".git");
			if (Directory.Exists(gitDirPath))
			{
				if (foundFirstRepo)
				{
					// Found a second .git directory higher up - this repo is nested
					return true;
				}

				foundFirstRepo = true;
			}

			checkDir = checkDir.Parent;
		}

		return false;
	}
}
