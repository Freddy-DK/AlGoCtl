# AlGoCtl

Command-line client for controlling [AL-Go for GitHub](https://github.com/microsoft/AL-Go) repositories. It automates creating new repositories from a template and keeping an existing repository's AL-Go system files up to date, then triggers the relevant workflow for you.

`algoctl` is distributed as a [.NET tool](https://aka.ms/global-tools) and works against both github.com and GitHub Enterprise Server hosts.

## Future

This CLI will be used for additional [AL-Go for GitHub](https://github.com/microsoft/AL-Go) tools in the future, to make working with AL-Go easier.

## Getting started

1. Install the [.NET SDK](https://dotnet.microsoft.com/download) (.NET 10 is the preferred runtime; a .NET 8 build is also provided).
2. Install the CLI from NuGet:

   ```pwsh
   dotnet tool install --global AlGoCtl
   ```

3. Make sure the prerequisites below are available on your `PATH`.

To update to the latest version:

```pwsh
dotnet tool update --global AlGoCtl
```

## Prerequisites

`algoctl` shells out to two external tools, which must be installed and
reasonably recent:

- [git](https://git-scm.com/) — used to clone template/target repositories and push changes.
- [GitHub CLI (`gh`)](https://cli.github.com/) — used to authenticate, create repositories, and trigger workflows.

## Authentication

`algoctl` reuses the GitHub CLI's authentication. Sign in once with:

```pwsh
gh auth login
```

By default the **active account** on the relevant host is used. When you are signed in to multiple accounts (or GitHub Enterprise hosts), target a specific one with `--ghuser <user>` (and `--templateghuser <user>` for reading the template repository).

## Usage

```pwsh
algoctl <command> [--key "value" ...]
```

| Option | Description |
| --- | --- |
| `--version`, `-v` | Show the `algoctl` version |

Repository arguments accept either the short `owner/repo` form (which defaults to github.com) or a full URL (which may target a GitHub Enterprise host).

## Commands

### createrepo

Creates a new GitHub repository and seeds it with the contents of a template repository.

| Option | Required | Description |
| --- | --- | --- |
| `--repo <owner/repo\|url>` | Yes | The repository to create. Short form defaults to github.com. |
| `--templaterepo <owner/repo\|url>` | No | Template repository to copy content from. Default: `https://github.com/Freddy-DK/AL-Go-PTE`. |
| `--ghuser <user>` | No | GitHub CLI account used to create the repository. Default: active account on the target host. |
| `--templateghuser <user>` | No | GitHub CLI account used to read the template repository. Default: active account on the template host, or unauthenticated. |
| `--visibility <private\|internal\|public>` | No | Visibility of the new repository. Default: `private`. |
| `--confirm` | No | Skip the interactive confirmation prompt. |

```pwsh
algoctl createrepo --repo myorg/my-al-app --ghuser myuser --visibility internal
```

### updaterepo

Copies the AL-Go system-file update workflow (`.github/workflows/UpdateGitHubGoSystemFiles.yaml`) from a template repository into an existing repository and triggers that workflow. Only the workflow file is copied; no other template content is touched.

| Option | Required | Description |
| --- | --- | --- |
| `--repo <owner/repo\|url>` | Yes | The repository to update. Short form defaults to github.com. |
| `--templaterepo <owner/repo\|url>` | No | Template repository to copy the workflow from. Default: `https://github.com/Freddy-DK/AL-Go-PTE`. |
| `--ghuser <user>` | No | GitHub CLI account used to update the repository. Default: active account on the target host. |
| `--templateghuser <user>` | No | GitHub CLI account used to read the template repository. Default: active account on the template host, or unauthenticated. |
| `--branches <list>` | No | Comma-separated list of branches to update (passed to the workflow's `includeBranches` input). Wildcards supported. Default: `main`. |
| `--confirm` | No | Skip the interactive confirmation prompt. |

```pwsh
algoctl updaterepo --repo myorg/my-al-app --ghuser myuser --branches "main,release/*"
```

### createrelease

Runs the AL-Go `CreateRelease.yaml` workflow in an existing repository to promote a build to a GitHub release. The workflow is triggered on the branch selected with `--branch` (the repository's default branch when omitted).

| Option | Required | Description |
| --- | --- | --- |
| `--repo <owner/repo\|url>` | Yes | The repository to create the release in. Short form defaults to github.com. |
| `--ghuser <user>` | No | GitHub CLI account used to run the workflow. Default: active account on the target host. |
| `--branch <branch>` | No | Branch (ref) to run the workflow on. Default: the repository's default branch. |
| `--name <name>` | Yes | Name of this release. |
| `--tag <tag>` | Yes | Tag of this release; must be a semantic version string (e.g. `1.0.0`). A pattern ending in `*` (e.g. `1.0.*`) resolves to the latest matching tag with its patch incremented, or the first one (e.g. `1.0.0`) when none exist. |
| `--buildversion <version>` | No | Build version to promote to release. Default: `latest`. |
| `--releasetype <Release\|Prerelease\|Draft>` | No | Release, prerelease or draft? Default: `Release`. |
| `--createreleasebranch` | No | Create a release branch. Default: `false`. |
| `--releasebranchprefix <prefix>` | No | Prefix for the release branch. Used only if `--createreleasebranch` is set. Default: `release/`. |
| `--updateversionnumber <version>` | No | New version number in the main branch. Use `Major.Minor` (optionally add `.Build` for versioning strategy 3) for an absolute change, or `+1`, `+0.1` (or `+0.0.1` for versioning strategy 3) for an incremental change. Default: none. |
| `--skipupdatingdependencies` | No | Skip updating dependency version numbers in all apps. Default: `false`. |
| `--directcommit` | No | Direct commit? Default: `false`. |
| `--useghtokenworkflow` | No | Use GhTokenWorkflow for PR/commit? Default: `false`. |
| `--confirm` | No | Skip the interactive confirmation prompt. |

```pwsh
algoctl createrelease --repo myorg/my-al-app --ghuser myuser --name "Version 1.0.0" --tag 1.0.0
```

## License

Licensed under the [MIT License](https://github.com/Freddy-DK/AlGoCtl).
