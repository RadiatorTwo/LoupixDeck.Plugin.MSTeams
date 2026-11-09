# LoupixDeck.Plugin.MSTeams

LoupixDeck plugin.

## Build

```bash
dotnet build -c Release
```

## Try it locally

```bash
loupix install          # packs the plugin and copies it into the LoupixDeck plugins folder
```

Restart LoupixDeck afterwards. `loupix install --debug` targets the plugins folder of a Debug build.

## Release

`loupix pack` writes the Plugin Store files to `dist/`. For a store release, bump `version` in
`plugin.json`, then publish a GitHub release tagged `v<version>`; the workflow in
`.github/workflows/release.yml` builds and attaches the files.
