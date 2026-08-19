# AcpKit

A .NET SDK for the [Agent Client Protocol](https://agentclientprotocol.com/), for both the
**v1** and **v2** protocol lines, generated from the official schemas and free of reflection
on the runtime path — so it works under native AOT and trimming.

[![AcpKit.Core](https://img.shields.io/nuget/v/AcpKit.Core?label=AcpKit.Core)](https://www.nuget.org/packages/AcpKit.Core)
[![ci](https://github.com/vezaynk/acpkit/actions/workflows/ci.yml/badge.svg)](https://github.com/vezaynk/acpkit/actions/workflows/ci.yml)

> **Status: early.** Published at 0.0.x, which is a claim about the API's stability rather
> than the implementation's: the shape of the public surface is still settling, and no
> production consumer has exercised it yet.

## Installing

Take the pair for the protocol line you need. Both pull in `AcpKit.Core` and the matching
protocol types.

```sh
dotnet add package AcpKit.Client.V1     # build a client against ACP v1
dotnet add package AcpKit.Agent.V1      # build an agent against ACP v1
```

Substitute `.V2` for the v2 line, bearing in mind it is a preview: see
[Packages](#packages) above and
[Do not trust the reported protocol version](#do-not-trust-the-reported-protocol-version)
below.

## Why another one

ACP has a .NET SDK already — [timxx/dotacp](https://github.com/timxx/dotacp), which remains
the reference for anyone who needs `netstandard2.0` or `net472`. AcpKit is an independent
implementation aimed at a narrower target:

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

The generator builds a typed model of what it intends to emit, renders it through Roslyn, and
then *compiles* the result as part of the build. Output that does not compile clean under
nullable reference types and warnings-as-errors is a build failure, not a review comment.

## Packages

| Package | Contents |
|---|---|
| `AcpKit.Core` | JSON-RPC 2.0 over newline-delimited JSON. No protocol types. |
| `AcpKit.Protocol.V1` / `.V2` | Models, converters, method tables, serialization contexts |
| `AcpKit.Client.V1` / `.V2` | `IAcpClient` and `AgentConnection` — for building a client |
| `AcpKit.Agent.V1` / `.V2` | `IAcpAgent` and `ClientConnection` — for building an agent |

**The v2 packages are for preview only.** No agent implements ACP v2 yet: the schema is
still tagged `2.0.0-alpha`, the reference Rust SDK gates v2 behind an opt-in
`unstable_protocol_v2` feature, and the TypeScript SDK — which most agents build on — has no
v2 surface at all. Every agent measured answers in v1 shapes. Use `.V1` for anything real;
`.V2` is there to develop against and to be ready when the ecosystem moves.

Each protocol package also carries the line's **unstable** surface under a `.Unstable`
namespace. That surface is exempt from SemVer: it tracks schema features that have not
stabilised, and it is substantially larger than the stable one — v1 unstable has 262
definitions against stable's 142.

## Writing a client

You implement `IAcpClient` — the methods the *agent* may call on you — and hold an
`AgentConnection` to call the agent.

```csharp
using AcpKit;
using AcpKit.Protocol.V1;

// Whatever ACP agent you are driving, launched so it speaks the protocol over stdio.
var agent = Process.Start(new ProcessStartInfo("your-agent", "acp")
{
    RedirectStandardInput = true,
    RedirectStandardOutput = true,
})!;

await using var connection = AgentConnection.Create(
    input: agent.StandardOutput.BaseStream,
    output: agent.StandardInput.BaseStream,
    handler: new MyClient(),
    onDiagnostic: Console.Error.WriteLine);

// The pump must be running before you call anything: responses arrive on it.
var pump = connection.RunAsync();

await connection.InitializeAsync(new InitializeRequest
{
    ProtocolVersion = new ProtocolVersion(1),
    ClientInfo = new Implementation { Name = "my-editor", Version = "1.0.0" },
});

var session = await connection.SessionNewAsync(
    new NewSessionRequest { Cwd = Environment.CurrentDirectory, McpServers = [] });

var turn = await connection.SessionPromptAsync(new PromptRequest
{
    SessionId = session.SessionId,
    Prompt = [new ContentBlockText { Value = new TextContent { Text = "What changed?" } }],
});

Console.WriteLine(turn.StopReason.Value);   // v1: the turn is over when this returns
```

`MyClient` receives everything the agent streams back:

```csharp
sealed class MyClient : IAcpClient
{
    public Task SessionUpdateAsync(SessionNotification n, CancellationToken ct)
    {
        // Discriminated unions are real types, so the compiler enumerates the cases for you.
        switch (n.Update)
        {
            case SessionUpdateAgentMessageChunk chunk
                when chunk.Value.Content is ContentBlockText text:
                Console.Write(text.Value.Text);
                break;
            case SessionUpdateToolCall call:
                Console.WriteLine($"tool: {call.Value.Title}");
                break;
            case SessionUpdateUnknown unknown:
                // A newer agent sent something this version does not define. The payload is
                // intact, so it can be logged, stored, or forwarded rather than dropped.
                Console.Error.WriteLine($"unhandled update: {unknown.Kind}");
                break;
        }

        return Task.CompletedTask;
    }

    public Task<RequestPermissionResponse> SessionRequestPermissionAsync(
        RequestPermissionRequest r, CancellationToken ct) =>
        Task.FromResult(new RequestPermissionResponse
        {
            Outcome = new RequestPermissionOutcomeSelected
            {
                Value = new SelectedPermissionOutcome { OptionId = r.Options[0].OptionId },
            },
        });

    // ...the remaining members are fs/* and terminal/* in v1; throw
    // AcpException(AcpErrorCode.MethodNotFound, ...) for any you do not offer.
}
```

Writing an **agent** is the mirror image: implement `IAcpAgent` and hold a `ClientConnection`.

A host that must keep a verbatim transcript of the agent's stdout cannot reconstruct it from
decoded messages: banners, blank lines, and parse failures never become typed traffic. Set
`AcpPeerOptions.OnFrame`. It runs on every inbound NDJSON line, before parse, and the memory
is borrowed for the duration of the call — copy it to keep it.

```csharp
var peer = new AcpPeer(input, output, new AcpPeerOptions
{
    OnFrame = frame => transcript.Write(frame.Span),
    OnDiagnostic = Console.Error.WriteLine,
});
```

## Do not trust the reported protocol version

The obvious way to decide which line an agent speaks is to read `protocolVersion` from the
`initialize` response. It does not work. At least one shipping agent echoes back whatever
version it is sent — answering `2` to a request for 2, and `99` to a request for 99 — while
always replying with v1 field names. A client that believed the number would try to read a v1
body as v2 and fail on the first field it could not find.

What identifies the line is the shape of the response, because v2 renamed the fields that
carry it: `agentCapabilities` became `capabilities`, and `agentInfo` became a required `info`.
`AcpHandshake` reads those:

```csharp
using var result = await connection.Peer.SendRawRequestAsync(
    AcpHandshake.InitializeMethod, parametersJson);

var shape = AcpHandshake.DetectShape(result.RootElement);      // V1, V2, or Unknown
var declared = AcpHandshake.DeclaredVersion(result.RootElement);

if (AcpHandshake.VersionDisagreesWithShape(result.RootElement))
{
    logger.LogWarning("Agent reported v{Declared} but speaks {Shape}.", declared, shape);
}
```

Then build the typed connection for the line you actually got.

## Three-state fields in v2

ACP v2's update model rests on the difference between a field that was **omitted**, one sent
as **null**, and one carrying a **value** — leave unchanged, clear, and replace. A nullable
property collapses the first two, so those fields are `Patch<T>`:

```csharp
var update = new ToolCallUpdate
{
    ToolCallId = new ToolCallId("call-1"),
    Title = Patch<string>.Set("Reading config"),   // replace
    Kind = Patch<ToolKind>.Cleared,                // clear
    // Status omitted entirely                     // leave alone
};

// Folding an update into the view of the tool call you are keeping. Generated models are
// init-only — a decoded message is a record of what arrived, not something to edit — so the
// state you maintain is your own type:
myToolCall.Title = update.Title.ApplyTo(myToolCall.Title);
myToolCall.Kind = update.Kind.ApplyTo(myToolCall.Kind);
```

`Patch<T>.Unset` is `default`, so an untouched field is simply not written to the wire.

## Open enums

Every enum-like string in ACP is extensible: `_`-prefixed values are vendor extensions and
other unknown values are reserved for future protocol versions. Both must survive a round
trip, which a C# `enum` cannot represent, so they are structs over `string`:

```csharp
if (reason == StopReason.EndTurn) { /* ... */ }

if (!reason.IsKnown)
{
    logger.LogInformation("Unrecognised stop reason {Reason} (vendor: {Vendor}).",
        reason.Value, reason.IsExtension);
}
```

## Layout

| Path | What |
|---|---|
| `schema/{v1,v2}/{stable,unstable}/` | Vendored ACP schemas, pinned by `VERSION` |
| `src/AcpKit.Core/` | Hand-written JSON-RPC 2.0 over newline-delimited JSON |
| `src/AcpKit.Protocol.V*/` | Generated models, converters, method tables |
| `src/AcpKit.Client.V*/` | Generated client connection and dispatch |
| `src/AcpKit.Agent.V*/` | Generated agent connection and dispatch |
| `tools/AcpKit.Generator/` | The schema-to-C# generator |
| `tests/AcpKit.Conformance/` | In-process client↔agent scenarios and the coverage gate |
| `tests/AcpKit.Live/` | Drives a real agent binary. Costs money. |

Everything under `src/**/Generated/` is produced by the generator and checked in, so a schema
bump shows up as a reviewable diff rather than an opaque rebuild.

## Regenerating

```sh
dotnet run --project tools/AcpKit.Generator -- generate      # rewrite the generated sources
dotnet run --project tools/AcpKit.Generator -- inspect       # what each schema classifies as
dotnet run --project tools/AcpKit.Generator -- show <Type>   # how one definition was modelled
```

CI fails if regenerating produces a diff, so the checked-in output always matches the schema
it claims to come from. A scheduled workflow tracks the newest `schema-v1.*` and
`schema-v2.*` tags weekly and opens a pull request when the output changes.

## Releasing

Package versions come from git tags, via MinVer — there is no version number in the source to
bump, and an untagged commit builds as a prerelease off the last tag.

Merging a pull request does not release anything. A draft release accumulates as work merges;
publishing that draft creates the tag and triggers the publish workflow, which pushes to NuGet
using trusted publishing. Cutting a release is one button, and choosing not to cut one is the
default.

## Testing

```sh
dotnet run --project tests/AcpKit.Conformance    # transport, protocol, and coverage
```

Three oracles, no unit tests:

- **Compilation.** The generator compiles its own output before writing it.
- **In-process end-to-end.** A generated client drives a generated agent over a real
  transport. Nothing is mocked.
- **A schema-derived corpus.** 1,153 payloads — one per type, one per union arm, one per
  enum member, plus a vendor extension value per enum — round-trip through the generated
  contracts. Every declared type must have one, so surface cannot ship untested.

Method coverage is measured by sniffing method names off the wire rather than by asking
scenarios what they cover. Uncovered methods are listed with reasons; a waiver that becomes
redundant also fails, because stale waivers are how a coverage gate quietly stops meaning
anything.

The live tier needs an agent and a credential:

```sh
ACPKIT_AGENT=your-agent ACPKIT_AGENT_ARGS=acp \
  dotnet run --project tests/AcpKit.Live
```

`ACPKIT_AGENT` and `ACPKIT_AGENT_ARGS` name the binary to drive; whatever credentials and
model that agent needs come from its own environment variables. Because no agent implements
v2, this tier exercises the v1 client only.

## License

Apache-2.0. See [LICENSE](LICENSE). The ACP schemas vendored under `schema/` come from
[agentclientprotocol/agent-client-protocol](https://github.com/agentclientprotocol/agent-client-protocol)
and are Apache-2.0 as well; see [NOTICE](NOTICE).
