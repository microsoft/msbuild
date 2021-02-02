using System;
using System.IO;
using System.Reflection;
using System.Runtime.Versioning;
using Microsoft.Build.Shared;
using Microsoft.Build.Tasks;
using Microsoft.Build.Utilities;
using Newtonsoft.Json;
using Xunit;

namespace Microsoft.Build.UnitTests.ResolveAssemblyReference_Tests
{
    public class ResolveAssemblyReferenceCacheSerialization : IDisposable
    {
        // Maintain this two in sync with the constant in SystemState
        private static readonly byte[] TranslateContractSignature = new[] { (byte)'M', (byte)'B', (byte)'R', (byte)'S', (byte)'C', }; // Microsoft Build Rar State Cache
        private static readonly byte TranslateContractVersion = 0x01;

        private string _tempPath;
        private string _rarCacheFile;
        private TaskLoggingHelper _taskLoggingHelper;

        public ResolveAssemblyReferenceCacheSerialization()
        {
            _tempPath = Path.GetTempPath();
            _rarCacheFile = Path.Combine(_tempPath, Guid.NewGuid() + ".UnitTest.RarCache");
            _taskLoggingHelper = new TaskLoggingHelper(new MockEngine(), "TaskA")
            {
                TaskResources = AssemblyResources.PrimaryResources
            };
        }

        public void Dispose()
        {
            if (File.Exists(_rarCacheFile))
            {
                FileUtilities.DeleteNoThrow(_rarCacheFile);
            }
        }

        [Fact]
        public void RoundTripEmptyState()
        {
            SystemState systemState = new();

            systemState.SerializeCacheByTranslator(_rarCacheFile, _taskLoggingHelper);

            var deserialized = SystemState.DeserializeCacheByTranslator(_rarCacheFile, _taskLoggingHelper);

            Assert.NotNull(deserialized);
        }

        [Fact]
        public void WrongFileSignature()
        {
            SystemState systemState = new();

            for (int i = 0; i < TranslateContractSignature.Length; i++)
            {
                systemState.SerializeCacheByTranslator(_rarCacheFile, _taskLoggingHelper);
                using (var cacheStream = new FileStream(_rarCacheFile, FileMode.Open, FileAccess.ReadWrite))
                {
                    cacheStream.Seek(i, SeekOrigin.Begin);
                    cacheStream.WriteByte(0);
                    cacheStream.Close();
                }

                var deserialized = SystemState.DeserializeCacheByTranslator(_rarCacheFile, _taskLoggingHelper);
                Assert.Null(deserialized);
            }
        }

        [Fact]
        public void WrongFileVersion()
        {
            SystemState systemState = new();

            systemState.SerializeCacheByTranslator(_rarCacheFile, _taskLoggingHelper);
            using (var cacheStream = new FileStream(_rarCacheFile, FileMode.Open, FileAccess.ReadWrite))
            {
                cacheStream.Seek(TranslateContractSignature.Length, SeekOrigin.Begin);
                cacheStream.WriteByte((byte) (TranslateContractVersion + 1));
                cacheStream.Close();
            }

            var deserialized = SystemState.DeserializeCacheByTranslator(_rarCacheFile, _taskLoggingHelper);
            Assert.Null(deserialized);
        }

        [Fact]
        public void CorrectFileSignature()
        {
            SystemState systemState = new();

            for (int i = 0; i < TranslateContractSignature.Length; i++)
            {
                systemState.SerializeCacheByTranslator(_rarCacheFile, _taskLoggingHelper);
                using (var cacheStream = new FileStream(_rarCacheFile, FileMode.Open, FileAccess.ReadWrite))
                {
                    cacheStream.Seek(i, SeekOrigin.Begin);
                    cacheStream.WriteByte(TranslateContractSignature[i]);
                    cacheStream.Close();
                }

                var deserialized = SystemState.DeserializeCacheByTranslator(_rarCacheFile, _taskLoggingHelper);
                Assert.NotNull(deserialized);
            }
        }

        [Fact]
        public void CorrectFileVersion()
        {
            SystemState systemState = new();

            systemState.SerializeCacheByTranslator(_rarCacheFile, _taskLoggingHelper);
            using (var cacheStream = new FileStream(_rarCacheFile, FileMode.Open, FileAccess.ReadWrite))
            {
                cacheStream.Seek(TranslateContractSignature.Length, SeekOrigin.Begin);
                cacheStream.WriteByte(TranslateContractVersion);
                cacheStream.Close();
            }

            var deserialized = SystemState.DeserializeCacheByTranslator(_rarCacheFile, _taskLoggingHelper);
            Assert.NotNull(deserialized);
        }

        [Theory]
        [InlineData("Microsoft.VisualStudio.LanguageServices.Implementation.csprojAssemblyReference.cache")]
        [InlineData("Microsoft.CodeAnalysis.csprojAssemblyReference.cache")]
        [InlineData("Microsoft.CodeAnalysis.VisualBasic.Emit.UnitTests.vbprojAssemblyReference.cache")]
        [InlineData("Roslyn.Compilers.Extension.csprojAssemblyReference.cache")]
        public void RoundTripSampleFileState(string sampleName)
        {
            var fileSample = GetTestPayloadFileName($@"AssemblyDependency\CacheFileSamples\BinaryFormatter\{sampleName}");
            // read old file
            var deserialized = SystemState.DeserializeCacheByBinaryFormatter(fileSample, _taskLoggingHelper);
            // white as TR
            deserialized.SerializeCacheByTranslator(_rarCacheFile, _taskLoggingHelper);
            // read as TR
            var deserializedByTranslator = SystemState.DeserializeCacheByTranslator(_rarCacheFile, _taskLoggingHelper);

            var sOld = JsonConvert.SerializeObject(deserialized, Formatting.Indented);
            var sNew = JsonConvert.SerializeObject(deserializedByTranslator, Formatting.Indented);
            Assert.Equal(sOld, sNew);
        }

        [Fact]
        public void VerifySampleStateDeserialization()
        {
            // This test might also fail when binary format is modified.
            // Any change in SystemState and child class ITranslatable implementation will most probably make this fail.
            // To fix it, file referred by 'sampleName' needs to be recaptured and constant bellow modified to reflect
            // the content of that cache.
            // This sample was captured by compiling https://github.com/dotnet/roslyn/commit/f8107de2a94a01e96ac3d7c1f225acbb61e18830
            const string sampleName = "Microsoft.VisualStudio.LanguageServices.Implementation.csprojAssemblyReference.cache";
            const string expectedAssemblyPath = @"C:\Users\rokon\.nuget\packages\microsoft.visualstudio.codeanalysis.sdk.ui\15.8.27812-alpha\lib\net46\Microsoft.VisualStudio.CodeAnalysis.Sdk.UI.dll";
            const long expectedAssemblyLastWriteTimeTicks = 636644382480000000;
            const string expectedAssemblyName = "Microsoft.VisualStudio.CodeAnalysis.Sdk.UI, Version=15.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a";
            const string expectedFrameworkName = ".NETFramework,Version=v4.5";
            var expectedDependencies = new[]
            {
                "mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089",
                "System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089",
                "System.Drawing, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a",
                "Microsoft.VisualStudio.CodeAnalysis, Version=15.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a",
                "Microsoft.VisualStudio.DeveloperTools, Version=15.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a",
                "System, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089",
                "Microsoft.VisualStudio.Shell.Interop, Version=7.1.40304.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a",
                "EnvDTE, Version=8.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a",
                "Microsoft.VisualStudio.CodeAnalysis.Sdk, Version=15.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a",
                "Microsoft.Build.Framework, Version=15.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a",
                "Microsoft.VisualStudio.Text.Logic, Version=15.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a",
                "Microsoft.VisualStudio.Text.UI, Version=15.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a",
                "Microsoft.VisualStudio.Text.Data, Version=15.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a",
                "Microsoft.VisualStudio.Text.UI.Wpf, Version=15.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a",
                "Microsoft.VisualStudio.ComponentModelHost, Version=15.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a",
                "Microsoft.VisualStudio.VSHelp, Version=7.0.3300.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a",
                "Microsoft.VisualStudio.Shell.Interop.11.0, Version=11.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a",
                "Microsoft.VisualStudio.VCProjectEngine, Version=15.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a",
                "Microsoft.VisualStudio.Shell.15.0, Version=15.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a",
                "Microsoft.VisualStudio.OLE.Interop, Version=7.1.40304.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a",
                "System.Xml, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089",
                "Microsoft.VisualStudio.TextManager.Interop, Version=7.1.40304.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a",
                "EnvDTE80, Version=8.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a",
                "System.Core, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089",
                "Microsoft.VisualStudio.VirtualTreeGrid, Version=15.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a",
                "Microsoft.VisualStudio.Shell.Interop.8.0, Version=8.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a",
                "Microsoft.VisualStudio.Editor, Version=15.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a",
            };


            var fileSample = GetTestPayloadFileName($@"AssemblyDependency\CacheFileSamples\{sampleName}");
            var deserializedByTranslator = SystemState.DeserializeCacheByTranslator(fileSample, _taskLoggingHelper);

            deserializedByTranslator.SetGetLastWriteTime(path =>
            {
                if (path != expectedAssemblyPath)
                    throw new InvalidOperationException("Unexpected file name for this test case");

                return new DateTime(expectedAssemblyLastWriteTimeTicks, DateTimeKind.Utc);
            });

            GetAssemblyName getAssemblyName = deserializedByTranslator.CacheDelegate((GetAssemblyName)null);
            GetAssemblyMetadata getAssemblyMetadata = deserializedByTranslator.CacheDelegate((GetAssemblyMetadata)null);

            var assemblyName = getAssemblyName(expectedAssemblyPath);
            getAssemblyMetadata(expectedAssemblyPath, null,
                out AssemblyNameExtension[] dependencies,
                out string[] scatterFiles,
                out FrameworkName frameworkNameAttribute);

            Assert.NotNull(assemblyName);
            Assert.Equal(
                new AssemblyNameExtension(expectedAssemblyName, false),
                assemblyName);
            Assert.Empty(scatterFiles);
            Assert.Equal(
                new FrameworkName(expectedFrameworkName),
                frameworkNameAttribute);

            Assert.NotNull(dependencies);
            Assert.Equal(expectedDependencies.Length, dependencies.Length);
            foreach (var expectedDependency in expectedDependencies)
            {
                Assert.Contains(new AssemblyNameExtension(expectedDependency), dependencies);
            }
        }

        private static string GetTestPayloadFileName(string name)
        {
            var codeBaseUrl = new Uri(Assembly.GetExecutingAssembly().CodeBase);
            var codeBasePath = Uri.UnescapeDataString(codeBaseUrl.AbsolutePath);
            var dirPath = Path.GetDirectoryName(codeBasePath) ?? string.Empty;
            return Path.Combine(dirPath, name);
        }
    }
}
