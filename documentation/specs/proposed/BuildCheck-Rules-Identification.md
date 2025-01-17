# MSBuild Rules/Analyzers Identification

## Background and Context

The MSBuild team is currently working on delivering the MSBuild analyzers (aka BuildCheck). The BuildCheck infrastructure has built-in analyzers and functionality to support custom ones. Hence, we need to make sure it will be possible to configure and differentiate built-in and custom analyzers.

Note: Single analyzer can have multiple rules.

### Problems to address:
- The report should unambiguously point to the rule.
- Execution and configuration issues and execution time reporting for analyzers.
- Preventing clashes of identification within a single build and clashes of custom rules/analyzers with well-known rules/analyzers.
- Possibility to configure the rule.

## Proposal

### Built-in analyzers
Every built-in analyzer will have the friendly name: `BuildCheck.{FriendlyName}`.
- Regular expression for the name: `^BuildCheck.[A-Z]{1,}[a-zA-Z0-9_]{0,}$`

Each Rule that is shipped inbox will contain the RuleId as an identifier of the rule for this analyzer. 
- The rule id format is as follows: `^BC[A-Za-z_.]{0,}[0-9]{1,}$`.

#### Example of a built-in analyzer:
- Name: `BuildCheck.SharedOutputPath`
- RuleId: `BC0101` or `BC.AdditionalInfo0101` or `BC.Prefix.Test0123`

### Custom analyzers
Custom analyzer will have the friendly name: `{NameOfTheAnalyzer}` with defined format: 
- `^[A-Z]{1,}[a-zA-Z_]{1,}$`
- should not start with `BuildCheck.` this is built-in prefix for built-in.

Each Custom Analyzer Rule will have the rule id format as follows:
- `^[A-Z]{1}[A-Za-z]{0,}[0-9]{1,}$`. 
- should not start from `BC` this is reserved prefix for built-in rules.

#### Example of a custom analyzer:
- Name: `SharedOutputPath`, `SharedOutputPath` 
- RuleId: `SOMEPREFIX123`

Any registered analyzers that don't follow the pattern (built-in and custom) will raise an exception and fail the build.

The identification of the rule will consist of two components: the Friendlyname and the RuleId.

#### Examples 
- Built-in
    - `BuildCheck.SharedOutputPath.BC0001`
    - `BuildCheck.SharedOutputPath.BC0002`
    - `BuildCheck.PropertyAssignedIncorrectly.BC0002`
- Custom
    - `SharedOutputPath.ID0001`
    - `SharedOutputPath.ID0002`
    - `PropertyAssignedIncorrectly.ID0002`

#### Example of the output:
```
...
Determining projects to restore...
MSBUILD : error : BuildCheck.SharedOutputPath.BC0002: Projects FooBar-Copy.csproj and FooBar.csproj have onflicting output paths: C:\projects\msbuild\playground\buildcheck\bin\Debug\net8.0\.
MSBUILD : error : BuildCheck.SharedOutputPath.BC0002: Projects FooBar-Copy.csproj and FooBar.csproj have onflicting output paths: C:\projects\msbuild\playground\buildcheck\obj\Debug\net8.0\.
Restore:
...
```

### Rules Identification clash prevention

#### Custom VS Built-In
The prevention of having the same analyzer/rule's name/id's between built-in and custom is guaranteed by preserved prefixes
- Name Prefix: (BuildCheck|MSBuild|Microsoft)
- Id Prefix: (BC|MSB|MS)
If custom analyzer will not meet predefined pattern the registration of the custom analyzer will fail.

#### Custom VS Custom
The prevention of having the same analyzer/rule's name/id's between custom analyzers is not guaranteed, and during the registration of the custom analyzer, an additional check will happen to ensure that the analyzer name is not already registered.


### EditorConfig configurations

Any build check related configuration should start with the `build_check.` prefix. Without the prefix, the BuildCheck infrastructure will not recognize the input config as a valid key-value, and the config will not be respected.

- Built-in BuildCheck rule configuration
    - `build_check.BuildCheck.SharedOutputPath.BC0001.enabled = true|false`
    - `build_check.BuildCheck.SharedOutputPath.BC0001.severity = Error`

- Custom BuildCheck rules configuration
    - `build_check.SharedOutputPath.ID0001.enabled = true|false`
    - `build_check.SharedOutputPath.ID0001.severity = Error`
    - `build_check.SharedOutputPathSecond.AnotherRuleId0001.enabled = true|false`
    - `build_check.SharedOutputPathSecond.AnotherRuleId0001.severity = Error`

- To configure the analyzer (Priority of this is higher than configuring the single rule)
    -  `build_check.SharedOutputPath.enabled = true|false`

#### .editorconfig examples:

```
root=true

[FooBar.csproj]
build_check.BuildCheck.SharedOutputPath.BC0002.IsEnabled=true
build_check.BuildCheck.SharedOutputPath.BC0002.Severity=error

[FooBar-Copy.csproj]
build_check.BuildCheck.SharedOutputPath.BC0002.IsEnabled=true
build_check.BuildCheck.SharedOutputPath.BC0002.Severity=error
```

```
root=true

[FooBar.csproj]
build_check.BuildCheck.SharedOutputPath.IsEnabled=true
build_check.BuildCheck.SharedOutputPath.Severity=error

[FooBar-Copy.csproj]
build_check.BuildCheck.SharedOutputPath.IsEnabled=true
build_check.BuildCheck.SharedOutputPath.Severity=error
```

#### Priority of configuration

- Rule
- Analyzer