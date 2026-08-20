# SecRandom SecAgent Plugin

This is the native SecRandom plugin that provides the local loopback API consumed by the SecRandom SecAgent connector.

It replaces the former built-in SecAgent services in the SecRandom application:

- `http://127.0.0.1:3910/api/secagent/v1/students`
- `http://127.0.0.1:3910/api/secagent/v1/draw/students`
- best-effort installation/update of the `secrandom` connector in a running SecAgent

The plugin is packaged as `srpx/SecRandom.SecAgentPlugin.srpx`.

## Build

```powershell
dotnet build -c Release -p:CreateSrpx=true
```

Build against the matching SecRandom host source on the `agent/extract-secagent-plugin` branch. `SecRandom.PluginSdk` `3.0.0-alpha.2` is ABI-incompatible with that host and must not be used.

```powershell
dotnet build -c Release -p:CreateSrpx=true -p:UseLocalPluginSdk=true -p:LocalSecRandomRoot=<path-to-SecRandom>
```

Copy the generated SRPX package into SecRandom's `data/cache/plugin-packages` directory and restart SecRandom.
