# AGENTS.md

This file provides system context and instructions for AI coding agents operating on the `Sibber.SingleInstanceApp` repository.

## Project Overview

`Sibber.AppInstanceLock` is a lightweight cross-platform .NET library that enforces a single running instance of an application. The library:
- Restricts application execution based on lock scopes: `Session` (logon session), `User` (current user account), or `Machine` (machine-wide).
- Supports Windows, Linux, and macOS platforms.
- Utilizes OS-native locking mechanisms: Mutexes on Windows and `flock` file locking on Unix systems.
- Leverages Named Pipes for inter-process communication (IPC) to notify the primary instance and pass message payloads when secondary instances are launched.

## Tech Stack & Tooling

- **Target Framework**: .NET 10.0 (`net10.0`)
- **Testing Frameworks**: None. There are no test projects in this repository.
- **Major NuGet Dependencies**:
  - `Microsoft.Extensions.Logging.Abstractions`
  - `Tetractic.CodeAnalysis.ExceptionAnalyzers`
- **Key Project Configuration**:
  - Nullable reference types are enabled (`<Nullable>enable</Nullable>`).
  - Implicit usings are enabled (`<ImplicitUsings>enable</ImplicitUsings>`).
  - Compiler strict mode is enabled (`<Features>strict</Features>`).
  - Nullable warnings are treated as errors (`<CodeAnalysisTreatWarningsAsErrors>true</CodeAnalysisTreatWarningsAsErrors>` and `<WarningsAsErrors>nullable</WarningsAsErrors>`).
  - Artifacts output format is enabled (`<UseArtifactsOutput>true</UseArtifactsOutput>`).

## Architecture Boundaries

The repository contains a single project (`src/Sibber.AppInstanceLock.csproj`) in the `src/` directory.

- **Public API**:
  - Define all public entry points, options, and enums in `src/InstanceLock.cs`.
  - Expose the single-instance locking API via `InstanceLock<TMessage>`.
- **Core Logic & Base Class**:
  - Place shared, platform-agnostic IPC loop logic, Named Pipe reading/writing, and serialization in the abstract base class `src/InstanceLockImpl.cs`.
- **Infrastructure & Platform Backends**:
  - Place Windows-specific implementation details, such as mutex and named pipe creation, in `src/WindowsInstanceLock.cs`.
  - Place Linux/macOS-specific implementation details, such as flock file locking, directory resolution, and P/Invoke declarations, in `src/UnixInstanceLock.cs`.
- **Utilities**:
  - Place auxiliary helper extensions in files dedicated to the type that the extensions are for, e.g., string sanitization in the file `src/StringExtensions.cs`.
- **Solution Items**:
  - Solution-wide settings reside in `.editorconfig` and `Directory.Build.props` in the root.

## Coding Standards

### Namespace Declarations
- **Always** use file-scoped namespaces (e.g., `namespace Sibber.AppInstanceLock;`).
- **Never** use block-scoped namespaces with curly braces `{}`.

### Usings & Imports
- **Always** leverage implicit usings.
- **Never** add explicit using directives for namespaces that are already covered by implicit usings (such as `System`, `System.IO`, `System.Threading`).

### Dependency Injection
- **Always** use constructor injection to pass dependencies (such as `ILoggerFactory` or `ILogger`).
- **Never** instantiate logger instances directly or use static service locators.

### Logging Guidelines
- **Always** inject `ILogger<T>` or `ILoggerFactory` into constructors to enable logging.
- **Always** use structured logging with named placeholder arguments (e.g., `_logger?.LogInformation("Message details: {Param1}", param1);`).
- **Never** use `Console.WriteLine` or `Console.Error.WriteLine` for application diagnostics.

### Asynchronous Programming (async/await)
- **Always** configure asynchronous await calls with `.ConfigureAwait(false)` (since this is library code) to avoid potential deadlock or other bugs in application that provide custom synchronization contexts.

## Common Workflows

### Build the Solution
- Run the build command from the root directory:
  ```powershell
  dotnet build ./Sibber.AppInstanceLock.slnx
  ```
  *Note: Code style, nullable checks, and analyzers are executed during the build.*

### Run Tests
- **Never** execute test commands. There are no test projects or tests configured in this repository.

### Database Migrations
- **Never** run EF Core migration commands. Entity Framework is not used in this project.
