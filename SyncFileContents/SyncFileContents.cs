// Copyright (c) 2023-2026 ktsu-dev contributors

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

using PrettyPrompt;

internal static class SyncFileContents
{
	internal static Settings Settings { get; set; } = new();

	private static async Task Main(string[] args)
	{
		Console.CancelKeyPress += (sender, e) => Environment.Exit(0);

		Settings = Settings.LoadOrCreate();

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

				await CommitChangedFilesAsync(commitDirectories, expandedFilesToSync, path).ConfigureAwait(false);

				await PushToRemoteAsync(commitDirectories, path).ConfigureAwait(false);

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
			HandleMultipleHashGroups(results, uniqueFilename, path, allDirectories);
		}
		else if (results.Count == 1)
		{
			Console.WriteLine($"No outstanding files to sync for: {uniqueFilename}.");
		}
	}

	private static void HandleMultipleHashGroups(
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

		string syncHash = PromptForSyncHash(results);

		if (!string.IsNullOrWhiteSpace(syncHash))
		{
			SyncFilesToHash(syncHash, results, uniqueFilename, path);
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

	private static string PromptForSyncHash(Dictionary<string, Collection<string>> results)
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

	private static void SyncFilesToHash(
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

	private static async Task CommitChangedFilesAsync(
		HashSet<string> commitDirectories,
		HashSet<string> expandedFilesToSync,
		string path)
	{
		Console.WriteLine();

		Collection<string> commitFiles = [];

		foreach (string dir in commitDirectories)
		{
			string directoryPath = Path.Combine(path, dir);
			string repoRoot = await GitCli.DiscoverRootAsync(directoryPath).ConfigureAwait(false);
			if (!string.IsNullOrEmpty(repoRoot))
			{
				foreach (string uniqueFilename in expandedFilesToSync)
				{
					string filePath = Path.Combine(directoryPath, uniqueFilename);

					// --porcelain prints nothing for a path that matches HEAD, so any output means
					// the file is either modified or untracked.
					GitResult status = await GitCli
						.RunInAsync(repoRoot, "status", "--porcelain", "--", filePath)
						.ConfigureAwait(false);

					if (status.Succeeded && status.OutputText.Length > 0)
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
					await CommitFileAsync(filePath).ConfigureAwait(false);
				}
			}
		}
	}

	private static async Task CommitFileAsync(string filePath)
	{
		Console.WriteLine($"Committing: {filePath}");

		string repoRoot = await GitCli.DiscoverRootAsync(filePath).ConfigureAwait(false);
		if (string.IsNullOrEmpty(repoRoot))
		{
			return;
		}

		// Staging through git, rather than writing the index directly, is what lets the clean
		// filter run so an LFS-tracked file is committed as a pointer instead of raw bytes.
		GitResult staged = await GitCli.RunInAsync(repoRoot, "add", "--", filePath).ConfigureAwait(false);
		if (!staged.Succeeded)
		{
			Console.WriteLine($"Failed to stage: {staged.FailureText}");
			return;
		}

		string relativeFilePath = Path.GetRelativePath(repoRoot, filePath);

		// The identity is supplied per invocation so the commit is attributed to the tool without
		// depending on, or disturbing, the repository's own configuration.
		GitResult committed = await GitCli
			.RunInAsync(
				repoRoot,
				"-c", $"user.name={nameof(SyncFileContents)}",
				"-c", $"user.email={nameof(SyncFileContents)}",
				"commit",
				"-m", $"Sync {relativeFilePath}",
				"--", filePath)
			.ConfigureAwait(false);

		// A file already matching HEAD leaves nothing staged, which git reports as a failure but
		// is the ordinary no-op case here.
		if (!committed.Succeeded
			&& !committed.Output.Contains("nothing to commit", StringComparison.OrdinalIgnoreCase)
			&& !committed.Output.Contains("nothing added to commit", StringComparison.OrdinalIgnoreCase))
		{
			Console.WriteLine($"Failed to commit: {committed.FailureText}");
		}
	}

	private static async Task PushToRemoteAsync(HashSet<string> commitDirectories, string path)
	{
		Collection<string> pushDirectories = [];
		HashSet<string> seenRoots = [];

		foreach (string dir in commitDirectories)
		{
			string repoRoot = await GitCli.DiscoverRootAsync(Path.Combine(path, dir)).ConfigureAwait(false);
			if (string.IsNullOrEmpty(repoRoot) || !seenRoots.Add(repoRoot))
			{
				continue;
			}

			// @{u} is the configured upstream. Without one, rev-list fails and there is nothing
			// meaningful to push, so treat that as zero commits ahead.
			GitResult ahead = await GitCli
				.RunInAsync(repoRoot, "rev-list", "--count", "@{u}..HEAD")
				.ConfigureAwait(false);

			if (!ahead.Succeeded || !int.TryParse(ahead.OutputText, out int aheadBy) || aheadBy == 0)
			{
				continue;
			}

			// Only push when every unpushed commit is one this tool made, so a user's own work is
			// never pushed on their behalf.
			GitResult authors = await GitCli
				.RunInAsync(repoRoot, "log", "--format=%an", $"-{aheadBy}", "HEAD")
				.ConfigureAwait(false);

			bool canPush = authors.Succeeded
				&& authors.OutputLines.Count == aheadBy
				&& authors.OutputLines.TrueForAll(author => author == nameof(SyncFileContents));

			if (canPush)
			{
				pushDirectories.Add(repoRoot);
				Console.WriteLine($"{repoRoot} can be pushed automatically");
			}
		}

		if (pushDirectories.Count > 0)
		{
			Console.WriteLine();
			Console.WriteLine("Push? (y/N)");

			if (Console.ReadLine()?.ToUpperInvariant() == "Y")
			{
				Console.WriteLine();
				foreach (string repoRoot in pushDirectories)
				{
					await PushDirectoryAsync(repoRoot).ConfigureAwait(false);
				}
			}
		}
	}

	private static async Task PushDirectoryAsync(string repoRoot)
	{
		Console.WriteLine($"Pushing: {repoRoot}");

		// Credentials are left to git, which uses the platform credential helper. That removes the
		// need to prompt for a token and store it, and it means SSH remotes work too.
		Console.WriteLine("Checking for remote changes...");
		GitResult pull = await GitCli.RunInAsync(repoRoot, "pull", "--ff-only").ConfigureAwait(false);
		if (!pull.Succeeded)
		{
			Console.WriteLine($"Error during pull: {pull.FailureText}");
			Console.WriteLine("Skipping push so a divergent branch is resolved manually.");
			return;
		}

		// Pushing through git runs the LFS pre-push hook, which uploads the objects that the
		// committed pointers refer to.
		GitResult push = await GitCli.RunInAsync(repoRoot, "push").ConfigureAwait(false);
		if (push.Succeeded)
		{
			Console.WriteLine($"Successfully pushed: {repoRoot}");
		}
		else
		{
			Console.WriteLine($"Error pushing: {push.FailureText}");
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
