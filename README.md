# PrintPlus Service

PrintPlus is a background service for processing work orders and printing documentation.
It downloads attachments from external sources (such as SharePoint) and prepares
files for printing with SAP and local printers.

The solution targets **.NET 8** (see `global.json`).

## Building

Use the .NET SDK 8.0 to build and run tests:

```bash
dotnet test --no-build
```

SAP native libraries are required at runtime and are included in the repository.

## Recent Changes

### SharePoint file download improvements

- `SharePointService` now assigns unique filenames when downloading files
  from SharePoint. Files with identical names no longer overwrite each other.
- A numeric counter is appended before the file extension to keep each
  downloaded path distinct.

- Additional inline comments explain this behaviour in `SharePointService.cs`.
