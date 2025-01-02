# Telemetry 

We want to implement telemetry collection for VS/MSBuild.exe scenarios where we are currently not collecting data. VS OpenTelemetry initiative provides a good opportunity to use their infrastructure and library. 
There is some data we collect via SDK which we want to make accessible.

## Goals and Motivation 

We have limited data about usage of MSBuild by our customers in VS and no data about usage of standalone msbuild.exe.
This limits us in prioritization of features and scenarios to optimize performance for. 
Over time we want to have comprehensive insight into how MSBuild is used in all scenarios. Collecting such a data without any constraints nor limitations would however be prohibitively expensive (from the data storage PoV and possibly as well from the client side performance impact PoV). Ability to sample / configure the collection is an important factor in deciding the instrumentation and collection tech stack. Implementing telemetry via VS OpenTelemetry initiative would give us this ability in the future.

Goal: To have relevant data in that is actionable for decisions about development. Measuring real world performance impact of features (e.g. BuildCheck). Easily extensible telemetry infrastructure if we want to measure a new datapoint.

## Impact 
- Better planning of deployment of forces in MSBuild by product/team management.
- Customers can subscribe to telemetry locally to have data in standardized OpenTelemetry format

## Stakeholders 
- @Jan(Krivanek|Provaznik) design and implementation of telemetry via VS OTel. @ - using data we already have from SDK.
- @maridematte - documenting + dashboarding currently existing datapoints.
- MSBuild Team+Management – want insights from builds in VS
- VS OpenTelemetry team – provide support for VS OpenTelemetry collector library, want successful adoption 
- SourceBuild – consulting and approving usage of OpenTelemetry 
- MSBuild PM @baronfel – representing customers who want to monitor their builds locally

### V1 Successful handover
- Shipped to Visual Studio
- Data queryable in Kusto
- Dashboards (even for pre-existing data - not introduced by this work)
- Customers are able to monitor with OpenTelemetry collector of choice (can be cut)

## Risks 
- Performance regression risks - it's another thing MSBuild would do and if the perf hit would be too bad it would need mitigation effort.
- It introduces a closed source dependency for VS and MSBuild.exe distribution methods which requires workarounds to remain compatible with SourceBuild policy (conditional compilation/build). 
- Using a new VS API - might have gaps
- storage costs 
- Potential additional costs and delays due to compliance with SourceBuild/VS data.

## V1 Cost 
5 months of .5 developer's effort ~ 50 dev days (dd)

20-30dd JanPro OTel design + implementation, 10-15dd JanK design + implementation, 5-10dd Mariana/someone getting available data in order/"data science"/dashboards + external documentation

Uncertainties:
It’s an exploratory project for VS OpenTelemetry, we'll be their first OSS component, so there might come up issues. SourceBuild compliance could introduce delays.

## Plan 
### V1 scope
- Collected data point definition
- Instrumented data points (as an example how the instrumentation and collection works)
- Telemetry sent to VS Telemetry in acceptable quantity
- Dashboards for collected data
- Hooking of customer's telemetry collection
- Documenting and leveraging pre-existing telemetry

#### Out of scope
- Unifying telemetry for SDK MSBuild and MSBuild.exe/VS MSBuild.
- Thorough instrumentation of MSBuild
- Using MSBuild server
- Distributed tracing

### Detailed cost
- Prototyping the libraries/mechanism for collecting telemetry data (month 1) 10dd

- Defining usful data points (month 1) 5dd

- Design and approval of hooking VSTelemetry collectors and OTel collectors  (month 2) 10dd

- Formalizing, agreeing to sourcebuild and other external requirements (month 2) 5dd

- Instrumenting MSBuild with defined datapoints (month 3) 7dd

- Creating dashboards/insights (month 4) 5dd

- Documenting for customers how to hook their own telemetry collection (month 4) 3dd

- Buffer for discovered issues (VSData Platform, SourceBuild, OpenTelemetry) and more investments (month 5) 5dd
