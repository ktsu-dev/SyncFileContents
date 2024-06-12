# SyncFileContents

![GitHub branch status](https://img.shields.io/github/checks-status/ktsu-io/SyncFileContents/main)

`SyncFileContents` is a console application that scans a directory for files with a specified name, compares their contents using SHA256 hashes, and allows the user to synchronize these files across the directory.

## Features

- Recursively scan a specified directory for files with a given name.
- Compare file contents using SHA256 hashes.
- Display file paths, creation times, and modification times.
- Synchronize file contents across different directories within the scanned path.
- Commit changes to a Git repository if there are outstanding modifications.

## Table of Contents

- [Installation](#installation)
- [Usage](#usage)
  - [Command-Line Arguments](#command-line-arguments)
  - [Example Usage](#example-usage)
- [Development](#development)
- [License](#license)

## Installation

To use `SyncFileContents`, you need to have [.NET SDK](https://dotnet.microsoft.com/download) and [Git](https://git-scm.com/downloads) installed on your machine.

Clone the repository:

```sh
git clone https://github.com/ktsu-io/SyncFileContents.git
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

Run the application with the required command-line arguments:

```sh
dotnet run -- [path] [filename]
```

### Command-Line Arguments

- `path`: The path to recursively scan in. If not provided, you will be prompted to enter it.
- `filename`: The filename to scan for. If not provided, you will be prompted to enter it.

### Example Usage

To scan the current directory for files named `example.txt` and synchronize their contents:

```sh
dotnet run -- . example.txt
```

## Development

### Prerequisites

- [.NET SDK](https://dotnet.microsoft.com/download)
- [Git](https://git-scm.com/downloads)

### Building the Project

Clone the repository and navigate to the project directory:

```sh
git clone https://github.com/ktsu-io/SyncFileContents.git
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
