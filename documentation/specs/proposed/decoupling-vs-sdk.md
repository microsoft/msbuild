# Decoupling VS builds of .NET SDK projects
The experience of building a .NET SDK project can differ significantly depending if the project was built using Visual Studio / MSBuild or `dotnet build`. The build can produce different diagnostics, use different language rules, etc. This is because building a .NET SDK project from Visual Studio mixes components from Visual Studio and the .NET SDK. This means core tooling, like the compiler, can be substantially different between the two types of build. This leads to customer confusion and hard-to-diagnose problems. To solve this want to ensure that when building a .NET SDK project we use more components from the .NET SDK to do so.

## Goals and Motivation

We are aiming for:
 - More consistent end-user experience for build between .NET CLI and Visual Studio.
 - Decoupling the .NET SDK experience from Visual Studio 

There are a few reasons that makes us persue this effort.
The first, we want a better experience when using or writting Roslyn analyzers and MSBuild Tasks. Currently tooling authors need to target NetStandard2.0 for their projects to be recognized by VS, and doing so blocks out newer features available. 

Second, tasks will not need to be multitargeted to cover both VS and .NET SDK. Right now, a lot of tasks need different versions to cover both of these scenarios, but with the changes in this features, authors will be able to use the same version for both situations.


## Impact
There are a few area of impact:
 - .NET SDK style project builds will be more stable between VS and CLI builds, as the tooling will not be devided between different versions.
 - Reduced cost of development for external and internal teams that author Roslyn Analyzers, source generators, or MSBuild Tasks.
 - End-user will not experience mismatch between analyzer versions, and gain higher confidence that their .NET SDK project builds will behave the same way in VS and in the command line.

## Stakeholders
Other teams will need to work to fully complete the VS and .NET SDK decoupling feature after our base work is done. There are two handovers in this project:

1. After providing enough information to do so through MSBuild and the SDK, Roslyn will need to use it to invoke their .NET compiler in VS.
2. After MSBuild enables tasks to target .NET even for VS use, task-owning teams like the .NET SDK will need to migrate their targets to use .NET Core instead of keeping them targeting .NET Framework.

The handovers should allow other teams to proceed with their work smoothly and no unexpected change in build behavior should be present within MSBuild.

## Risks
A few risks associated with this feature:
 - If .NET Core tasks is discovered to have too large of a performance impact (due to IPC overhead to a .NET process), core partner teams may choose to keep multitargeting their tasks for improved user perf.
 - There is a hard deadline for this feature, VS17.14. As a consequence of how we support versions we would need to get all the work of this feature completed before that release. If we do not reach the deadline for this feature we would need to change policies on SDK level to be able to continue support.


## Plan
 1. Ensure that MSBuild.exe provides the same execution state as the dotnet command line invocation.
    -  This is should take around 1 dev week to complete, and will be handed over to Roslyn team.
 2. Implement .NET Core task host, tasks can be executed on the .NET Core vresion instead of .NET framework.
    - This should take 1 to 2 dev months to complete, including extensive testing. This would be handed over to internal teams that have custom tasks so they can be updated and tested.
 3. Load common targets from the .NET SDK and not from .NET NetFramework on VS. This work depends on other team's finilizing their part of the feature and might not be in scope for .NET 10.
    - This should take a dev week for code changes. For everything else, analysis, testing, etc... the time is very dependent on what happens after the code change, which we can't fully predict at this moment.

