# MorphLab Sprinkler - Claude Development Instructions

## Project

This is an Autodesk Revit plugin for sprinkler design,
automation, tagging, checking and related Revit workflows.

## Technology

- C#
- .NET Framework
- WPF
- Autodesk Revit API
- Visual Studio

## Important Development Rules

Before changing any code:

1. Understand the existing architecture.
2. Identify the relevant classes and methods.
3. Check all callers and dependencies.
4. Do not rewrite unrelated code.
5. Preserve existing functionality.
6. Make the smallest safe change possible.

## Revit API

All Revit document modifications must use valid Revit API
transactions.

Do not modify the Revit document from arbitrary background
threads.

Respect Revit API threading requirements.

## UI

The existing UI architecture must be preserved.

Do not replace WPF/XAML architecture unless explicitly requested.

## Build

After making code changes:

1. Build the project.
2. Report compilation errors.
3. Fix errors related to the requested change.
4. Do not hide compiler warnings or errors.
5. Do not change unrelated functionality.

## Revit Version Compatibility

Check the project's actual Revit API references before assuming
a Revit API version.

Do not invent Revit API methods.

When an API method is unavailable, identify the correct API
alternative based on the referenced Revit version.

## Git Safety

Before major changes:

1. Check git status.
2. Review the current branch.
3. Never discard existing user changes.
4. Never reset or delete user work without explicit permission.

After a meaningful change:

1. Show changed files.
2. Explain the changes.
3. Build the project.
4. Report build results.

Do not commit or push unless explicitly requested.

## Important Files

Core/
Revit/
UI/
Resources/
Installer/
App.cs
MorphLab.Sprinkler.csproj
MorphLab.Sprinkler.addin

## Family Files

Revit .rfa family files are intentionally excluded from Git.

Do not remove or modify the local Families directory unless
explicitly requested.

## Coding Philosophy

Prefer:

- Small safe changes
- Clear C# code
- Reusable services
- Defensive error handling
- Revit API-safe operations
- Existing architecture preservation

Avoid:

- Unnecessary rewrites
- Duplicate classes
- Duplicate methods
- Hard-coded machine-specific paths
- Breaking public APIs
- Removing existing functionality