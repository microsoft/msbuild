# Static Graph

## What is static graph for?

As a repo gets bigger and more complex, weaknesses in MSBuild's scheduling and incrementality models become more apparent. MSBuild's static graph features are intended to ameliorate these weaknesses while remaining as compatible as possible with existing projects and SDKs.

MSBuild projects can refer to other projects by using the `MSBuild` task to execute targets in another project and return values. In `Microsoft.Common.targets`, `ProjectReference` items are transformed into `MSBuild` task executions in order to provide a user-friendly interface: "reference the output of these projects".

### Weakness of the old model: project-level scheduling

Because references to other projects aren't known until a target in the referencing project calls the `MSBuild` task, the MSBuild engine cannot start working on building referenced projects until the referencing project yields. For example, if project `A` depended on `B`, `C`, and `D` and was being built with more-than-3 way parallelism, an ideal build would run `B`, `C`, and `D` in parallel with the parts of `A` that could execute before the references were available.

Today, the order of operations of this build are:

1. `A` completes evaluation and starts building, doing isolated work until it gets to `ResolveProjectReferences`.
1. In parallel, `B`, `C`, and `D` run the requested targets.
1. `A` resumes building and completes.

With graph-aware scheduling, this becomes:

1. `A`, `B`, `C`, and `D` evaluate in parallel.
1. `B`, `C`, and `D` build to completion in parallel.
1. `A` builds, and instantly gets cached results for the `MSBuild` task calls in `ResolveProjectReferences`

### Weakness of the old model: incrementality

[Incremental build](https://docs.microsoft.com/visualstudio/msbuild/incremental-builds) (that is, "redo only the parts of the build that would produce different outputs compared to the last build") is the most powerful tool to reduce build times and increase developer inner-loop speed.

MSBuild supports incremental builds by allowing a target to be skipped if the target's outputs are up to date with its inputs. This allows tools like the compiler to be skipped when possible. But since the incrementality is at the target level, MSBuild must fully evaluate the project and walk through all targets, running those that are out of date or that don't specify inputs and outputs.

Consider a simple solution with a library and an application that depends on the library. Suppose you build, then make a change in the application's source code, then build again.

The second build will:

1. Build the library project, skipping all targets that define inputs and outputs.
1. Build the application project.

But using higher-level knowledge, we can see a more-optimal build:

1. Skip everything involving the library project, because _none_ of its inputs have changed.
1. Build only the application project.

Visual Studio offers a ["fast up-to-date check"](https://github.com/dotnet/project-system/blob/cd275918ef9f181f6efab96715a91db7aabec832/docs/up-to-date-check.md) system that gets closer to the latter, but MSBuild itself does not.

### Weakness of the old model: caching and distributability

For very large builds, including many Microsoft products, the fact that MSBuild can build in parallel only on a single machine is a major impediment, even if incrementality is addressed.

Ideally, a build could span multiple computers, and each could use results generated on another machine as inputs to its own build projects. In addition, if all of a project's inputs remain unchanged, the system would ideally reuse the outputs of the project, even if they were built long ago on another computer.

Microsoft has an internal build system, [CloudBuild](https://www.microsoft.com/research/publication/cloudbuild-microsofts-distributed-and-caching-build-service/), that supports this and has proven that it is effective, but is heuristic-based and requires maintenance.

MSBuild static graph features make it easier to implement a system like CloudBuild by building operations like graph construction and output caching into MSBuild itself.

## What is static graph?

MSBuild's static graph extends the MSBuild engine and APIs with new functionality to improve on these weaknesses:

- The ability to [construct a directed acyclic graph of MSBuild projects](#project-graph) given an entry point (solution or project).
- The ability to consider that graph when scheduling projects for build.
- The ability to cache MSBuild's internal build results (metadata about outputs, not the outputs themselves) across build invocations.
- The ability to [enforce restrictions on builds](#isolated-builds) to ensure that the graph is correct and complete.

Static graph functionality can be used in three ways:

- On the command line with `-graph` (and equivalent API).
  - This gets the scheduling improvements for well-specified projects, but allows underspecified projects to complete without error.
- On the command line with `-graph -isolate` (and equivalent API).
  - This gets the scheduling improvements and also enforces that the graph is correct and complete. In this mode, MSBuild will produce an error if there is an `MSBuild` task invocation that was not known to the graph ahead of time.
- As part of a higher-order build system that uses [single project isolated builds](#single-project-isolated-builds) to provide caching and/or distribution on top of the built-in functionality. The only known implementation of this system is Microsoft-internal currently.

"Correct and complete" here means that the static graph can be used to accurately predict all targets that need to be built for all projects in the graph, and all of the references between projects. This is required for the higher-order build system scenario, because an unknown reference couldn't be satisfied at runtime (as it is in regular MSBuild and `-graph` with no `-isolate` scenarios).

## Design documentation

### Design goals

- Stock projects can build with "project-level build" and if clean onboard to MS internal build engines with cache/distribution
- Stock projects will be "project-level build" clean.
- Add determinism to MSBuild w.r.t. project dependencies. Today MSBuild discovers projects just in time, as it finds MSBuild tasks. This means there’s no guarantee that the same graph is produced two executions in a row, other than hopefully sane project files. With the static graph, you’d know the shape of the build graph before the build starts.
- Potential perf gain in graph scheduling
- Increase perf of interaction between MSBuild and higher-order build engines (eg. MS internal build engines) - reuse evaluation and MSBuild nodes
- **Existing functionality must still work. This new behavior is opt-in only.**

## Project Graph

### Constructing the project graph
Calculating the project graph will be very similar to the MS internal build engine's existing Traversal logic. For a given evaluated project, all project references will be identified and recursively evaluated (with deduping).
Project references are identified via the `ProjectReference` item.

A node in the graph is a tuple of the project file and global properties. Each (project, global properties) combo can be evaluated in parallel.

Transitive project references are opt-in per project. Once a project opts-in, transitivity is applied for all ProjectReference items.
A project opt-ins by setting the property `AddTransitiveProjectReferencesInStaticGraph` to true.

### Build dimensions

Build dimensions can be thought of as different ways to build a particular project. For example, a project can be built Debug or Retail, x86 or x64, for .NET Framework 4.7.1 or .NET Core 2.0.

Because we are using ProjectReferences to determine the graph, we will need to duplicate the mapping of ProjectReference metadata to global properties given to the MSBuild task. This means that we need to couple the engine to the common MSBuild targets, which means that whenever we change those targets we need to update the project graph construction code as well.

**OPEN ISSUE:** What about MSBuild calls which pass in global properties not on the ProjectReference? Worse, if the global properties are computed at runtime? The sfproj SDK does this with PublishDir, which tells the project where to put its outputs so the referencing sfproj can consume them. We may need to work with sfproj folks and other SDK owners, which may not be necessarily a bad thing since already the sfproj SDK requires 2 complete builds of dependencies (normal ProjectReference build + this PublishDir thing) and can be optimized. A possible short term fix: Add nodes to graph to special case these kinds of SDKs. This is only really a problem for isolated builds.

The graph also supports multiple entry points, so this enables scenarios where projects build with different platforms in one build. For example, one entry point could be a project with global properties of `Platform=x64` and another entry point might be that same project with `Platform=x86`. Because the project graph understands the metadata on project references as well, including `GlobalPropertiesToRemove`, this also enables the notion of "platform agnostic projects" which should only build once regardless of the platform. Note that multiple entry points will not be useable from the command-line due to the inability to express it concisely, and so programmatic access must be used in that case.

For example, if project A had a project reference to project B with `GlobalPropertiesToRemove=Platform`, and we wanted to build project A for x86 and x64 so used both as entry points, the graph would consist of 3 nodes: project A with `Platform=x86`, project A with `Platform=x64`, and project B with no global properties set.

#### Multitargeting

<!-- definition and TF example-->
Multitargeting refers to projects that specify multiple build dimensions applicable to themselves. For example, `Microsoft.Net.Sdk` based projects can target multiple target frameworks (e.g. `<TargetFrameworks>net472;netcoreapp2.2</TargetFrameworks>`). As discussed, build dimensions are expressed as global properties. Let's call the global properties that define the multitargeting set as the multitargeting global properties.

<!-- how it works: outer builds and inner builds -->
Multitargeting is implemented by having a project reference itself multiple times, once for each combination of multitargeting global properties. This leads to multiple evaluations of the same project, with different global properties. These evaluations can be classified in two groups
1.  Multiple inner builds. Each inner build is evaluated with one set of multitargeting global properties (e.g. the `TargetFramework=net472` inner build, or the `TargetFramework=netcoreapp2.2` inner build).
2.  One outer build. This evaluation does not have any multitargeting global properties set. It can be viewed as a proxy for the inner builds. Other projects query the outer build in order to learn the set of valid multitargeting global properties (the set of valid inner builds). When the outer build is also the root of the project to project graph, the outer build multicasts the entry target (i.e. `Build`, `Clean`, etc) to all inner builds.

<!-- contract with the graph -->

In order for the graph to represent inner and outer builds as nodes, it imposes a contract on what multitargeting means, and requires the multitargeting supporting SDKs to implement this contract.

Multitargeting supporting SDKs MUST implement the following properties and semantics:
- `InnerBuildProperty`. It contains the property name that defines the multitargeting build dimension.
- `InnerBuildPropertyValues`. It contains the property name that holds the possible values for the `InnerBuildProperty`.
- Project classification:
  - *Outer build*, when `$($(InnerBuildProperty))` is empty AND  `$($(InnerBuildPropertyValues))` is not empty.
  - *Dependent inner build*, when both `$($(InnerBuildProperty))` and  `$($(InnerBuildPropertyValues))` are non empty. These are inner builds that were generated from an outer build.
  - *Standalone inner build*, when `$($(InnerBuildProperty))` is not empty and `$($(InnerBuildPropertyValues))` is empty. These are inner builds that were not generated from an outer build.
  - *Non multitargeting build*, when both `$($(InnerBuildProperty))` and  `$($(InnerBuildPropertyValues))` are empty.
- Node edges
  - When project A references multitargeting project B, and B is identified as an outer build, the graph node for project A will reference both the outer build of B, and **all the inner builds of B**. The edges to the inner builds are **speculative**, as at build time only one inner build gets referenced. However, the graph cannot know at evaluation time which inner build will get chosen.
  - When multitargeting project B is a root, then the outer build node for B will reference **all the inner builds of B**.
  - For multitargeting projects, the `ProjectReference` item gets applied only to inner builds. An outer build cannot have its own distinct `ProjectReference`s, it is the inner builds that reference other project files, not the outer build. This constraint might get relaxed in the future via additional configuration, to allow outer build specific references.

These specific rules represent the minimal rules required to represent multitargeting in `Microsoft.Net.Sdk`. As we adopt SDKs whose multitargeting complexity that cannot be expressed with the above rules, we'll extend the rules.
For example, `InnerBuildProperty` could become `InnerBuildProperties` for SDKs where there's multiple multitargeting global properties.

For example, here is a trimmed down `Microsoft.Net.Sdk` multitargeting project:
```xml
<Project Sdk="Microsoft.Net.Sdk">
  <!-- This property group is defined in the sdk -->
  <PropertyGroup>
    <InnerBuildProperty>TargetFramework</InnerBuildProperty>
    <InnerBuildPropertyValues>TargetFrameworks</InnerBuildPropertyValues>
  </PropertyGroup>

  <!-- This property group is defined in the project file-->
  <PropertyGroup>
    <TargetFrameworks>net472;netcoreapp2.2</TargetFrameworks>
  </PropertyGroup>
</Project>
```

To summarize, there are two main patterns for specifying build dimensions:
1. Multitargeting based. A multitargeting project self describes supported build dimensions. In this case the SDK needs to specify the multitargeting build dimensions. The graph then extracts innerbuilds from a given outer build. For example, the `TargetFramework` build dimension gets specified this way.
2. Global Property based: A top level set of global properties get applied to the graph entrypoints and get propagated downward through the graph. For example, the `Configuration` and `Platform` build dimensions get specified this way.

Why does an outerbuild need to generate speculative edges to all of its innerbuilds? Why can't it use nuget to prune the speculative edges down to the compatible set?
- One big design constraint we imposed on static graph was to keep it agnostic of SDK implementation details. So the graph must not know about particular details of one language's SDK. We wanted a generic design that all language SDKs can leverage. We considered that calling nuget to get the compatible TargetFramework values breaks this rule, as both the concept of "nuget" and the concept of "TargetFramework" are implementation details of the .net SDK. If someone were to write a Java SDK, would "calling nuget to get the compatible TargetFramework" still be relevant? A solution to this is to allow SDKs to configure the graph with an extension point on "how to collapse multiple speculative innerbuild edges into a smaller compatible set", but we didn't have the time to design it yet.
- There is a conflicting need between build everything or just building a "TF slice" through the graph. Outer loop builds (CI builds) that publish binaries need to build all the packages for all the supported TFs, so they need the graph to express all possible combinations. Inner loop builds (dev-at-work) can be sliced down to only the TF that the dev is working on in order to reduce build times. Again, we didn't have time to design how to express these two things so we went with "express everything" because that allows both scenarios to work.

### Executing targets on a graph
When building a graph, project references should be built before the projects that reference them, as opposed to the existing msbuild scheduler which builds projects just in time.

For example if project A depends on project B, then project B should build first, then project A. Existing msbuild scheduling would start building project A, reach an MSBuild task for project B, yield project A, build project B, then resume project A once unblocked.

Building in this way should make better use of parallelism as all CPU cores can be saturated immediately, rather than waiting for projects to get to the phase in their execution where they build their project references. More subtly, less execution state needs to be held in memory at any given time as there are no paused builds waiting to be unblocked. Knowing the shape of the graph may be able to better inform the scheduler to prevent scheduling projects that could run in parallel onto the same node.

Note that graph cycles are disallowed, even if they're using disconnected targets. This is a breaking change, as today you can have two projects where each project depends on a target from the other project, but that target doesn't depend on the default target or anything in its target graph.

#### Command line
`msbuild /graph` - msbuild will create a static graph from the entry point project and build it in topological order with the specified targets. Targets to call on each node are inferred via the rules in [this section](#inferring-which-targets-to-run-for-a-project-within-the-graph).

#### APIs

[BuildManager.PendBuildRequest(GraphBuildRequestData requestData)](https://github.com/microsoft/msbuild/blob/37c5a9fec416b403212a63f95f15b03dbd5e8b5d/src/Build/BackEnd/BuildManager/BuildManager.cs#L676)

### Inferring which targets to run for a project within the graph
In the classic MSBuild build (i.e. execution of targets), the referencing project chooses which targets to call on the referenced projects and may call into a project multiple times with different target lists and global properties (examples in [project reference protocol](../ProjectReference-Protocol.md)). This is a top-down traversal of dependencies. These calls are made via the [MSBuild task](https://docs.microsoft.com/en-us/visualstudio/msbuild/msbuild-task?view=vs-2019). When building a graph, projects are built before the projects that reference them. This is a bottom-up traversal. Therefore the graph needs to determine the list of targets to execute on a specific project `B` **before** building the referencing projects that reference `B`.

The static graph contains the structural information on which reference projects a referencing project depends on. But it does not contain information on what "depends" means. At build time "depends" means that a referencing evaluated project will call a subset of reference evaluations with some targets. Subset because the static graph is an inferred graph, therefore there are ambiguities during graph construction, and thus it needs to be conservative and represent a superset of the "runtime graph". The "runtime graph" is the actual graph that gets executed during a real build. We cannot know the runtime graph because that would require us to analyze msbuild xml code inside of targets in order to find the `MSBuild task` invocations. This means doing heavy program analysis, like symbolic execution. That would make things very complicated, slower, and would probably introduce even more ambiguity, so a larger superset conservative graph. So we kept it simple and only looked at evaluation time msbuild xml code (i.e. msbuild xml code outside of `<Target>` elements).
To summarize, the static graph does not have insights into the `MSBuild task` callsites. It does not know callsite specific information such as the `Targets="Foo;Bar"` or `Properties="Foo=Bar"` `MSBuild task` attributes.
Since the graph does not have access to MSBuild task callsites, it does not know what targets will get called for a given graph edge. 

To infer target information we use a flow analysis to propagate target information down the graph. The flow analysis uses the `ProjectReferenceTargets` protocol (described further down) to infer how one incoming target on a graph node (e.g. `Build`) generates multiple outgoing targets to its referenced nodes (e.g. `GetTargetFrameworks`, `GetNativeManifest`, `Build`).
SDKs **must** explicitly describe the project-to-project calling patterns via the `ProjectReferenceTargets` protocol in such a way that a graph based build can correctly infer the entry targets for a graph node.

Each project needs to specify the project reference protocol targets it supports, in the form of a target mapping: `incoming target -> outgoing target list`. The `incoming target` represents the project reference entry target, and the `outgoing target list` represents the list of targets that `incoming target` ends up calling on each referenced project.

For example, a simple recursive rule would be `A -> A`, which says that a project called with target `A` will call target `A` on its referenced projects. Here's an example execution with two nodes:

```
Execute target A+-->Proj1   A->A
                    +
                    |
                    | A
                    |
                    v
                    Proj2   A->A
```

Proj1 depends on Proj2, and we want to build the graph with target `A`. Proj1 gets inspected for the project reference protocol for target `A` (represented to the right of Proj1). The protocol says the referenced projects will be called with `A`. Therefore Proj2 gets called with target `A`. After Proj2 builds, Proj1 then also builds with `A` because Proj1 is an entry point and `A` is what was requested by the user.

A project reference protocol may contain multiple targets, for example `A -> B, A`. This means that building `A` on the referencing project will lead to `B` and `A` getting called on the referenced projects. If all nodes in the graph repeat the same rule, then the rule is repeated recursively on all nodes. However, a project can choose to implement the protocol differently. In the following example, the entry targets are:
- Proj4 is called with targets `B, A, C, D`. On multiple references, the incoming targets get concatenated. The order of these target lists does not matter, as MSBuild has non-deterministic p2p ordering, however the order within the target lists does. IE. `B, A, C, D` and `C, D, B, A` are valid, while `A, B, C, D` is not.
- Proj3 and Proj2 get called with `B, A`, as specified by the rule in Proj1.
- Proj1 builds with `A`, because it's the root of the graph.

```
            A+-->Proj1   A->B, A
                 /    \
           B, A /      \ B, A
               /        \
              v          v
  A->B, A   Proj3      Proj2   A->C, D
             \            /
        B, A  \          /  C, D
               \        /
                \      /
                 v    v
                 Proj4   A->B, A
```

The common project reference protocols (Build, Rebuild, Restore, Clean) will be specified by the common props and targets file in the msbuild repository. Other SDKs can implement their own protocols (e.g. ASPNET implementing Publish).

Here are the rules for the common protocols:

`Build -> GetTargetFrameworks, <default>, GetNativeManifest, GetCopyToOutputDirectoryItems`

The default target (represented in this spec's pseudo protocol representation as `<default>`) is resolved for each project.

`Clean -> GetTargetFrameworks, Clean`

`Rebuild -> GetTargetFrameworks, Clean, <default>, GetNativeManifest, GetCopyToOutputDirectoryItems`

`Rebuild` actually calls `Clean` and `Build`, which in turn uses the concatenation of the `Clean` and `Build` mappings. `GetTargetFrameworks` is repeated so only the first call to it remains in the final target list.

Restore is a composition of two rules:
- `Restore -> _IsProjectRestoreSupported, _GenerateRestoreProjectPathWalk, _GenerateRestoreGraphProjectEntry`
- `_GenerateRestoreProjectPathWalk -> _IsProjectRestoreSupported, _GenerateRestoreProjectPathWalk, _GenerateRestoreGraphProjectEntry`

**Open Issue:** Restore is a bit complicated, and we may need new concepts to represent it. The root project calls the recursive `_GenerateRestoreProjectPathWalk` on itself to collect the referenced projects closure, and then after the recursion call returns (after having walked the graph), it calls the other targets on each returned referenced project in a non-recursive manner. So the above protocol is not a truthful representation of what happens, but it correctly captures all targets called on each node in the graph.

We'll represent the project reference protocols as `ProjectReferenceTargets` items in MSBuild. For extensibility, the target list for the core mappings will be stored as properties so that users can append to it, but the `ProjectReferenceTargets` item ultimately is what will actually be read by the ProjectGraph.

```xml
<PropertyGroup>
  <ProjectReferenceTargetsForClean>GetTargetFrameworks;Clean</ProjectReferenceTargetsForClean>
</PropertyGroup>

<ItemGroup>
  <ProjectReferenceTargets Include="Clean" Targets="$(ProjectReferenceTargetsForClean)"/>
</ItemGroup>
```

#### Multitargeting details

A multitargeting project can get called with different targets for the outer build and the inner builds. In this case, the `ProjectReferenceTargets` items containing targets for the outer build are marked with the `OuterBuild=true` metadata. Here are the rules for how targets from `ProjectReferenceTargets` get assigned to different project types:
  - *Outer build*: targets with `OuterBuild=true` metadata
  - *Dependent inner build*: targets without `OuterBuild=true` metadata
  - *Standalone inner build*: the same as non multitargeting builds.
  - *Non multitargeting build*: concatenation of targets with `OuterBuild=true` metadata and targets without `OuterBuild=true` metadata

**OPEN ISSUE:** Current implementation does not disambiguate between the two types of inner builds, leading to overbuilding certain targets by conservatively treating both inner build types as standalone inner builds.

For example, consider the graph of `A (non multitargeting) -> B (multitargeting with 2 innerbuilds) -> C (standalone inner build)`, with the following target propagation rules:
```
A -> Ao when OuterBuild=true
A -> Ai, A
```

According to the graph construction rules defined in the [multitargeting section](#multitargeting), we get the following graph, annotated with the target propagation for target `A`.

```
                   A+-->ProjA
                      /   |   \
                     /    |    \
                    /     |     \
                Ao /      |      \ Ai, A
                  /       |       \
                 /  Ai, A |        \
                v         v         v
       ProjB(outer)  ProjB(inner1)  ProjB(inner2)
                          |         /
                          |        /
                          |       /
                Ao, Ai, A |      / Ao, Ai, A
                          |     /
                          |    /
                          v   v
                          projC
```

### Underspecified graphs
The intention is that the project graph and the target lists for each node be exactly correct, however MSBuild is quite flexible and particular projects or project types may not adequately describe these for the project graph.

If a project calls into another project which either isn't represented in the graph or with a target list which isn't represented by the graph, it will fall back to classical MSBuild behavior and execute that target on the project reference just-in-time. This has the consequence of still requiring all project state be kept in memory in case any arbitrary project wants to execute targets on any other arbitrary project.

To enable further optimizations (and strictness), graph builds can run [isolated](#isolated-builds) which enforces that the graph be entirely accurate.

### Public API
This is a proposal for what the public API for ProjectGraph may look like:

```csharp
namespace Microsoft.Build.Experimental.Graph
{
    public class ProjectGraph
    {
        // Creates a graph starting at the given project file.
        public ProjectGraph(string projectFile) { }
        public ProjectGraph(string entryProjectFile, IDictionary<string, string> globalProperties) { }

        // Creates a graph starting at the given project files.
        public ProjectGraph(IEnumerable<string> projectFiles) { }
        public ProjectGraph(IEnumerable<string> entryProjectFiles, IDictionary<string, string> globalProperties) { }

        // Creates a graph starting at the given entry point(s). An entry point is a (project file, global properties) pair.
        public ProjectGraph(ProjectGraphEntryPoint entryPoint) { }
        public ProjectGraph(IEnumerable<ProjectGraphEntryPoint> entryPoints) { }

        /* Also various constructor overloads which take a ProjectCollection */

        // Nodes for the provided entry points
        IReadOnlyCollection<ProjectGraphNode> EntryPointNodes { get; }

        // All project nodes in the graph.
        IReadOnlyCollection<ProjectGraphNode> ProjectNodes { get; }
    }

    public struct ProjectGraphEntryPoint
    {
        public ProjectGraphEntryPoint(string projectFile) { }

        public ProjectGraphEntryPoint(string projectFile, IDictionary<string, string> globalProperties) { }

        // The project file to use for this entry point
        public string ProjectFile { get; }

        // The global properties to use for this entry point
        public IDictionary<string, string> GlobalProperties { get; }
    }

    public class ProjectGraphNode
    {
        // No public creation.
        internal ProjectGraphNode() { }

        // Projects which this project references.
        IReadOnlyCollection<ProjectGraphNode> ProjectReferences { get; }

        // Projects which reference this project.
        IReadOnlyCollection<ProjectGraphNode> ReferencingProjects { get; }

        // The evaluated project
        Project Project { get; }
    }
}
```

## Isolated builds
Building a project in isolation means enforcing the constraint that whenever a graph node is built, all the target calls that it does on its references **do not execute** because their results are already available. This means that any BuildResult objects for project references must be pre-computed and somehow provided as inputs to the referencing project.

If a project uses the MSBuild task, the build result must be in MSBuild's build result cache instead of just-in-time executing targets on that referenced project. If it is not in the build result cache, an error will be logged and the build will fail. If the project is calling into itself either via `CallTarget` or the MSBuild task with a different set of global properties, this will be allowed to support multitargeting and other build dimensions implemented in a similar way.

Because referenced projects and their entry targets are guaranteed to be in the cache, they will not build again. Therefore we do not need to set `/p:BuildProjectReferences=false` or any other gesture that tells SDKs to not do recursive operations.

### Isolated graph builds
When building a graph in isolated mode, the graph is used to traverse and build the projects in the right order, but each individual project is built in isolation. The build result cache will just be in memory exactly as it is today, but on cache miss it will error. This enforces that both the graph and target mappings are complete and correct.

Furthermore, running in this mode enforces that each (project, global properties) pair is executed only once and must execute all targets needed by all projects which reference that node. This gives it a concrete start and end time, which leads to some potential perf optimizations, like garbage collecting all project state (except the build results) once it finishes building. This can greatly reduce the memory overhead for large builds.

This discrete start and end time also allows for easy integration with [I/O Tracking](#io-tracking) to observe all inputs and outputs for a project. Note however that I/O during target execution, particular target execution which may not normally happen as part of a project's individual build execution, would be attributed to the project reference project rather the project with the project reference. This differs from today's behavior, but seems like a desirable difference anyway.

### Single project isolated builds
When building a single project in isolation, all project references' build results must be provided to the project externally. Specifically, the results will need to be [deserialized](#deserialization) from files and loaded into the build result cache in memory.

When MSBuild runs in isolation mode, it fails the build when it detects:
1. `MSBuild` task calls which cannot be served from the cache. Cache misses are illegal.
2. `MSBuild` task calls to project files which were not defined in the `ProjectReference` item.

Because of this, single project isolated builds is quite restrictive and is not intended to be used directly by end-users. Instead the scenario is intended for higher-order build engines which support caching and [distribution](#distribution).

There is also the possibility for these higher-order build engines and even Visual Studio to enable faster incremental builds for a project. For example, when a project's references' build results are provided via file caches (and validated as up to date by that higher-order build engine), there is no need to evaluate or execute any targets for any reference.

These incremental builds could be extended to the entire graph by keeping a project graph in memory as well as the last build result cache files for each node and whether a node's results are up to date. The higher-order build engine can then itself traverse the graph and do single project isolated builds only for projects which are not currently up to date.

Details on how isolation and cache files are implemented in MSBuild can be found [here](./static-graph-implementation-details.md).

#### APIs
Cache file information is provided via [BuildParameters](https://github.com/Microsoft/msbuild/blob/2d4dc592a638b809944af10ad1e48e7169e40808/src/Build/BackEnd/BuildManager/BuildParameters.cs#L746-L764). Input caches are applied in `BuildManager.BeginBuild`. Output cache files are written in `BuildManager.EndBuild`. Thus, the scope of the caches are one BuildManager BeginBuild/EndBuild session.

Isolation constraints are turned on via [BuildParameters.IsolateProjects](https://github.com/microsoft/msbuild/blob/b111470ae61eba02c6102374c2b7d62aebe45f5b/src/Build/BackEnd/BuildManager/BuildParameters.cs#L742). Isolation constraints are also automatically turned on if either input or output cache files are used.

#### Command line
Caches are provided to MSBuild.exe via the multi value `/inputResultsCaches` and the single value `/outputResultsCache`.
Isolation constraints are turned on via `/isolate` (they are also implicitly activated when either input or output caches are used).

#### Exempting references from isolation constraints
In certain situations one may want to exempt a reference from isolation constraints. A few potential cases:
- debugging / onboarding to isolation constraints
- exempting references whose project files are generated at build times with random names (for example, each WPF project, before the Build target, generates and builds a helper .csproj with a random file name)
- relaxing constraints for MSBuild task calling patterns that static graph cannot express (for exemple, if a project is calculating references, or the targets to call on references, at runtime via an arbitrary algorithm)

A project is exempt from isolation constraints by adding its full path to the `GraphIsolationExemptReference` item. For example, if project A.csproj references project B.csproj, the following snippet exempts B.csproj from isolation constraints while A.csproj is built:
```xml
<ItemGroup>
  <GraphIsolationExemptReference Include="/Full/Path/To/B.csproj" />
</ItemGroup>
```

A reference is exempt only in projects that add the reference in `GraphIsolationExemptReference`. If multiple projects need to exempt the same reference, all of them need to add the reference to `GraphIsolationExemptReference`.

For now, self-builds (a project building itself with different global properties) are also exempt from isolation constraints, but this behaviour is of dubious value and might be changed in the future.

## I/O Tracking
To help facilitate caching of build outputs by a higher-order build engine, MSBuild needs to track all I/O that happens as part of a build.

**OPEN ISSUE:** This isn't actually true in most scenarios. Today the MS internal build engine can wrap any arbitrary process to track the I/O that happens as part of its execution as well as its children. That's sufficient for all scenarios except compiler servers or an MSBuild server (see below). Additionally, if the MS internal build engine supports any other build type besides MSBuild (or older versions of MSBuild), it will still need to be able to detour the process itself anyway.

**NOTE**: Based on the complexity and challenges involved, the feature of I/O tracking in MSBuild is currently on hold and not scheduled to be implemented. This section intends to describe these challenges and be a dump of the current thinking on the subject.

### Detours
[Detours](https://github.com/microsoft/detours) will be used to intercept Windows API calls to track I/O. This is the same technology that [FileTracker](../../src/Utilities/TrackedDependencies/FileTracker.cs) and [FullTracking](../../src/Build/BackEnd/Components/RequestBuilder/FullTracking.cs) use as well as what the MS internal build engine ("BuildXL Tracker") uses to track I/O.

Today FileTracker and FullTracking are currently a bit specific to generating tlogs, and do not collect all the I/O operations we would want to collect like directory enumerations and probes. Additionally, the BuildXL Tracker implementation does not currently have the ability to attach to the currently running process.

Either existing implementation would require some work to fit this scenario. Because FileTracker/FullTracking aren't actively being improved unlike BuildXL Tracker, we will likely be adding the necessary functionality to BuildXL Tracker.

Elsewhere in this spec the final Detours-based file tracking implementation will simply be referred to as "Tracker".

### Isolation requirement
I/O Tracking will only be available when running isolated builds, as the current implementation of project yielding in MSBuild makes it exceedingly difficult to attribute any observed I/O to the correct project. Isolated builds make this feasible since each MSBuild node will be building exactly one project configuration at any given moment and each project configuration has a concrete start and stop time. This allows us to turn on I/O tracking for the MSBuild process and start and stop tracking with the project start and stop.

**OPEN ISSUE:** For graph-based isolated builds, project evaluation happens in parallel on the main node. Any I/O that happens as part of evaluation should be reported for that specific project, but there's no good way to do that here.

### Tool servers
Tool servers are long-lived processes which can be reused multiple times across builds. This causes problems for Tracker, as that long-lived process is not a child process of MSBuild, so many I/O operations would be missed.

For example, when `SharedCompilation=true`, the Roslyn compiler (csc.exe) will launch in server mode. This causes the `Csc` task to connect to any existing csc.exe process and pass the compilation request over a named pipe.

To support this scenario, a new MSBuild Task API could be introduced which allows build tasks which interact with tool servers to manually report I/O. In the Roslyn example above, it would report reads to all assemblies passed via `/reference` parameters, all source files, analyzers passed by `/analyzer`, and report writes to the assembly output file, pdb, and xml, and any other reads and writes which can happen as part of the tool. Effectively, the task will be responsible for reporting what file operations the tool server may perform for the request it makes to it. Note that the tool server may cache file reads internally, but the task should still report the I/O as if the internal cache was empty.

**OPEN ISSUE:** Analyzers are just arbitrary C# code, so there's no guarantees as to what I/O it may do, leading to possibly incorrect tracking.

Similarly for a theoretical server mode for MSBuild, MSBuild would need to report its own I/O rather than the higher-order build engine detouring the process externally. For example, if the higher-order build engine connected to an existing running MSBuild process to make build requests, it could not detour that process and so MSBuild would need to report all I/O done as part of a particular build request.

**OPEN ISSUE:** As described above in an open issue, tool servers are the only scenario which would not be supportable by just externally detouring the MSBuild process. The amount of investment required to enable tool servers is quite high and spans across multiple codebases: MSBuild needs to detour itself, MSBuild need to expose a new Tasks API, the `Csc` task needs to opt into that API, and the higher-order build engine needs to opt-in to MSBuild reporting its own I/O, as well as detecting that the feature is supported in the version of MSBuild it's using. Tool servers may add substantial performance gain, but the investment is also substantial.

## Examples

To illustrate the difference between `-graph` and `-graph -isolate`, consider these two projects, which are minimal except for a new target in the referenced project that is consumed in the referencing project.

`Referenced\Referenced.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>netcoreapp3.1</TargetFramework>
    <UnusualOutput>Configuration\Unusual.txt</UnusualOutput>
  </PropertyGroup>

  <Target Name="UnusualThing" Returns="$(UnusualOutput)" />
</Project>
```

`Referencing\Referencing.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>netcoreapp3.1</TargetFramework>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\Referenced\Referenced.csproj" />
  </ItemGroup>

  <Target Name="GetUnusualThing" BeforeTargets="BeforeBuild">
    <MSBuild Projects="..\Referenced\Referenced.csproj"
             Targets="UnusualThing">
      <Output TaskParameter="TargetOutputs"
              ItemName="Content" />
    </MSBuild>
  </Target>
</Project>
```

This project can successfully build with `-graph`

```sh-session
$ dotnet msbuild Referencing\Referencing.csproj -graph
"Static graph loaded in 0.253 seconds: 2 nodes, 1 edges"
  Referenced -> S:\Referenced\bin\Debug\netcoreapp3.1\Referenced.dll
  Referencing -> S:\Referencing\bin\Debug\netcoreapp3.1\Referencing.dll
```

But fails with `-graph -isolate`

```sh-session
$ dotnet msbuild Referencing\Referencing.csproj -graph -isolate
"Static graph loaded in 0.255 seconds: 2 nodes, 1 edges"
  Referenced -> S:\Referenced\bin\Debug\netcoreapp3.1\Referenced.dll
S:\Referencing\Referencing.csproj(12,5): error : MSB4252: Project "S:\Referencing\Referencing.csproj" with global properties
S:\Referencing\Referencing.csproj(12,5): error :     (IsGraphBuild=true)
S:\Referencing\Referencing.csproj(12,5): error :     is building project "S:\Referenced\Referenced.csproj" with global properties
S:\Referencing\Referencing.csproj(12,5): error :     (IsGraphBuild=true)
S:\Referencing\Referencing.csproj(12,5): error :     with the (UnusualThing) target(s) but the build result for the built project is not in the engine cache. In isolated builds this could mean one of the following:
S:\Referencing\Referencing.csproj(12,5): error :     - the reference was called with a target which is not specified in the ProjectReferenceTargets item in project "S:\Referencing\Referencing.csproj"
S:\Referencing\Referencing.csproj(12,5): error :     - the reference was called with global properties that do not match the static graph inferred nodes
S:\Referencing\Referencing.csproj(12,5): error :     - the reference was not explicitly specified as a ProjectReference item in project "S:\Referencing\Referencing.csproj"
S:\Referencing\Referencing.csproj(12,5): error :
```

This part of the error is the problem here:

> the reference was called with a target which is not specified in the ProjectReferenceTargets item in project "S:\Referencing\Referencing.csproj"

This is unacceptable in an isolated build because it means that the cached outputs of `Referenced.csproj` will be incomplete: they won't have the results of the `GetUnusualThing` target, because it's nonstandandard (and thus not one of the "well understood to be called on `ProjectReference`s targets that are handled by default).

TODO: write docs for SDK authors/build engineers on how to teach the graph about this sort of thing.
