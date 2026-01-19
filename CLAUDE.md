# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build Commands

```bash
dotnet build                    # Build the project
dotnet test                     # Run tests
dotnet run -- [path] [filename] # Run the application
```

## Project Overview

SyncFileContents is a .NET 9 console application that synchronizes files with the same name across multiple directories. It uses the `ktsu.Sdk.App` SDK for project configuration.

**Core workflow:**
1. Recursively scans a directory for files matching a given filename pattern
2. Groups files by SHA256 hash to identify differing versions
3. Allows user to select a source hash to propagate to all other copies
4. Commits changes to Git repositories using LibGit2Sharp
5. Optionally pushes commits to remote repositories

## Architecture

- **SyncFileContents.cs** - Main program logic including file scanning, hash comparison, and git operations (commit/push)
- **Arguments.cs** - Command-line argument parsing using CommandLineParser (`path` and `filename` positional args)
- **Settings.cs** - Persistent user settings (git username/token) stored via `ktsu.AppDataStorage`

## Key Dependencies

- `LibGit2Sharp` - Git operations (commit, push, fetch, merge)
- `CommandLineParser` - CLI argument parsing
- `PrettyPrompt` - Interactive console prompts with history
- `ktsu.StrongPaths` - Strongly-typed path handling
- `ktsu.AppDataStorage` - Application settings persistence

## Code Quality

When suppressing warnings, use explicit suppression attributes with justifications rather than global suppressions. Make the smallest targeted suppressions possible.
