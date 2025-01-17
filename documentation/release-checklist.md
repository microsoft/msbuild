# MSBuild Release Checklist {{THIS_RELEASE_VERSION}}

## At any time

- [ ] Create a new issue to track the release checklist, with this checklist copied into the issue.
  - [ ] Replace `{{PREVIOUS_RELEASE_VERSION}}` with the previous release version, for example `17.9`
  - [ ] Replace `{{THIS_RELEASE_VERSION}}` with the current release version, for example `17.10`
  - [ ] Replace `{{NEXT_VERSION}}` with the next release version, for example `17.11`
- [ ]  Create `vs{{THIS_RELEASE_VERSION}}` branch
- [ ]  Create darc channel for `VS {{NEXT_VERSION}}` if it doesn't already exist \
`darc add-channel --name "VS {{NEXT_VERSION}}"`
- [ ]  Ping internal "First Responders" Teams channel to get the new channel made available as a promotion target (e.g. dotnet/arcade#12150): {{URL_OF_CHANNEL_PROMOTION_PR}}

## At release time
Before starting the process:
- [ ] If the release is being cut more than a few days before the VS-side snap, run insertions manually OR redirect MSBuild release branch
  - [ ]  Disable scheduled run of [MSBuild VS Insertion pipeline](https://devdiv.visualstudio.com/DevDiv/_build?definitionId=24295) (our {{NEXT_VERSION}} builds don't have a place to go in VS yet) by: Edit -> ... -> Triggers -> add a schedule on a dead branch (this overrides the YAML defined once-per-day schedule for main). Manual pipeline run: select as input resource the to-be-inserted "MSBuild" pipeline run on branch `vs{{THIS_RELEASE_VERSION}}` and VS TargetBranch `main`.
OR
  - [ ]  If the release is being cut more than couple of weeks modify [YAML](https://github.com/dotnet/msbuild/tree/main/azure-pipelines/vs-insertion.yml) (and merge to affected MSBuild branches) of the [VS insertion pipeline](https://devdiv.visualstudio.com/DevDiv/_build?definitionId=24295) so that it schedules insertions from MSBuild `vs{{THIS_RELEASE_VERSION}}` to VS `main`. Keep scheduled daily insertions to simplify your workflow and exclude `vs{{THIS_RELEASE_VERSION}}` from triggering insertion on each commit.

### Branching from main
- [ ] Ensure planned branch association to the channel
  - [ ] Check if the association exist (it is now recommended to create it as a part of the previous release checklist):\
  `darc get-default-channels  --channel "VS {{THIS_RELEASE_VERSION}}" --branch vs{{THIS_RELEASE_VERSION}} --source-repo https://github.com/dotnet/msbuild`
     - [ ] This step is done if output shows active expected association such as:\
     `(5997) https://github.com/dotnet/msbuild @ vs17.13 -> VS 17.13`
     - [ ] If the association is missing - we'll see output similar to:\
     `No matching channels were found.`
        - [ ] In such case - associate the `vs{{THIS_RELEASE_VERSION}}` branch with the next VS {{THIS_RELEASE_VERSION}} release channel \
        `darc add-default-channel  --channel "VS {{THIS_RELEASE_VERSION}}" --branch vs{{THIS_RELEASE_VERSION}} --repo https://github.com/dotnet/msbuild`
- [ ]  If the new version's branch was created before the Visual Studio fork: fast-forward merge the correct commit (the one that is currently inserted to VS main) to the `vs{{THIS_RELEASE_VERSION}}` branch \
e.g.: `git push upstream 2e6f2ff7ea311214255b6b2ca5cc0554fba1b345:refs/heads/vs17.10` \
_(This is for the case where we create the branch too early and want it to be based actually on a different commit. If you waited until a good point in time with `main` in a clean state, just branch off and you are done. The branch should point to a good, recent spot, so the final-branding PR goes in on top of the right set of commits.)_
- [ ]  Update the branch merge flow in `.config/git-merge-flow-config.jsonc` file to have the currently-in-servicing branches.
- [ ]  Create {{NEXT_VERSION}} branding PR (in main) including public API baseline package version change: {{URL_OF_NEXT_VERSION_BRANDING_PR}}.
  - In the file `eng/Versions.props` Update the `VersionPrefix` to `{{NEXT_VERSION}}` and `PackageValidationBaselineVersion` set to a latest internally available {{THIS_RELEASE_VERSION}} preview version in the [internal dnceng dotnet-tools feed](https://dev.azure.com/dnceng/internal/_artifacts/feed/dotnet-tools-internal). It might be needed to update `CompatibilitySuppressions.xml` files. See [this documentation](https://learn.microsoft.com/en-us/dotnet/fundamentals/apicompat/overview) for more details. You can update `CompatibilitySuppressions.xml` files by running
`dotnet pack MSBuild.Dev.slnf /p:ApiCompatGenerateSuppressionFile=true`.
  - [ ]  When VS main snaps to {{THIS_RELEASE_VERSION}} and updates its version to {{NEXT_VERSION}}, modify the [MSBuild VS Insertion pipeline](https://devdiv.visualstudio.com/DevDiv/_build?definitionId=24295) YAML so that it flows from MSBuild main to VS main.
    - [ ]  Update AutoTargetBranch selection in the [YAML](../azure-pipelines/vs-insertion.yml) (add to parameters and make new AutoTargetBranch rule by copying it from existing ones) of the [MSBuild VS Insertion pipeline](https://devdiv.visualstudio.com/DevDiv/_build?definitionId=24295) to insert MSBuild `vs{{THIS_RELEASE_VERSION}}` to the corresponding VS branch `rel/d{{THIS_RELEASE_VERSION}}`.
    - [ ] Add `rel/d{{THIS_RELEASE_VERSION}}` case to TargetBranch parameter in [Experimental insertion](../azure-pipelines/vs-insertion-experimental.yml)
    - [ ] Set scheduled insertion for main and remove exclusion of `vs{{THIS_RELEASE_VERSION}}` triggering on each commit if added earlier.
- [ ] Merge {{NEXT_VERSION}} branding PR

### Adjust DARC channels and subscriptions
- [ ]  Remove the `main` to old release channel ({{THIS_RELEASE_VERSION}}) default channel \
`darc delete-default-channel --repo https://github.com/dotnet/msbuild --branch main --channel "VS {{THIS_RELEASE_VERSION}}"`
- [ ]  Associate the `main` branch with the next release channel \
`darc add-default-channel  --channel "VS {{NEXT_VERSION}}" --branch main --repo https://github.com/dotnet/msbuild`
- [ ]  Prepare the same channel association as well for the next release branch (vs{{NEXT_VERSION}}) - as a preparation for a next release:\
  `darc add-default-channel  --channel "VS {{NEXT_VERSION}}" --branch vs{{NEXT_VERSION}} --repo https://github.com/dotnet/msbuild`
- [ ]  Check subscriptions for the forward-looking channel `VS {{NEXT_VERSION}}` and update as necessary (for instance, SDK's `main` branch should usually be updated, whereas release branches often should not be \
`darc get-subscriptions --exact --source-repo https://github.com/dotnet/msbuild --channel "VS {{THIS_RELEASE_VERSION}}"`
   - [ ]  Update channel VS {{THIS_RELEASE_VERSION}} to VS {{NEXT_VERSION}} for the sdk main subscription and any others from the previous step
     `darc update-subscription --id <subscription_id_of_msbuild_main_to_sdk_main> --channel "VS {{NEXT_VERSION}}"`
- [ ]  Ensure that the current release channel `VS {{THIS_RELEASE_VERSION}}` is associated with the correct release branch\
`darc get-default-channels --source-repo https://github.com/dotnet/msbuild --branch vs{{THIS_RELEASE_VERSION}}` \
if it is not, `darc add-default-channel  --channel "VS {{THIS_RELEASE_VERSION}}" --branch vs{{THIS_RELEASE_VERSION}} --repo https://github.com/dotnet/msbuild`
- [ ] Double check subscriptions from our repo `darc get-subscriptions --target-repo dotnet/msbuild` and update subscriptions to `VS{{THIS_RELEASE_VERSION}}` and `main` branches according to [supported versions of VS and SDK](https://learn.microsoft.com/en-us/dotnet/core/porting/versioning-sdk-msbuild-vs#supported-net-versions):
  - [ ] NuGet client
    - Based on VS version channel
    - `darc get-subscriptions --exact --target-repo https://github.com/dotnet/msbuild --source-repo https://github.com/nuget/nuget.client`
  - [ ] Source Build Packages
    - Based on .NET version channel
    - `darc get-subscriptions --exact --target-repo https://github.com/dotnet/msbuild --source-repo https://github.com/dotnet/source-build-reference-packages`
  - [ ] Roslyn:
    - Based on VS version channel
    - `darc get-subscriptions --exact --target-repo https://github.com/dotnet/msbuild --source-repo https://github.com/dotnet/roslyn`
  - [ ] Arcade:
    - Based on .NET version channel--does not change every MSBuild release
    - `darc get-subscriptions --exact --target-repo https://github.com/dotnet/msbuild --source-repo https://github.com/dotnet/arcade`
- [ ] Make sure the non-infrastructure dependencies (currently Roslyn and Nuget) are set to 'disabled' (`  - Enabled: False` in the `darc get-subscriptions` output) - we do not want to automatically bump them. The version updates should be explicitly driven by SDK or VS.
- [ ] Any missing subscription need to be added via `darc add-subscription` command, any misconfigured subscription needs to be edit via `darc update-subscription` command (for additional required and optional parameters run with `--help`)

### Adjust pipelines / releases
- [ ]  Fix OptProf data flow for the new vs{{THIS_RELEASE_VERSION}} branch
  - [ ] Run the [official build](https://devdiv.visualstudio.com/DevDiv/_build?definitionId=9434) for vs{{THIS_RELEASE_VERSION}} without OptProf (set `SkipApplyOptimizationData` variable in 'Advanced options' section of the 'Run pipeline' menu to `true`) or alternatively with the latest Opt-Prof collected for the main branch (set `Optional OptProfDrop Override` to the drop path of the collected data, which could be found in the logs of the pipeline: Windows_NT -> Build -> search for `OptimizationData`).
  - [ ] Check that the [OptProf data collection](https://devdiv.visualstudio.com/DevDiv/_build?definitionId=17389) pipeline run is triggered for vs{{THIS_RELEASE_VERSION}}. If not, run manually ('Run pipeline' in upper right)
  - [ ] Run the [official build](https://devdiv.visualstudio.com/DevDiv/_build?definitionId=9434) for vs{{THIS_RELEASE_VERSION}} with no extra customization - OptProf should succeed now
- [ ] Restore [MSBuild VS Insertion pipeline](https://devdiv.visualstudio.com/DevDiv/_build?definitionId=24295) to the default [YAML](https://github.com/dotnet/msbuild/tree/main/azure-pipelines/vs-insertion.yml) defined schedule, by removing all triggers from Edit -> ... -> Triggers.

### Configure localization
- [ ]  Create {{THIS_RELEASE_VERSION}} localization ticket: https://aka.ms/ceChangeLocConfig (requesting to switch localization from {{PREVIOUS_RELEASE_VERSION}} to {{THIS_RELEASE_VERSION}}): {{URL_OF_LOCALIZATION_TICKET}}
- [ ]  Enable {{THIS_RELEASE_VERSION}} localization - by setting [`EnableReleaseOneLocBuild`](https://github.com/dotnet/msbuild/blob/vs{{THIS_RELEASE_VERSION}}/.vsts-dotnet.yml) to `true`
- [ ]  Disable {{PREVIOUS_RELEASE_VERSION}} localization -  by setting [`EnableReleaseOneLocBuild`](https://github.com/dotnet/msbuild/blob/vs{{PREVIOUS_RELEASE_VERSION}}/.vsts-dotnet.yml) to `false`. Update the comment on the same line.
- [ ]  Create and merge a PR in main to update a localization version comment in setting [`EnableReleaseOneLocBuild`](https://github.com/dotnet/msbuild/blob/main/.vsts-dotnet.yml) to set up the merge conflict when this line will be updated in the release branch.

### Final branding
- [ ] Prepare final branding PR for `vs{{THIS_RELEASE_VERSION}}`: {{URL_OF_FINAL_BRANDING_PR}} \
      Edit Version.props file - add `<DotNetFinalVersionKind>release</DotNetFinalVersionKind>` as a suffix (on same line! - to intentionaly make it merge conflict on flows to main) after the `VersionPrefix`  \
      e.g.: #11130, #10697
- [ ]  Merge final branding to `vs{{THIS_RELEASE_VERSION}}` branch
- [ ]  Update perfstar MSBuild insertions configuration: [example PR](https://dev.azure.com/devdiv/DevDiv/_git/dotnet-perfstar/pullrequest/522843): {{URL_OF_PERFSTAR_PR}}
- [ ] Get M2 or QB approval as necessary per the VS schedule
- [ ]  Merge to VS (babysit the automatically generated VS insertion PR https://devdiv.visualstudio.com/DevDiv/_git/VS/pullrequests for the MSBuild commit noted in above step): {{URL_OF_VS_INSERTION}}
     The PR will be helpful for requesting nuget packages publishing - as it contains the inserted packages versions
    - [ ] Respond to the 'VS xyz package stabilization' email - with the merged insertion PR (as nowVS is on stable version).
- [ ] Update the PackageValidationBaselineVersion to the latest released version ({{THIS_RELEASE_VERSION}}.0) - this might require temporary addition of the [build artifacts feed](https://github.com/dotnet/msbuild/blob/29397b577e3ec0fe0c7650c3ab0400909655dc88/NuGet.config#L9) as the new version is not yet added to the official feeds (this is post release). This can trigger a high severity CG error (https://eng.ms/docs/cloud-ai-platform/devdiv/one-engineering-system-1es/1es-docs/secure-supply-chain/how-to-securely-configure-package-source-files) - however it should be fine to keep this temporary feed untill the release.
- [ ] Update the requested SDK version for bootstrap folder (the `BootstrapSdkVersion` property in [Versions.props](https://github.com/dotnet/msbuild/blob/main/eng/Versions.props)) if a fresh sdk was released (released runtimes and associated sdk versions can be checked here - https://dotnet.microsoft.com/download/visual-studio-sdks - make sure to always check the details of the appropriate targeted version of .NET for the matching latest version of SDK).
- [ ] Update `VisualStudio.ChannelName` (and `VisualStudio.MajorVersion` if applicable) of `Windows_NT` build step for our build pipeline in a newly created branch - it should point to the matching VS release branch and make sure the change is not automatically mergable with the interbranch flow (example: #11246): {{URL_OF_PR}}

## ASAP On/After GA:

Timing based on the [(Microsoft-internal) release schedule](https://dev.azure.com/devdiv/DevDiv/_wiki/wikis/DevDiv.wiki/10097/Dev17-Release).

- [ ]  Push packages to nuget.org (not currently automated, contact dnceng - search "Publish MSBuild 17.6 to NuGet.org" email subject for template).

  Following packages should be published (`THIS_RELEASE_EXACT_VERSION` is equal to `VersionPrefix` that comes form the eng\Version.props, that were part of the build we are trying to get published, it is as well part of the VS insertion PR noted above):
    - Microsoft.Build.Utilities.Core.{{THIS_RELEASE_EXACT_VERSION}}.nupkg
    - Microsoft.Build.{{THIS_RELEASE_EXACT_VERSION}}.nupkg
    - Microsoft.Build.Framework.{{THIS_RELEASE_EXACT_VERSION}}.nupkg
    - Microsoft.Build.Runtime.{{THIS_RELEASE_EXACT_VERSION}}.nupkg
    - Microsoft.Build.Tasks.Core.{{THIS_RELEASE_EXACT_VERSION}}.nupkg
    - Microsoft.NET.StringTools.{{THIS_RELEASE_EXACT_VERSION}}.nupkg
    - Microsoft.Build.Templates.{{THIS_RELEASE_EXACT_VERSION}}.nupkg

  **Note:** Microsoft.Build.Conversion.Core and Microsoft.Build.Engine are **not** part of the list. Microsoft.Build.Templates **is** part of the list. Those 3 packages are a difference to the historic publishing list.

- [ ]  Publish docs: submit reference request at https://aka.ms/publishondocs
  - Click on the link labeled *Request – Reference Publishing*
  - You can use existing [ticket](https://dev.azure.com/msft-skilling/Content/_workitems/edit/183613) as a reference
- [ ] Remove the temporarily added [build feed from `nuget.config`](https://github.com/dotnet/msbuild/blob/29397b577e3ec0fe0c7650c3ab0400909655dc88/NuGet.config#L9) if it was added in the `Update the PackageValidationBaselineVersion` step
- [ ]  Update `main` subscriptions to the new channel (this can be done before or after release - depending on when the source repos from our previous - VS {{THIS_RELEASE_VERSION}} - channle start to publish in the next - VS {{NEXT_VERSION}} - channel) \
`darc get-subscriptions --exact --target-repo https://github.com/dotnet/msbuild --target-branch main`
- [ ]  Create the {{THIS_RELEASE_VERSION}} release
  - [ ]  Create tag (can be done upfront)
  ```
  git checkout <commit noted above>
  git tag v{{THIS_RELEASE_VERSION}}.3
  git push upstream v{{THIS_RELEASE_VERSION}}.3
  ```
  - [ ]  Create Release in Github with `Create Release from Tag` GH option (https://github.com/dotnet/msbuild/releases/new?tag=v17.9.3) - the release notes can be prepopulated (`Generate Release Notes`)
