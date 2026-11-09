# LoupixDeck.Plugin.MSTeams

LoupixDeck plugin, scaffolded with `loupix new`.

## Plugin skeleton

- **Assembly / folder name:** `LoupixDeck.Plugin.MSTeams` (convention: `LoupixDeck.Plugin.<Name>`)
- **Namespace:** `LoupixDeck.Plugin.MSTeams`
- **Plugin class:** `MSTeamsPlugin` derives from `LoupixPlugin`
- **Manifest:** `plugin.json` (id = `msteams`, sdkVersion = `1.36`, entryAssembly = `LoupixDeck.Plugin.MSTeams.dll`)
- **SDK package:** `LoupixDeck.PluginSdk` 1.36.0 with `<ExcludeAssets>runtime</ExcludeAssets>`. The host provides the SDK DLL; never ship it.

## Build, pack, install

- `dotnet build -c Release` builds the plugin.
- `loupix pack` validates `plugin.json` and writes the Plugin Store files to `dist/`.
- `loupix install [--debug]` packs and copies the plugin into the LoupixDeck plugins folder; restart LoupixDeck afterwards.
- Store release: publish a GitHub release tagged `v<version>` (see `.github/workflows/release.yml`).

## Rules

1. Exactly **one** concrete `LoupixPlugin` subclass per assembly; the host finds it via reflection.
2. `Metadata.Id`, `plugin.json#id` and the plugin folder name must be identical (lowercase).
3. `CommandName` is a **stable public API**: never rename it after a release.
4. `Execute` does **not** run on the UI thread: never touch Avalonia objects directly, wrap the body in `try/catch`, do not block.
5. Keep `Metadata.Version` and `plugin.json#version` in sync.

## References

- SDK documentation: the GitHub wiki of `RadiatorTwo/LoupixDeck.PluginSdk`.
- Full example plugin: `RadiatorTwo/LoupixDeck.Plugin.Audio` (commands, folder providers, settings page, platform-specific services).
