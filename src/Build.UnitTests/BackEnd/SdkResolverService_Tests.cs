// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.Build.BackEnd.Logging;
using Microsoft.Build.BackEnd.SdkResolution;
using Microsoft.Build.Construction;
using Microsoft.Build.Framework;
using Microsoft.Build.UnitTests;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Build.Unittest;
using Shouldly;
using Xunit;
using SdkResolverContextBase = Microsoft.Build.Framework.SdkResolverContext;
using SdkResultBase = Microsoft.Build.Framework.SdkResult;
using SdkResultFactoryBase = Microsoft.Build.Framework.SdkResultFactory;
using SdkResultImpl = Microsoft.Build.BackEnd.SdkResolution.SdkResult;

namespace Microsoft.Build.Engine.UnitTests.BackEnd
{
    public class SdkResolverService_Tests
    {
        private readonly MockLogger _logger;
        private readonly LoggingContext _loggingContext;

        public SdkResolverService_Tests()
        {
            _logger = new MockLogger();
            ILoggingService loggingService = LoggingService.CreateLoggingService(LoggerMode.Synchronous, 1);
            loggingService.RegisterLogger(_logger);

            _loggingContext = new MockLoggingContext(
                loggingService,
                new BuildEventContext(0, 0, BuildEventContext.InvalidProjectContextId, 0, 0));
        }

        [Fact]
        public void AssertAllResolverErrorsLoggedWhenSdkNotResolved()
        {
            using ResettableSdkResolverServiceState state = new(SdkResolverService.Instance, new MockLoaderStrategy());

            SdkReference sdk = new SdkReference("notfound", "referencedVersion", "minimumVersion");

            var result = SdkResolverService.Instance.ResolveSdk(BuildEventContext.InvalidSubmissionId, sdk, _loggingContext, new MockElementLocation("file"), "sln", "projectPath", interactive: false, isRunningInVisualStudio: false);

            result.Success.ShouldBeFalse();
            result.ShouldNotBeNull();
            result.SdkReference.ShouldNotBeNull();
            result.SdkReference.Name.ShouldBe("notfound");
            result.SdkReference.Version.ShouldBe("referencedVersion");
            result.SdkReference.MinimumVersion.ShouldBe("minimumVersion");

            _logger.BuildMessageEvents.Select(i => i.Message).ShouldContain("MockSdkResolver1 running");
            _logger.BuildMessageEvents.Select(i => i.Message).ShouldContain("MockSdkResolver2 running");
            _logger.Errors.Select(i => i.Message).ShouldBe(new [] { "ERROR1", "ERROR2" });
            _logger.Warnings.Select(i => i.Message).ShouldBe(new[] { "WARNING2" });
        }

        [Fact]
        public void AssertResolutionWarnsIfResolvedVersionIsDifferentFromReferencedVersion()
        {
            var sdk = new SdkReference("foo", "1.0.0", null);

            using ResettableSdkResolverServiceState state = new(SdkResolverService.Instance,
            null,
                new List<SdkResolver>
                {
                    new SdkUtilities.ConfigurableMockSdkResolver(
                        new SdkResultImpl(
                            sdk,
                            "path",
                            "2.0.0",
                            Enumerable.Empty<string>()
                            ))
                });

            var result = SdkResolverService.Instance.ResolveSdk(BuildEventContext.InvalidSubmissionId, sdk, _loggingContext, new MockElementLocation("file"), "sln", "projectPath", interactive: false, isRunningInVisualStudio: false);

            result.Path.ShouldBe("path");

            _logger.WarningCount.ShouldBe(1);
            _logger.Warnings.First().Code.ShouldStartWith("MSB4241");
        }

        [Fact]
        public void AssertResolverThrows()
        {
            using ResettableSdkResolverServiceState state = new(SdkResolverService.Instance, new MockLoaderStrategy(includeErrorResolver: true));

            SdkReference sdk = new SdkReference("1sdkName", "version1", "minimumVersion");

            // When an SDK resolver throws, the expander will catch it and stop the build.
            SdkResolverException e = Should.Throw<SdkResolverException>(() => SdkResolverService.Instance.ResolveSdk(BuildEventContext.InvalidSubmissionId, sdk, _loggingContext, new MockElementLocation("file"), "sln", "projectPath", interactive: false, isRunningInVisualStudio: false));
            e.Resolver.Name.ShouldBe("MockSdkResolverThrows");
            e.Sdk.Name.ShouldBe("1sdkName");
        }

        [Fact]
        public void AssertFirstResolverCanResolve()
        {
            using ResettableSdkResolverServiceState state = new(SdkResolverService.Instance, new MockLoaderStrategy());

            SdkReference sdk = new SdkReference("1sdkName", "referencedVersion", "minimumVersion");

            var result = SdkResolverService.Instance.ResolveSdk(BuildEventContext.InvalidSubmissionId, sdk, _loggingContext, new MockElementLocation("file"), "sln", "projectPath", interactive: false, isRunningInVisualStudio: false);

            result.Path.ShouldBe("resolverpath1");
            _logger.BuildMessageEvents.Select(i => i.Message).ShouldContain("MockSdkResolver1 running");
        }

        [Fact]
        public void AssertFirstResolverErrorsSupressedWhenResolved()
        {
            using ResettableSdkResolverServiceState state = new(SdkResolverService.Instance, new MockLoaderStrategy());

            // 2sdkName will cause MockSdkResolver1 to fail with an error reason. The error will not
            // be logged because MockSdkResolver2 will succeed.
            SdkReference sdk = new SdkReference("2sdkName", "version2", "minimumVersion");

            var result = SdkResolverService.Instance.ResolveSdk(BuildEventContext.InvalidSubmissionId, sdk, _loggingContext, new MockElementLocation("file"), "sln", "projectPath", interactive: false, isRunningInVisualStudio: false);

            result.Path.ShouldBe("resolverpath2");

            // Both resolvers should run, and no ERROR string.
            _logger.BuildMessageEvents.Select(i => i.Message).ShouldContain("MockSdkResolver1 running");
            _logger.BuildMessageEvents.Select(i => i.Message).ShouldContain("MockSdkResolver2 running");

            // Resolver2 gives a warning on success or failure.
            _logger.Warnings.Select(i => i.Message).ShouldBe(new[] { "WARNING2" });
            _logger.ErrorCount.ShouldBe(0);
        }

        [Fact]
        public void AssertResolverHasStatePreserved()
        {
            const int submissionId = 5;

            using ResettableSdkResolverServiceState state = new(SdkResolverService.Instance, new MockLoaderStrategy());

            SdkReference sdk = new SdkReference("othersdk", "1.0", "minimumVersion");

            // First call should not know state
            SdkResolverService.Instance.ResolveSdk(submissionId, sdk, _loggingContext, new MockElementLocation("file"), "sln", "projectPath", interactive: false, isRunningInVisualStudio: false).Path.ShouldBe("resolverpath");

            // Second call should have received state
            SdkResolverService.Instance.ResolveSdk(submissionId, sdk, _loggingContext, new MockElementLocation("file"), "sln", "projectPath", interactive: false, isRunningInVisualStudio: false).Path.ShouldBe(MockSdkResolverWithState.Expected);
        }

        [Fact]
        public void AssertResolverStateNotPreserved()
        {
            const int submissionId = BuildEventContext.InvalidSubmissionId;

            using ResettableSdkResolverServiceState state = new(SdkResolverService.Instance, new MockLoaderStrategy());

            SdkReference sdk = new SdkReference("othersdk", "1.0", "minimumVersion");

            // First call should not know state
            SdkResolverService.Instance.ResolveSdk(submissionId, sdk, _loggingContext, new MockElementLocation("file"), "sln", "projectPath", interactive: false, isRunningInVisualStudio: false).Path.ShouldBe("resolverpath");

            // Second call should have received state
            SdkResolverService.Instance.ResolveSdk(submissionId, sdk, _loggingContext, new MockElementLocation("file"), "sln", "projectPath", interactive: false, isRunningInVisualStudio: false).Path.ShouldBe("resolverpath");
        }

        [Theory]
        [InlineData(null, "1.0", true)]
        [InlineData("1.0", "1.0", true)]
        [InlineData("1.0-preview", "1.0-PrEvIeW", true)]
        [InlineData("1.0", "1.0.0", false)]
        [InlineData("1.0", "1.0.0.0", false)]
        [InlineData("1.0.0", "1.0.0.0", false)]
        [InlineData("1.2.0.0", "1.0.0.0", false)]
        [InlineData("1.2.3.0", "1.2.0.0", false)]
        [InlineData("1.2.3.4", "1.2.3.0", false)]
        public void IsReferenceSameVersionTests(string version1, string version2, bool expected)
        {
            SdkReference sdk = new SdkReference("Microsoft.NET.Sdk", version1, null);

            SdkResolverService.IsReferenceSameVersion(sdk, version2).ShouldBe(expected);
        }

        [Fact]
        public void CachingWrapperShouldWarnWhenMultipleVersionsAreReferenced()
        {
            var sdk = new SdkReference("foo", "1.0.0", null);

            var resolver = new SdkUtilities.ConfigurableMockSdkResolver(
                new SdkResultImpl(
                    sdk,
                    "path",
                    "1.0.0",
                    Enumerable.Empty<string>()
                    ));

            var service = new CachingSdkResolverService();
            using ResettableSdkResolverServiceState state = new(service, 
                null,
                new List<SdkResolver>
                {
                    resolver
                });

            var result = service.ResolveSdk(BuildEventContext.InvalidSubmissionId, sdk, _loggingContext, new MockElementLocation("file"), "sln", "projectPath", interactive: false, isRunningInVisualStudio: false);
            resolver.ResolvedCalls.Count.ShouldBe(1);
            result.Path.ShouldBe("path");
            result.Version.ShouldBe("1.0.0");
            _logger.WarningCount.ShouldBe(0);

            result = service.ResolveSdk(BuildEventContext.InvalidSubmissionId, new SdkReference("foo", "2.0.0", null), _loggingContext, new MockElementLocation("file"), "sln", "projectPath", interactive: false, isRunningInVisualStudio: false);
            resolver.ResolvedCalls.Count.ShouldBe(1);
            result.Path.ShouldBe("path");
            result.Version.ShouldBe("1.0.0");
            _logger.WarningCount.ShouldBe(1);
            _logger.Warnings.First().Code.ShouldBe("MSB4240");

            resolver.ResolvedCalls.First().Key.ShouldBe("foo");
            resolver.ResolvedCalls.Count.ShouldBe(1);
        }

        private void CreateMockSdkResultPropertiesAndItems(out Dictionary<string, string> propertiesToAdd, out Dictionary<string, SdkResultItem> itemsToAdd)
        {
            propertiesToAdd = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    {"PropertyFromSdkResolver", "ValueFromSdkResolver" }
                };

            itemsToAdd = new Dictionary<string, SdkResultItem>(StringComparer.OrdinalIgnoreCase)
                {
                    { "ItemNameFromSdkResolver", new SdkResultItem( "ItemValueFromSdkResolver",
                        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            { "MetadataName", "MetadataValue" }
                        })
                    }
                };
        }

        private void ValidateExpectedPropertiesAndItems(bool includePropertiesAndItems, SdkResultBase result)
        {
            if (includePropertiesAndItems)
            {
                result.PropertiesToAdd.Count.ShouldBe(1);
                result.PropertiesToAdd["PropertyFromSdkResolver"].ShouldBe("ValueFromSdkResolver");

                result.ItemsToAdd.Count().ShouldBe(1);
                result.ItemsToAdd.Keys.Single().ShouldBe("ItemNameFromSdkResolver");
                result.ItemsToAdd.Values.Single().ItemSpec.ShouldBe("ItemValueFromSdkResolver");
                var metadata = result.ItemsToAdd.Values.Single().Metadata;
                metadata.ShouldBeSameIgnoringOrder(new[] { new KeyValuePair<string, string>("MetadataName", "MetadataValue") });
            }
            else
            {
                result.PropertiesToAdd.ShouldBeNull();
                result.ItemsToAdd.ShouldBeNull();
            }
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void SdkResolverCanReturnNoPaths(bool includePropertiesAndItems)
        {
            var sdk = new SdkReference("foo", null, null);

            Dictionary<string, string> propertiesToAdd = null;
            Dictionary<string, SdkResultItem> itemsToAdd = null;

            if (includePropertiesAndItems)
            {
                CreateMockSdkResultPropertiesAndItems(out propertiesToAdd, out itemsToAdd);
            }

            var resolver = new SdkUtilities.ConfigurableMockSdkResolver(
                new SdkResultImpl(
                    sdk,
                    Enumerable.Empty<string>(),
                    version: null,
                    propertiesToAdd,
                    itemsToAdd,
                    warnings: null
                    ));

            using ResettableSdkResolverServiceState state = new(SdkResolverService.Instance, null, new List<SdkResolver>() { resolver });

            var result = SdkResolverService.Instance.ResolveSdk(BuildEventContext.InvalidSubmissionId, sdk, _loggingContext, new MockElementLocation("file"), "sln", "projectPath", interactive: false, isRunningInVisualStudio: false);

            result.Success.ShouldBeTrue();
            result.Path.ShouldBeNull();
            result.Version.ShouldBeNull();

            result.AdditionalPaths.ShouldBeNull();

            ValidateExpectedPropertiesAndItems(includePropertiesAndItems, result);

            _logger.WarningCount.ShouldBe(0);
        }

        [Fact]
        public void SdkResultCanReturnPropertiesAndItems()
        {
            string expectedPath = "Path/To/Return/From/Resolver";

            var sdk = new SdkReference("foo", null, null);

            Dictionary<string, string> propertiesToAdd;
            Dictionary<string, SdkResultItem> itemsToAdd;
           
            CreateMockSdkResultPropertiesAndItems(out propertiesToAdd, out itemsToAdd);

            var resolver = new SdkUtilities.ConfigurableMockSdkResolver(
                new SdkResultImpl(
                    sdk,
                    new[] { expectedPath },
                    version: "1.0",
                    propertiesToAdd,
                    itemsToAdd,
                    warnings: null
                    ));

            using ResettableSdkResolverServiceState state = new(SdkResolverService.Instance, null, new List<SdkResolver>() { resolver });

            var result = SdkResolverService.Instance.ResolveSdk(BuildEventContext.InvalidSubmissionId, sdk, _loggingContext, new MockElementLocation("file"), "sln", "projectPath", interactive: false, isRunningInVisualStudio: false);

            result.Success.ShouldBeTrue();
            result.Path.ShouldBe(expectedPath);
            result.Version.ShouldBe("1.0");

            result.AdditionalPaths.ShouldBeNull();

            ValidateExpectedPropertiesAndItems(true, result);

            _logger.WarningCount.ShouldBe(0);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void SdkResultCanReturnMultiplePaths(bool includePropertiesAndItems)
        {
            string expectedPath1 = "First/Path/To/Return/From/Resolver";
            string expectedPath2 = "Second/Path/To/Return/From/Resolver";

            var sdk = new SdkReference("foo", "1.0", null);

            Dictionary<string, string> propertiesToAdd = null;
            Dictionary<string, SdkResultItem> itemsToAdd = null;

            if (includePropertiesAndItems)
            {
                CreateMockSdkResultPropertiesAndItems(out propertiesToAdd, out itemsToAdd);
            }

            var resolver = new SdkUtilities.ConfigurableMockSdkResolver(
                new SdkResultImpl(
                    sdk,
                    new []
                    {
                        expectedPath1,
                        expectedPath2
                    },
                    version: "1.0",
                    propertiesToAdd,
                    itemsToAdd,
                    warnings: null
                    ));

            using ResettableSdkResolverServiceState state = new(SdkResolverService.Instance, null, new List<SdkResolver>() { resolver });

            var result = SdkResolverService.Instance.ResolveSdk(BuildEventContext.InvalidSubmissionId, sdk, _loggingContext, new MockElementLocation("file"), "sln", "projectPath", interactive: false, isRunningInVisualStudio: false);

            result.Success.ShouldBeTrue();

            var resultPaths = new List<string>();
            resultPaths.Add(result.Path);
            resultPaths.AddRange(result.AdditionalPaths);

            resultPaths.ShouldBeSameIgnoringOrder(new[]
            {
                expectedPath1,
                expectedPath2
            });

            ValidateExpectedPropertiesAndItems(includePropertiesAndItems, result);

            _logger.WarningCount.ShouldBe(0);
        }

        [Fact]
        public void AssertResolutionWarnsIfResolvedVersionIsDifferentFromReferencedVersionWithMultipleReturnPaths()
        {
            var expectedPath1 = "First/Path/To/Return/From/Resolver";
            var expectedPath2 = "Second/Path/To/Return/From/Resolver";

            var sdk = new SdkReference("foo", "1.0", null);
            Dictionary<string, string> propertiesToAdd;
            Dictionary<string, SdkResultItem> itemsToAdd;
            CreateMockSdkResultPropertiesAndItems(out propertiesToAdd, out itemsToAdd);

            var resolver = new SdkUtilities.ConfigurableMockSdkResolver(
                new SdkResultImpl(
                    sdk,
                    new[]
                    {
                        expectedPath1,
                        expectedPath2
                    },
                    version: "1.1",
                    propertiesToAdd,
                    itemsToAdd,
                    warnings: null
                    ));

            using ResettableSdkResolverServiceState state = new(SdkResolverService.Instance, null, new List<SdkResolver>() { resolver });

            var result = SdkResolverService.Instance.ResolveSdk(BuildEventContext.InvalidSubmissionId, sdk, _loggingContext, new MockElementLocation("file"), "sln", "projectPath", interactive: false, isRunningInVisualStudio: false);

            result.Success.ShouldBeTrue();

            var resultPaths = new List<string>();
            resultPaths.Add(result.Path);
            resultPaths.AddRange(result.AdditionalPaths);

            resultPaths.ShouldBeSameIgnoringOrder(new[]
            {
                expectedPath1,
                expectedPath2
            });

            ValidateExpectedPropertiesAndItems(true, result);

            _logger.WarningCount.ShouldBe(1);
            _logger.Warnings.First().Code.ShouldStartWith("MSB4241");
        }

        /// <summary>
        /// Verifies that an SDK resolver is only called once per build per SDK.
        /// </summary>
        [Fact]
        public void CachingWrapperShouldOnlyResolveOnce()
        {
            var sdk = new SdkReference("foo", "1.0.0", null);

            var resolver = new SdkUtilities.ConfigurableMockSdkResolver(
                new SdkResultImpl(
                    sdk,
                    "path",
                    "1.0.0",
                    Enumerable.Empty<string>()
                ));

            var service = new CachingSdkResolverService();
            using ResettableSdkResolverServiceState state = new(service, 
                null,
                new List<SdkResolver>
                {
                    resolver
                });

            // Resolve the SDK 10 times in parallel
            Parallel.For(
                0,
                10,
                _ => service.ResolveSdk(BuildEventContext.InvalidSubmissionId, sdk, _loggingContext, new MockElementLocation("file"), "sln", "projectPath", interactive: false, isRunningInVisualStudio: false));

            var result = resolver.ResolvedCalls.ShouldHaveSingleItem();

            result.Key.ShouldBe(sdk.Name);

            // The SDK resolver keeps track of the number of times it was called which should be 1
            result.Value.ShouldBe(1, $"The SDK resolver should have only been called once but was called {result.Value} times");
        }

        [Fact]
        public void InteractiveIsSetForResolverContext()
        {
            // Start with interactive false
            bool interactive = false;

            var service = new CachingSdkResolverService();

            using ResettableSdkResolverServiceState state = new(service, 
                resolvers: new List<SdkResolver>
                {
                    new SdkUtilities.ConfigurableMockSdkResolver((sdkRference, resolverContext, factory) =>
                    {
                        interactive = resolverContext.Interactive;

                        return null;
                    })
                });

            service.ResolveSdk(
                BuildEventContext.InvalidSubmissionId,
                new SdkReference("foo", "1.0.0", null),
                _loggingContext,
                new MockElementLocation("file"),
                "sln",
                "projectPath",
                // Pass along interactive and expect it to be received in the SdkResolverContext
                interactive: true,
                false);

            interactive.ShouldBeTrue();
        }

        [Fact]
        public void IsRunningInVisualStudioIsSetForResolverContext()
        {
            bool isRunningInVisualStudio = false;

            var service = new CachingSdkResolverService();
            using ResettableSdkResolverServiceState state = new(service, 
                resolvers: new List<SdkResolver>
                {
                    new SdkUtilities.ConfigurableMockSdkResolver((sdkRference, resolverContext, factory) =>
                    {
                        isRunningInVisualStudio = resolverContext.IsRunningInVisualStudio;
                        return null;
                    })
                });

            var result = service.ResolveSdk(
                BuildEventContext.InvalidSubmissionId,
                new SdkReference("foo", "1.0.0", null),
                _loggingContext,
                new MockElementLocation("file"),
                "sln",
                "projectPath",
                false,
                // Pass along isRunningInVisualStudio and expect it to be received in the SdkResolverContext
                isRunningInVisualStudio: true);

            isRunningInVisualStudio.ShouldBeTrue();
        }

        private class MockLoaderStrategy : SdkResolverLoader
        {
            private readonly bool _includeErrorResolver;

            public MockLoaderStrategy(bool includeErrorResolver = false)
            {
                _includeErrorResolver = includeErrorResolver;
            }

            internal override IList<SdkResolver> LoadResolvers(LoggingContext loggingContext, ElementLocation location)
            {
                List<SdkResolver> resolvers = new List<SdkResolver>
                {
                    new MockSdkResolver1(),
                    new MockSdkResolver2(),
                    new MockResolverReturnsNull(),
                    new MockSdkResolverWithState()
                };

                if (_includeErrorResolver)
                {
                    resolvers.Add(new MockSdkResolverThrows());
                }

                return resolvers.OrderBy(i => i.Priority).ToList();
            }
        }

        private class MockResolverReturnsNull : SdkResolver
        {
            public override string Name => nameof(MockResolverReturnsNull);

            public override int Priority => -1;

            public override SdkResultBase Resolve(SdkReference sdkReference, SdkResolverContextBase resolverContext, SdkResultFactoryBase factory) => null;
        }

        private class MockSdkResolver1 : SdkResolver
        {
            public override string Name => nameof(MockSdkResolver1);

            public override int Priority => 1;

            public override SdkResultBase Resolve(SdkReference sdk, SdkResolverContextBase resolverContext, SdkResultFactoryBase factory)
            {
                resolverContext.Logger.LogMessage("MockSdkResolver1 running", MessageImportance.Normal);

                if (sdk.Name.StartsWith("1"))
                    return factory.IndicateSuccess("resolverpath1", "version1");

                return factory.IndicateFailure(new[] {"ERROR1"});
            }
        }

        private class MockSdkResolver2 : SdkResolver
        {
            public override string Name => nameof(MockSdkResolver2);

            public override int Priority => 2;

            public override SdkResultBase Resolve(SdkReference sdk, SdkResolverContextBase resolverContext, SdkResultFactoryBase factory)
            {
                resolverContext.Logger.LogMessage("MockSdkResolver2 running", MessageImportance.Normal);

                if (sdk.Name.StartsWith("2"))
                    return factory.IndicateSuccess("resolverpath2", "version2", new[] {"WARNING2"});

                return factory.IndicateFailure(new[] {"ERROR2"}, new[] {"WARNING2"});
            }
        }

        private class MockSdkResolverWithState : SdkResolver
        {
            public const string Expected = "01713226A202458F97D9074168DF2618";

            public override string Name => nameof(MockSdkResolverWithState);

            public override int Priority => 3;

            public override SdkResultBase Resolve(SdkReference sdkReference, SdkResolverContextBase resolverContext, SdkResultFactoryBase factory)
            {
                if (sdkReference.Name.Equals("notfound"))
                {
                    return null;
                }
                if (resolverContext.State != null)
                {
                    return factory.IndicateSuccess((string) resolverContext.State, "1.0");
                }

                resolverContext.State = Expected;

                return factory.IndicateSuccess("resolverpath", "1.0");
            }
        }

        private class MockSdkResolverThrows : SdkResolver
        {
            public override string Name => nameof(MockSdkResolverThrows);
            public override int Priority => 0;

            public override SdkResultBase Resolve(SdkReference sdk, SdkResolverContextBase resolverContext, SdkResultFactoryBase factory)
            {
                resolverContext.Logger.LogMessage("MockSdkResolverThrows running", MessageImportance.Normal);

                throw new ArithmeticException("EXMESSAGE");
            }
        }
    }
}
