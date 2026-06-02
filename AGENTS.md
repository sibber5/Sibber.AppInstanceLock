# AGENTS.md

## Project Overview
- **Library**: `Sibber.AppInstanceLock` (Enforces single running instances across OSes/sessions).
- **Target Framework**: .NET 10 (`net10.0`).
- **Dependencies**: Central Package Management (`Directory.Packages.props`) is enabled.

## Development & Build Environment
- **Artifacts**: Uses `UseArtifactsOutput=true`. Build outputs are routed to the `artifacts/` folder.
- **Analyzers**: Uses `latest-all` analysis level and `Tetractic.CodeAnalysis.ExceptionAnalyzers`. `src/ExceptionAdjustments.txt` is used for analyzer tuning.
- **Unsafe Code**: P/Invoke and native interop are heavily utilized (`<AllowUnsafeBlocks>true</AllowUnsafeBlocks>`).
- **Test Hooks**: The `INCLUDE_TEST_HOOKS` constant is defined in `Debug` builds (unless disabled). Be aware that internal logic may diverge when this is defined.

## Code Style & Standards
- **Persona Constraints**: Output must be natively terminal-renderable. Strict omission of formalities, pleasantries, and conversational filler. Be direct, concise, and technical. Skip explanations for standard concepts.
- **Language Features**: Use modern, safe C# features (e.g., `var`, primary constructors, file-scoped namespaces, extension syntax, collection expressions).
- **Quality & Safety**: Strict immediate error handling. Always prioritize security and reliability. Use custom error domains. Never swallow errors.
- **Formatting**:
  - 4 spaces for C#, 2 spaces for XML/props/csproj.
  - Types, constants, and non-field members are `PascalCase`.
  - Interfaces begin with `I`.
  - Private and internal fields (both instance and static) are `camelCase` and begin with `_`.
  - `var` is preferred everywhere (built-in types, when apparent, and elsewhere).
- **Git**: Use clear, concise, lowercase commit messages describing "what" changed (e.g., `add user caching`). Do NOT use Conventional Commits (no `feat:`, `fix:`). Preserve Git blame by applying minimal diffs. Never commit directly to protected branches.

## Testing Instructions
- **Framework**: `xunit.v3.mtp-v2` (Microsoft Testing Platform).
- **Structure**: Tests are split into `Integration`, `Unit`, and `Shared` projects under `/tests/`.
- **Strategy**: Prioritize integration tests. Write table-driven unit tests. Aim for wide, reliable coverage. Write self-documenting code with comments explaining "why", not "what".
- **Execution**: Use the `run-tests` skill to execute and debug tests (required for MTP). Use the `mtp-hot-reload` skill for rapid test iteration.

## Recommended Agent Skills
When working on this project, proactively utilize these loaded skills:
- `dotnet-pinvoke`: When reviewing or modifying `UnixInstanceLock.cs` or `WindowsInstanceLock.cs`.
- `run-tests` & `mtp-hot-reload`: When executing test suites or fixing failing tests.
- `test-anti-patterns`: When auditing test quality.
- `microsoft-docs`: When referencing .NET APIs.
