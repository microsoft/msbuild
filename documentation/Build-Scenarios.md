# Build Scenarios

MSBuild is used in a huge variety of ways, each with its own behavioral and performance characteristics.

## Batch builds

The most straightforward invocation of MSBuild is a batch build--a direct invocation of `dotnet build` or `MSBuild.exe` on a project or set of projects. This can run from a clean state, requiring all tools to run as well as all of MSBuild's overhead computation.

Depending on the size of the codebase, these builds are usually dominated by tool execution, such as the C# compiler. Other build tasks like `Copy` and ASP.NET static-asset handling are also often contributors.

This scenario is critical for:

* Clean repo checkout
* CI/PR/Official builds (usually from clean though not always)

## Incremental batch builds

MSBuild tries to avoid doing unnecessary work when projects have already been built. It uses file input and output lists to avoid executing targets when possible. Unfortunately, checking whether a target is up to date still has costs, and the overhead of MSBuild evaluation can also be a major factor in incremental build performance.

In the limit, a fully-up-to-date build is instructive for the MSBuild team, because an *ideal* fully up-to-date build would take no time to run. If you do `dotnet build && dotnet build`, any time spent in the second one should be overhead (barring an incrementality bug in the projects).

This scenario is critical for:

* Dev inner loop
* Optimizing constant overhead

## IDE load

IDEs like Visual Studio and C# Dev Kit need to understand

* What sources to parse and analyze
* What build configurations exist
* What compiler flags each configuration will set.

Ideally, this would result in a model (to power IntelliSense completion, go-to-definition, and other editor tools) that *exactly* matches the way compilation will be executed in a batch build (and ideally a batch build that produces deployable bits). However, the IDE load scenario is extremely performance sensitive, since a repo or solution may contain hundreds of projects that need to be understood before the load (and editor functionality) is complete.

Because MSBuild is so dynamic, the required information is generally extracted via a [design-time build](), which runs a subset of the build: ideally enough to generate the information required to run a compiler, but no more. Differences between design-time build and batch build can result in errors that are extremely confusing to the users (editor squiggles that don't show up in builds, or vice versa).

## IDE build

After projects are loaded into an IDE, the user may invoke a build directly or indirectly (for instance with a start-debugging command). These builds can differ from batch builds dramatically, since IDEs like Visual Studio may choose to build projects in isolation. This can be motivated by supporting non-MSBuild project types (as Visual Studio does), or by a desire to reduce IDE incremental build time by avoiding MSBuild overhead using [fast up-to-date checks]() that decide whether to invoke a project build based on a higher-order model of the project-level inputs and outputs.

## Project-level caching

Repos using MSBuild's project caching plugin system are [graph-based]() builds that consult the cache plugin before starting to build each project. The cache plugin can provide cached results for a project based on the state of the system or allow the build to continue (and possibly add results to a cache).

Because the build is graph-based, graph-construction time (dominated by evaluation time) is a major factor for these builds when up to date or mostly cached.

## Higher-order build systems

MSBuild can be invoked as part of a higher-order build system like [CloudBuild]() or [BuildXL](). Generally in these cases the higher-order build system often constructs and walks the graph, making MSBuild evaluation and execution time (of only out-of-date projects) the critical path.
