# AcpKit

A .NET SDK for the [Agent Client Protocol](https://agentclientprotocol.com/), for both the
**v1** and **v2** protocol lines, generated from the official schemas and free of reflection
on the runtime path — so it works under native AOT and trimming.

> **Status: under construction.** Nothing is published to NuGet yet.

## Why another one

ACP has a .NET SDK already — [timxx/dotacp](https://github.com/timxx/dotacp), which AcpKit's
generator is forked from and which remains the reference for anyone who needs
`netstandard2.0` or `net472`. AcpKit exists for a narrower target:

- **Native AOT and trimming.** Every JSON converter is generated for its concrete type. There
  is no `Activator.CreateInstance`, no `FieldInfo`, no constructor probing, and no
  `JsonSerializer` call that resolves a contract by `Type` at runtime.
- **ACP v2.** The v2 line is a redesign — the prompt response no longer ends the turn, tool
  calls are upsert-only, the client file-system and terminal APIs are gone, and every enum is
  open. v1 and v2 ship as separate packages so they can version independently.
- **Forward compatibility as specified.** The schemas carry `x-deserialize-default-on-error`
  and `x-deserialize-skip-invalid-items` hints, and v2 requires that unknown enum values,
  unknown union variants, and `_`-prefixed vendor extensions survive a round trip. AcpKit
  honours all of it.

## Layout

| Path | What |
|---|---|
| `schema/{v1,v2}/{stable,unstable}/` | Vendored ACP schemas, pinned by `VERSION` |
| `src/AcpKit.Core/` | Hand-written JSON-RPC 2.0 over newline-delimited JSON |
| `src/AcpKit.Protocol.V*/` | Generated models, converters, method tables |
| `src/AcpKit.Client.V*/` | Generated client connection and dispatch |
| `src/AcpKit.Agent.V*/` | Generated agent connection and dispatch |
| `tools/AcpKit.Generator/` | The schema-to-C# generator |
| `tests/` | Generator tests, golden files, conformance, AOT probe, live harnesses |

Everything under `src/**/Generated/` is produced by the generator and checked in, so a schema
bump shows up as a reviewable diff.

## Regenerating

```sh
dotnet run --project tools/AcpKit.Generator -- all --line v1 --line v2
```

A scheduled workflow runs this weekly against the newest `schema-v1.*` and `schema-v2.*` tags
and opens a pull request when the output changes.

## License

Apache-2.0. See [LICENSE](LICENSE) and [NOTICE](NOTICE) — the generator is derived from
[timxx/dotacp](https://github.com/timxx/dotacp), and the schemas come from
[agentclientprotocol/agent-client-protocol](https://github.com/agentclientprotocol/agent-client-protocol).
