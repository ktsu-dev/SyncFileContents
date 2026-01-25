# SyncFileContents

![GitHub branch status](https://img.shields.io/github/checks-status/ktsu-dev/SyncFileContents/main)

`SyncFileContents` is a .NET 9 console application that scans a directory for files with a specified name, compares their contents using SHA256 hashes, and allows the user to synchronize these files across the directory. It also provides Git integration for committing and pushing changes.

## Features

- Recursively scan a specified directory for files with a given name
- Support for multiple filenames (comma-separated) and wildcard patterns
- Compare file contents using SHA256 hashes
- Display file paths grouped by hash with the oldest modification time for each hash group
- Sort hash groups by modification time (most recent first)
- Synchronize file contents across different directories within the scanned path
- Automatically skip files in nested Git repositories (submodules)
- Commit changes to Git repositories with automatic staging
- Fetch, merge, and push changes to remote repositories
- Interactive prompts with persistent history for paths and filenames
- Persistent Git credentials storage

## Table of Contents

- [Installation](#installation)
- [Usage](#usage)
  - [Command-Line Arguments](#command-line-arguments)
  - [Example Usage](#example-usage)
  - [Workflow](#workflow)
  - [Git Credentials](#git-credentials)
- [Development](#development)
- [License](#license)

## Installation

To use `SyncFileContents`, you need to have [.NET 9 SDK](https://dotnet.microsoft.com/download) installed on your machine.

Clone the repository:

```sh
git clone https://github.com/ktsu-dev/SyncFileContents.git
```

Navigate to the project directory:

```sh
cd SyncFileContents
```

Build the project:

```sh
dotnet build
```

## Usage

Run the application with optional command-line arguments:

```sh
dotnet run -- [path] [filename]
```

### Command-Line Arguments

- `path`: The path to recursively scan in. If not provided, you will be prompted to enter it interactively.
- `filename`: The filename to scan for. If not provided, you will be prompted to enter it interactively. Supports wildcards and comma-separated values.

### Example Usage

To scan the current directory for files named `example.txt` and synchronize their contents:

```sh
dotnet run -- . example.txt
```

To scan a specific directory for multiple config files:

```sh
dotnet run -- /path/to/projects "config.json,settings.json"
```

To run interactively without arguments:

```sh
dotnet run
```

### Workflow

1. **Scan**: The application recursively scans the specified directory for files matching the filename pattern
2. **Compare**: Files are grouped by SHA256 hash to identify differing versions
3. **Display**: Shows each unique hash with the oldest modification time for that hash group and associated file paths, sorted by modification time (most recent first)
4. **Select**: If only one file differs, it's automatically selected as the source; otherwise, you enter the hash to use as the sync source
5. **Preview**: A dry-run shows which files will be updated
6. **Sync**: After confirmation, files are copied from the source to all destinations
7. **Commit**: Outstanding changes in Git repositories can be committed
8. **Push**: Repositories with only SyncFileContents commits can be automatically pushed

### Git Credentials

On first run, you'll be prompted to enter your Git username and personal access token. These credentials are stored locally and used for push operations.

## Development

### Prerequisites

- [.NET 9 SDK](https://dotnet.microsoft.com/download)

### Building the Project

Clone the repository and navigate to the project directory:

```sh
git clone https://github.com/ktsu-dev/SyncFileContents.git
cd SyncFileContents
```

Build the project:

```sh
dotnet build
```

### Running Tests

To run the tests, use the following command:

```sh
dotnet test
```

## License

This project is licensed under the MIT License. See the [LICENSE](LICENSE) file for details.
