# Telemetry via OpenTelemetry design

VS OTel provide packages compatible with ingesting data to their backend if we instrument it via OpenTelemetry traces (System.Diagnostics.Activity).
VS OTel packages are not open source so we need to conditionally include them in our build only for VS and MSBuild.exe

[Onepager](https://github.com/dotnet/msbuild/blob/main/documentation/specs/proposed/telemetry-onepager.md)

## Concepts

It's a bit confusing how things are named in OpenTelemetry and .NET and VS Telemetry and what they do.

| OTel concept | .NET/VS | Description |
| --- | --- | --- |
| Span/Trace | System.Diagnostics.Activity |  Trace is a tree of Spans. Activities can be nested.|
| Tracer | System.Diagnostics.ActivitySource | Creates and listens to activites.  |
| Processor/Exporter | VS OTel provided default config | filters and saves telemetry as files in a desired format |
| TracerProvider | OTel SDK TracerProvider | Singleton that is aware of processors, exporters and Tracers (in .NET a bit looser relationship because it does not create Tracers just hooks to them) |
| Collector | VS OTel Collector | Sends to VS backend, expensive to initialize and finalize |

## Requirements

### Performance

- If not sampled, no infra initialization overhead.
- Avoid allocations when not sampled.
- Has to have no impact on Core without opting into tracing, small impact on Framework

### Privacy

- Hashing data points that could identify customers (e.g. names of targets)
- Opt out capability

### Security

- Providing or/and documenting a method for creating a hook in Framework MSBuild
- If custom hooking solution will be used - document the security implications of hooking custom telemetry Exporters/Collectors in Framework
- other security requirements (transportation, rate limiting, sanitization, data access) are implemented by VS Telemetry library or the backend

### Data handling

- Implement head [Sampling](https://opentelemetry.io/docs/concepts/sampling/) with the granularity of a MSBuild.exe invocation/VS instance.
- VS Data handle tail sampling in their infrastructure not to overwhelm storage with a lot of build events.

#### Data points

The data sent via VS OpenTelemetry is neither a subset neither a superset of what is sent to SDK telemetry and it is not a purpose of this design to unify them.

##### Basic info

- Build duration
- Host
- Build success/failure
- Version
- Target (hashed)

##### Evnironment

- SAC (Smart app control) enabled

##### Features

- BuildCheck enabled

The design allows for easy instrumentation of additional data points.

## Core `dotnet build` scenario

- Telemetry should not be collected via VS OpenTelemetry mechanism because it's already collected in sdk.
- opt in to initialize the ActivitySource to avoid degrading performance.
- [baronfel/otel-startup-hook: A .NET CLR Startup Hook that exports OpenTelemetry metrics via the OTLP Exporter to an OpenTelemetry Collector](https://github.com/baronfel/otel-startup-hook/) and similar enable collecting telemetry data locally by listening to the ActivitySource name defined in MSBuild.

## Standalone MSBuild.exe scenario

- Initialize and finalize in Xmake.cs
	- ActivitySource, TracerProvider, VS Collector
		- overhead of starting VS collector is fairly big (0.3s on Devbox)[JanProvaznik/VSCollectorBenchmarks](https://github.com/JanProvaznik/VSCollectorBenchmarks)
			- head sampling should avoid initializing if not sampled

## VS scenario

- VS can call `BuildManager` in a thread unsafe way the telemetry implementation has to be mindful of [BuildManager instances acquire its own BuildTelemetry instance by rokonec · Pull Request #8444 · dotnet/msbuild](https://github.com/dotnet/msbuild/pull/8444)
	- ensure no race conditions in initialization
	- only 1 TracerProvider with VS defined processing should exist
- Visual Studio should be responsible for having a running collector, we don't want this overhead in MSBuild and eventually many components can use it

## Implementation and MSBuild developer experience

### ActivitySource names

...

### Sampling

Our estimation from VS and SDK data is that there are 10M-100M build events per day.
For proportion estimation (of fairly common occurence in the builds), with not very strict confidnece (95%) and margin for error (5%) sampling 1:25000 would be enough.

- this would apply for the DefaultActivitySource
- other ActivitySources could be sampled more frequently to get enough data
- Collecting has a cost, especially in standalone scenario where we have to start the collector. We might decide to undersample in standalone to avoid performance frequent impact.
- We want to avoid that cost when not sampled, therefore we prefer head sampling.
- Enables opt-in and opt-out for guaranteed sample or not sampled.
- nullable ActivitySource, using `?` when working with them, we can be initialized but not sampled -> it will not reinitialize but not collect telemetry.

### Initialization at entrypoints

- There are 2 entrypoints:
    - for VS in BuildManager.BeginBuild
    - for standalone in Xmake.cs Main

### Exiting

Force flush TracerProvider's exporter in BuildManager.EndBuild.
Dispose collector in Xmake.cs at the end of Main.

### Configuration

- Class that's responsible for configuring and initializing telemetry and handles optouts, holding tracer and collector.
- Wrapping source so that it has correct prefixes for VS backend to ingest.

### Instrumenting

2 ways of instrumenting:

#### Instrument areas in code running in the main process

```csharp
using (Activity? myActivity = OpenTelemetryManager.DefaultActivitySource?.StartActivity(TelemetryConstants.NameFromAConstantToAvoidAllocation))
{
// something happens here

// add data to the trace
myActivity?.WithTag("SpecialEvent","fail")
}
```

Interface for classes holding telemetry data

```csharp
IActivityTelemetryDataHolder data = new SomeData();
...
myActivity?.WithTags(data);
```

#### Default Build activity in EndBuild

- this activity would always be created at the same point when sdk telemetry is sent in Core
- we can add data to it that we want in general builds
- the desired count of data from this should control the sample rate of DefaultActivitySource

#### Multiple Activity Sources

We can create ActivitySources with different sample rates. Ultimately this is limited by the need to initialize a collector.

We potentially want apart from the Default ActivitySource:

1. Other activity sources with different sample rates (in order to get significant data for rarer events such as custom tasks).
2. a way to override sampling decision - ad hoc starting telemetry infrastructure to catch rare events

- Create a way of using a "HighPrioActivitySource" which would override sampling and initialize Collector in MSBuild.exe scenario/tracerprovider in VS.
- this would enable us to catch rare events


## Uncertainties

- Configuring tail sampling in VS telemetry server side infrastructure.
- Sampling rare events details.
- In standalone we could start the collector async without waiting which would potentially miss some earlier traces (unlikely to miss the important end of build trace though) but would degrade performance less than waiting for it's startup. The performance and utility tradeoff is not clear.
- Can collector startup/shutdown be faster?
- We could let users configure sample rate via env variable, VS profile
- Do we want to send antivirus state? Figuring it out is expensive: https://github.com/dotnet/msbuild/compare/main...proto/get-av ~100ms
- ability to configure the overal and per-namespace sampling from server side (e.g. storing it in the .msbuild folder in user profile if different then default values set from server side - this would obviously have a delay of the default sample rate # of executions)
