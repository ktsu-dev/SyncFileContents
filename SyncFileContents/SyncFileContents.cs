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

	[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "Top-level async method")]
	internal static async Task Sync(Arguments args)
	{
		string filename = args.Filename;
		string path = args.Path;

		do
		{
			try
			{
				await EnsureCredentialsAsync();

				string applicationDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), nameof(SyncFileContents));
				_ = Directory.CreateDirectory(applicationDataPath);

				path = await PromptForPathAsync(path, applicationDataPath).ConfigureAwait(false);

				if (!Directory.Exists(path))
				{
					Console.WriteLine($"Path does not exist. <{path}>");
					return;
				}

				HashSet<string> filesToSync = await PromptForFilenamesAsync(filename, applicationDataPath).ConfigureAwait(false);

				if (filesToSync.Count < 1)
				{
					Console.WriteLine("No files specified.");
					return;
				}

				(HashSet<string> commitDirectories, HashSet<string> expandedFilesToSync) = await FindAndSyncFilesAsync(filesToSync, path).ConfigureAwait(false);

				CommitChangedFiles(commitDirectories, expandedFilesToSync, path);

				PushToRemote(commitDirectories, path);

				Console.WriteLine();
				Console.WriteLine("Press any key...");
				_ = Console.ReadKey();

				filename = string.Empty;
				path = string.Empty;
			}
			catch (OperationCanceledException)
			{
				Console.WriteLine("Aborted.");
				return;
			}
		}
		while (string.IsNullOrWhiteSpace(args.Path) || string.IsNullOrWhiteSpace(args.Filename));
	}

	[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "Console UI method where synchronization context is not relevant")]
	private static async Task EnsureCredentialsAsync()
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
					throw new OperationCanceledException("User aborted credential entry.");
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
					throw new OperationCanceledException("User aborted credential entry.");
				}
			}
		}
	}

	[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "Console UI method where synchronization context is not relevant")]
	private static async Task<string> PromptForPathAsync(string path, string applicationDataPath)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			Console.WriteLine($"Path:");
			await using Prompt prompt = new(persistentHistoryFilepath: $"{applicationDataPath}/history-path");

			while (true)
			{
				PromptResult response = await prompt.ReadLineAsync().ConfigureAwait(false);
				if (response.IsSuccess)
				{
					return response.Text;
				}

				if (response.CancellationToken.IsCancellationRequested)
				{
					throw new OperationCanceledException("User aborted path entry.");
				}
			}
		}

		return path;
	}

	[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "Console UI method where synchronization context is not relevant")]
	private static async Task<HashSet<string>> PromptForFilenamesAsync(string filename, string applicationDataPath)
	{
		HashSet<string> filesToSync = [];

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

				string inputFilename;
				while (true)
				{
					PromptResult response = await prompt.ReadLineAsync().ConfigureAwait(false);
					if (response.IsSuccess)
					{
						inputFilename = response.Text;
						break;
					}

					if (response.CancellationToken.IsCancellationRequested)
					{
						throw new OperationCanceledException("User aborted filename entry.");
					}
				}

				if (string.IsNullOrWhiteSpace(inputFilename))
				{
					break;
				}

				foreach (string file in inputFilename.Split(','))
				{
					string newFile = Path.GetFileName(file.Trim());
					_ = filesToSync.Add(newFile.Trim());
				}

				if (inputFilename.Contains(','))
				{
					break;
				}
			}
		}

		return filesToSync;
	}

	private static async Task<(HashSet<string> CommitDirectories, HashSet<string> ExpandedFilesToSync)> FindAndSyncFilesAsync(
		HashSet<string> filesToSync,
		string path)
	{
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
				await ProcessUniqueFilenameAsync(uniqueFilename, fileEnumeration, path, commitDirectories).ConfigureAwait(false);
			}
		}

		return (commitDirectories, expandedFilesToSync);
	}

	private static async Task ProcessUniqueFilenameAsync(
		string uniqueFilename,
		Collection<string> fileEnumeration,
		string path,
		HashSet<string> commitDirectories)
	{
		IEnumerable<string> fileMatches = fileEnumeration.Where(f => Path.GetFileName(f) == uniqueFilename);
		Dictionary<string, Collection<string>> results = [];

		using SHA256 sha256 = SHA256.Create();

		foreach (string file in fileMatches)
		{
			using FileStream fileStream = new(file, FileMode.Open);
			fileStream.Position = 0;
			byte[] hash = await sha256.ComputeHashAsync(fileStream).ConfigureAwait(false);
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
			await HandleMultipleHashGroupsAsync(results, uniqueFilename, path, allDirectories).ConfigureAwait(false);
		}
		else if (results.Count == 1)
		{
			Console.WriteLine($"No outstanding files to sync for: {uniqueFilename}.");
		}
	}

	private static async Task HandleMultipleHashGroupsAsync(
		Dictionary<string, Collection<string>> results,
		string uniqueFilename,
		string path,
		IEnumerable<string> allDirectories)
	{
		int padWidth = allDirectories.Max(d => d.Length) + 4;

		// Calculate oldest modification date for each hash group
		Dictionary<string, DateTime> oldestModificationDates = CalculateOldestModificationDates(results, path, uniqueFilename);

		// Sort by oldest modification date (most recent first)
		results = results.OrderByDescending(r => oldestModificationDates[r.Key]).ToDictionary(r => r.Key, r => r.Value);

		DisplayHashGroups(results, uniqueFilename, oldestModificationDates, padWidth);

		string syncHash = await PromptForSyncHashAsync(results).ConfigureAwait(false);

		if (!string.IsNullOrWhiteSpace(syncHash))
		{
			await SyncFilesToHashAsync(syncHash, results, uniqueFilename, path).ConfigureAwait(false);
		}
	}

	private static Dictionary<string, DateTime> CalculateOldestModificationDates(
		Dictionary<string, Collection<string>> results,
		string path,
		string uniqueFilename)
	{
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

		return oldestModificationDates;
	}

	private static void DisplayHashGroups(
		Dictionary<string, Collection<string>> results,
		string uniqueFilename,
		Dictionary<string, DateTime> oldestModificationDates,
		int padWidth)
	{
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
	}

	private static async Task<string> PromptForSyncHashAsync(Dictionary<string, Collection<string>> results)
	{
		if (results.Count == 2)
		{
			// Suggest the most recent hash first
			KeyValuePair<string, Collection<string>> firstResult = results.First();
			Console.WriteLine($"Suggest most recent hash: {firstResult.Key}? (y/N)");
			string? response = Console.ReadLine();

			if (response?.ToUpperInvariant() is "Y")
			{
				return firstResult.Key;
			}
			else
			{
				// Suggest the older hash
				KeyValuePair<string, Collection<string>> secondResult = results.Skip(1).First();
				Console.WriteLine($"Suggest older hash: {secondResult.Key}? (y/N)");
				response = Console.ReadLine();

				if (response?.ToUpperInvariant() is "Y")
				{
					return secondResult.Key;
				}
				else
				{
					Console.WriteLine("Enter a hash to sync to, or return to continue:");
					return (Console.ReadLine() ?? string.Empty).Trim();
				}
			}
		}
		else
		{
			Console.WriteLine("Enter a hash to sync to, or return to continue:");
			return (Console.ReadLine() ?? string.Empty).Trim();
		}
	}

	private static async Task SyncFilesToHashAsync(
		string syncHash,
		Dictionary<string, Collection<string>> results,
		string uniqueFilename,
		string path)
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
			Console.WriteLine("Sync? (y/N)");

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

	private static void CommitChangedFiles(
		HashSet<string> commitDirectories,
		HashSet<string> expandedFilesToSync,
		string path)
	{
		Console.WriteLine();

		Collection<string> commitFiles = [];

		foreach (string dir in commitDirectories)
		{
			string directoryPath = Path.Combine(path, dir);
			string repoPath = Repository.Discover(directoryPath);
			if (repoPath?.EndsWith(".git\\", StringComparison.Ordinal) ?? false)
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
			Console.WriteLine("Commit? (y/N)");

			if (Console.ReadLine()?.ToUpperInvariant() == "Y")
			{
				Console.WriteLine();
				foreach (string filePath in commitFiles)
				{
					CommitFile(filePath);
				}
			}
		}
	}

	private static void CommitFile(string filePath)
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
			}
			catch (UnmergedIndexEntriesException)
			{
			}
		}
	}

	private static void PushToRemote(HashSet<string> commitDirectories, string path)
	{
		Collection<string> pushDirectories = [];
		IEnumerable<string> commitRepos = commitDirectories.Select(f => Repository.Discover(Path.Combine(path, f))).Distinct();
		foreach (string repoPath in commitRepos)
		{
			if (!string.IsNullOrEmpty(repoPath) && repoPath.EndsWith(".git\\", StringComparison.Ordinal))
			{
				using Repository repo = new(repoPath);
				string repoRoot = repoPath.Replace(".git\\", "", StringComparison.Ordinal);
				Branch localBranch = repo.Branches[repo.Head.FriendlyName];
				int aheadBy = localBranch?.TrackingDetails.AheadBy ?? 0;

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
			Console.WriteLine("Push? (y/N)");

			if (Console.ReadLine()?.ToUpperInvariant() == "Y")
			{
				Console.WriteLine();
				foreach (string dir in pushDirectories)
				{
					PushDirectory(dir, path);
				}
			}
		}
	}

	private static void PushDirectory(string dir, string path)
	{
		Console.WriteLine($"Pushing: {dir}");
		string directoryPath = Path.Combine(path, dir);
		string repoPath = Repository.Discover(directoryPath);

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
			Console.WriteLine("Checking for remote changes...");
			Remote remote = repo.Network.Remotes["origin"];
			IEnumerable<string> refSpecs = remote.FetchRefSpecs.Select(x => x.Specification);

			FetchOptions fetchOptions = new()
			{
				CredentialsProvider = (url, user, creds) => credentials,
			};

			try
			{
				Commands.Fetch(repo, remote.Name, refSpecs, fetchOptions, "Fetched latest changes");

				Branch trackingBranch = repo.Head.TrackedBranch;
				if (trackingBranch != null)
				{
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
						return;
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

			repo.Network.Push(repo.Head, pushOptions);
		}
		catch (LibGit2SharpException e)
		{
			Console.WriteLine($"Error pushing: {e.Message}");
		}
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
