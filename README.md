# Cove Single Extension Template

Use this template when one repository owns one Cove extension. On GitHub, 
create new extensions with the **Use this template** button.

After creating a repository from the template, replace the example extension ID,
namespaces, manifest fields, and release workflow `EXTENSION_ID` with your real
extension values.

## Build

```powershell
dotnet build .\SingleExtensionTemplate.slnx -c Release
```

## Package

The CI workflow publishes the project, copies `extension.json` to the package
root, creates `<extension-id>-<version>.zip`, and attaches it to tags named
`v<version>`.

## Local Cove contract development

If this repo is checked out beside `cove`, the project automatically uses the
local `src/Cove.Plugins` project. Force package mode with:

```powershell
dotnet build -p:UseLocalCovePlugins=false
```

## Scraper examples

The `scraper-examples` folder includes a pure YAML scraper example for extensions
that do not need compiled C# logic.
