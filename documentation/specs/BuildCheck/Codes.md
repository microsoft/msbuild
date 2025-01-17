# `BuildCheck` reports codes and their meaning

Report codes are chosen to conform to suggested guidelines. Those guidelines are currently in revew: https://github.com/dotnet/msbuild/pull/10088

| Diagnostic&nbsp;Code | Default Severity | Default Scope | Available from SDK | Reason |
|:-----|-------|-------|-------|----------|
| [BC0101](#bc0101---shared-output-path) | Warning | N/A | 9.0.100 | Shared output path. |
| [BC0102](#bc0102---double-writes) | Warning | N/A | 9.0.100 | Double writes. |
| [BC0103](#bc0103---used-environment-variable) | Suggestion | Project | 9.0.100 | Used environment variable. |
| [BC0104](#bc0104---projectreference-is-preferred-to-reference) | Warning | N/A | 9.0.200 | ProjectReference is preferred to Reference. |
| [BC0105](#bc0105---embeddedresource-should-specify-culture-metadata) | Warning | N/A | 9.0.200 | Culture specific EmbeddedResource should specify Culture metadata. |
| [BC0106](#bc0106---copytooutputdirectoryalways-should-be-avoided) | Warning | N/A | 9.0.200 | CopyToOutputDirectory='Always' should be avoided. |
| [BC0107](#bc0107---targetframework-and-targetframeworks-specified-together) | Warning | N/A | 9.0.200 | TargetFramework and TargetFrameworks specified together. |
| [BC0108](#bc0108---targetframework-or-targetframeworks-specified-in-non-sdk-style-project) | Warning | N/A | 9.0.300 | TargetFramework or TargetFrameworks specified in non-SDK style project. |
| [BC0201](#bc0201---usage-of-undefined-property) | Warning | Project | 9.0.100 | Usage of undefined property. |
| [BC0202](#bc0202---property-first-declared-after-it-was-used) | Warning | Project | 9.0.100 | Property first declared after it was used. |
| [BC0203](#bc0203----property-declared-but-never-used) | None | Project | 9.0.100 | Property declared but never used. |


Notes: 
 * What does the 'N/A' scope mean? The scope of checks are only applicable and configurable in cases where evaluation-time data are being used and the source of the data is determinable and available. Otherwise the scope of whole build is always checked.
 * How can you alter the default configuration? [Please check the Configuration section of the BuildCheck documentation](./BuildCheck.md#sample-configuration)
 * To enable verbose logging in order to troubleshoot issue(s), enable [binary logging](https://github.com/dotnet/msbuild/blob/main/documentation/wiki/Binary-Log.md#msbuild-binary-log-overview
   _Cmd:_
   ```cmd
   dotnet build -bl -check
   ```

<a name="BC0101"></a>
## BC0101 - Shared output path.

"Two projects should not share their OutputPath nor IntermediateOutputPath locations"

It is not recommended to share output path nor intermediate output path between multiple projects. Such practice can lead to silent overwrites of the outputs. Such overwrites will depend on the order of the build, that might not be guaranteed (if not explicitly configured) and hence it can cause nondeterministic behavior of the build.

If you want to produce outputs in a consolidated output folder - consider using the [Artifacts output layout](https://learn.microsoft.com/en-us/dotnet/core/sdk/artifacts-output) and/or [Microsoft.Build.Artifacts SDK](https://github.com/microsoft/MSBuildSdks/tree/main/src/Artifacts).


<a name="BC0102"></a>
## BC0102 - Double writes.

"Two tasks should not write the same file"

This is a similar problem as ['BC0101 - Shared output path'](#BC0101) - however with higher granularity. It is not recomended that multiple tasks attempt to write to a single file - as such behavior might lead to nondeterminism of a build (as result can be dependent on the order of the tasks execution if those belong to independent projects) or/and to a lost updates.

If you want multiple tasks to update file in a one-by-one pipeline fashion, it is recommended to give each intermediate output a distinct name - preventing silent mixups if any of the tasks in the chain are skipped or removed.

<a name="BC0103"></a>
## BC0103 - Used environment variable.

"Environment variables should not be used as a value source for the properties"

Using environment variables as a data source in MSBuild is problematic and can lead to nondeterministic builds.
Relying on environment variables introduces variability and unpredictability, as their values can change between builds or environments.

This practice can result in inconsistent build outcomes and makes debugging difficult, since environment variables are external to project files and build scripts. To ensure consistent and reproducible builds, avoid using environment variables. Instead, explicitly pass properties using the /p option, which offers better control and traceability.

<a name="BC0104"></a>
## BC0104 - ProjectReference is preferred to Reference.

"A project should not be referenced via 'Reference' to its output, but rather directly via 'ProjectReference'."

It is not recommended to reference project outputs. Such practice leads to losing the explicit dependency between the projects. Build then might not order the projects properly, which can lead to randomly missing reference and hence undeterministic build.

If you need to achieve more advanced dependency behavior - check [Controlling Dependencies Behavior](https://github.com/dotnet/msbuild/blob/main/documentation/wiki/Controlling-Dependencies-Behavior.md) document. If neither suits your needs - then you might need to disable this check for your build or for particular projects.

<a name="BC0105"></a>
## BC0105 - EmbeddedResource should specify Culture metadata.

"It is recommended to specify explicit 'Culture' metadata, or 'WithCulture=false' metadata with 'EmbeddedResource' item in order to avoid wrong or nondeterministic culture estimation."

[`EmbeddedResource` item](https://learn.microsoft.com/en-us/visualstudio/msbuild/common-msbuild-project-items#embeddedresource) has a `Culture` and `WithCulture` metadata that are strongly recommended to be used - to prevent MSBuild to need to 'guess' the culture from the file extension - which may be dependent on the current OS/Runtime available cultures and hence it can lead to nondeterministic build.

Examples:
 * `<EmbeddedResource Update = "Resource1.xyz.resx" Culture="xyz" />` This indicates the culture to the MSBuild engine and the culture will be respected. No diagnostic (warning) is issued ([see below for exceptions](#RespectAlreadyAssignedItemCulture)).
 * `<EmbeddedResource Update = "Resource1.xyz.resx" WithCulture="false" />` This indicates to the MSBuild engine that the file is culture neutral and the extension should not be treated as culture indicator. No diagnostic (warning)  is issued.
 * `<EmbeddedResource Update = "Resource1.xyz.resx" />` MSBuild infers the culture from the extra extension ('xyz') and if it is known to [`System.Globalization.CultureInfo`](https://learn.microsoft.com/en-us/dotnet/api/system.globalization.cultureinfo) it is being used as the resource culture. The `BC0105` diagnostic is emitted (if BuildCheck is enabled and BC0105 is not disabled)
 * `<EmbeddedResource Update = "Resource1.resx" />` MSBuild infers that the resource is culture neutral. No diagnostic (warning)  is issued.

<a name="RespectAlreadyAssignedItemCulture"></a>
**Note:** In Full Framework version of MSBuild (msbuild.exe, Visual Studio) and in .NET SDK prior 9.0 a global or project specific property `RespectAlreadyAssignedItemCulture` needs to be set to `'true'` in order for the explicit `Culture` metadata to be respected. Otherwise the explicit culture will be overwritten by MSBuild engine and if different from the extension - a `MSB3002` warning is emitted (`"MSB3002: Explicitly set culture "{0}" for item "{1}" was overwritten with inferred culture "{2}", because 'RespectAlreadyAssignedItemCulture' property was not set."`)

<a name="BC0106"></a>
## BC0106 - CopyToOutputDirectory='Always' should be avoided.

"Avoid specifying 'Always' for 'CopyToOutputDirectory' as this can lead to unnecessary copy operations during build. Use 'PreserveNewest' or 'IfDifferent' metadata value, or set the 'SkipUnchangedFilesOnCopyAlways' property to true to employ more effective copying."

[`CopyToOutputDirectory` metadata](https://learn.microsoft.com/en-us/visualstudio/msbuild/common-msbuild-project-items) can take the values:
 * `Never`
 * `Always`
 * `PreserveNewest`
 * `IfDifferent`

`Always` is not recommended, as it causes the files to be copied in every build, even when the destination file content is identical to the source.

Before the introduction of `IfDifferent`, `Always` was needed to work around cases where the destination file could have changed between builds (e.g. an asset that can be changed during test run, but needs to be reset by the build). `IfDifferent` preserves this behavior without unnecessary copying.

In order to avoid the need to change copy metadata for a large number of items, it's now possible to specify the `SkipUnchangedFilesOnCopyAlways` property in order to flip all copy behavior of `CopyToOutputDirectory=Always` to behave identically to `CopyToOutputDirectory=IfDifferent`:

```xml
<PropertyGroup>
    <SkipUnchangedFilesOnCopyAlways>True</SkipUnchangedFilesOnCopyAlways>
</PropertyGroup>

<ItemGroup>
    <None Include="File1.txt" CopyToOutputDirectory="Always" />
    <None Include="File2.txt" CopyToOutputDirectory="IfDifferent" />
</ItemGroup>
```

Both items in above example are treated same and no BC0106 diagnostic is issued.

<a name="BC0107"></a>
## BC0107 - TargetFramework and TargetFrameworks specified together.

"'TargetFramework' (singular) and 'TargetFrameworks' (plural) properties should not be specified in the scripts at the same time."

When building a .NET project - you can specify target framework of the resulting output (for more info see [the documentation](https://learn.microsoft.com/en-us/dotnet/standard/frameworks#how-to-specify-a-target-framework)).

When using `TargetFrameworks` property - you are instructing the build to produce output per each specified target framework.

If you specify `TargetFramework` you are instructing the build to produce a single output for that particualar target framework. `TargetFramework` gets precedence even if `TargetFrameworks` is specified - which might seem as if `TargetFrameworks` was ignored.

`BC0107` doesn't apply if you explicitly choose to build a single target of multitargeted build:

```
dotnet build my-multi-target.csproj /p:TargetFramework=net9.0
```

<a name="BC0108"></a>
## BC0108 - TargetFramework or TargetFrameworks specified in SDK-less project.

"'TargetFramework' nor 'TargetFrameworks' property should not be specified in projects not using .NET SDK."

'TargetFramework' or 'TargetFrameworks' control the project output targets in modern .NET SDK projects. The older SDK-less projects interprets different properties for similar mechanism (like 'TargetFrameworkVersion') and the 'TargetFramework' or 'TargetFrameworks' are silently ignored.

Make sure the Target Framework is specified appropriately for your project.


<a name="BC0201"></a>
## BC0201 - Usage of undefined property.

"A property that is accessed should be declared first."

This check indicates that a property was accessed without being declared (the declaration might have happen later - see [BC0202](#BC0202) for such checking). Only accessing in the configured scope (by default it's the project file only) are checked.

There are couple cases which are allowed by the check:

* Selfreferencing declaration is allowed - e.g.:
  `<ChainProp>$(ChainProp)</ChainProp>`

* Checking the property for emptyness - e.g.:
  `<PropertyGroup Condition="'$(PropertyThatMightNotBeDefined)' == ''">`

* Any usage of property in condition. This can be opted out via the configuration `AllowUninitializedPropertiesInConditions` - e.g.:
  ```ini
  [*.csproj]
  build_check.BC0201.severity=error
  build_check.BC0201.AllowUninitializedPropertiesInConditions=false
  build_check.BC0202.AllowUninitializedPropertiesInConditions=false
  ```

  BC0201 and BC0202 must have same value for the optional switch - as both operate on top of same data and same filtering.

<a name="BC0202"></a>
## BC0202 - Property first declared after it was used.

"A property should be declared before it is first used."

This check indicates that a property was accessed before it was declared. The default scope of this rule is the project file only. The scope captures the read and write operations as well. So this rule reports:
 * Uninitialized reads that happened anywhere during the build, while the uninitialized property was later defined within the scope of this check (e.g. project file).
 * Uninitialized reads that happened within the scope of check (e.g. project file), while later defined anywhere in the build

If `BC0202` and [BC0201](#BC0201) are both enabled - then `BC0201` reports only the undefined reads that are not reported by this rule (so those that do not have late definitions).

<a name="BC0203"></a>
## BC0203 -  Property declared but never used.

"A property that is not used should not be declared."

This check indicates that a property was defined in the observed scope (by default it's the project file only) and it was then not used anywhere in the build.

This is a runtime check, not a static analysis check - so it can have false positives - for this reasons it's currently not enabled by defaut.

Common cases of false positives:
 * Property not used in a particular build might be needed in a build with different conditions or a build of a different target (e.g. `dotnet pack /check` or `dotnet build /t:pack /check` accesses some additional properties as compared to ordinary `dotnet build /check`).
 * Property accessing is tracked for each project build request. There might be multiple distinct build requests for a project in a single build. Specific case of this is a call to the [MSBuild task](https://learn.microsoft.com/en-us/visualstudio/msbuild/msbuild-task) or [CallTarget task](https://learn.microsoft.com/en-us/visualstudio/msbuild/calltarget-task) that can request a result from a project build, while passing additional or different global properties and/or calling specific target. This happens often as part of common targets - e.g. for [multi-targeted project build parallelization](../../High-level-overview.md#parallelism)
 * Incremental build might skip execution of some targets, that might have been accessing properties of interest.

<BR/>
<BR/>
<BR/>

### Related Resources
* [BuildCheck documentation](./BuildCheck.md)
