# Builds suck

No build is ever fast enough, reliable enough, or does everything you want.

MSBuild-driven builds are no exception.

## Evaluation

*Every* [batch build]() (absent a higher-order build system) must evaluate every project in the scope of the build. IDEs can cache evaluation and act as a higher-order builds sytem but important IDE scenarios like “first load after a repo clone” are dominated by evaluation.

## ResolveAssemblyReferences

When build is invoked, most targets can be skipped as up to date, but `ResolveAssemblyReferences` (RAR) and some of its prerequisites like `ResolvePackageAssets` cannot, because their role is to produced data used within the build to compute the compiler command line. Since they don't have concrete file outputs and their file inputs can be difficult to express (it’s the closure of all referenced assemblies), MSBuild's standard up-to-date check mechanisms can't apply.

## Copy

The amount of time spent copying files in a build can be surprising. Efficient copy-on-write filesystems can help dramatically (we now have this on all major operating systems via `clonefile` on [Linux]() and [macOS]() and the [Windows 11 24H2+ Dev Drive]()).

As an implementation detail of MSBuild's common copies, the targets are generally not incremental, in favor of fine-grained incrementality within the Copy task itself. This means that Copy task time can be nonzero even on a fully up-to-date build.
