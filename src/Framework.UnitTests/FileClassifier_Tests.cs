// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO;
using Microsoft.Build.Shared;
using Shouldly;
using Xunit;

#nullable disable

namespace Microsoft.Build.Framework.UnitTests
{
    public class FileClassifierTests
    {
        private sealed class FileClassifierUnderTest : FileClassifier
        {
            public void RegisterImmutableDirectory(string directory)
                => base.RegisterImmutableDirectory(directory, false);
        }

        [Fact]
        public void Shared_ReturnsInstance()
        {
            FileClassifier.Shared.ShouldNotBeNull();
        }

        [Fact]
        public void IsNonModifiable_EvaluatesModifiability()
        {
            FileClassifierUnderTest classifier = new();

            var volume = NativeMethodsShared.IsWindows ? @"X:\" : "/home/usr";
            classifier.RegisterImmutableDirectory($"{Path.Combine(volume, "Test1")}");
            classifier.RegisterImmutableDirectory($"{Path.Combine(volume, "Test2")}");

            classifier.IsNonModifiable(Path.Combine(volume, "Test1", "File.ext")).ShouldBeTrue();
            classifier.IsNonModifiable(Path.Combine(volume, "Test2", "File.ext")).ShouldBeTrue();
            classifier.IsNonModifiable(Path.Combine(volume, "Test3", "File.ext")).ShouldBeFalse();
        }

        [Fact]
        public void IsNonModifiable_DuplicateNugetRegistry_EvaluatesModifiability()
        {
            FileClassifierUnderTest classifier = new();

            var volume = NativeMethodsShared.IsWindows ? @"X:\" : "/home/usr";

            for (int i = 0; i < 3; ++i)
            {
                classifier.RegisterImmutableDirectory($"{Path.Combine(volume, "Test1")}");
                classifier.RegisterImmutableDirectory($"{Path.Combine(volume, "Test2")}");
            }

            classifier.IsNonModifiable(Path.Combine(volume, "Test1", "File.ext")).ShouldBeTrue();
            classifier.IsNonModifiable(Path.Combine(volume, "Test2", "File.ext")).ShouldBeTrue();
            classifier.IsNonModifiable(Path.Combine(volume, "Test3", "File.ext")).ShouldBeFalse();
        }

        [Fact]
        public void IsNonModifiable_RespectsOSCaseSensitivity()
        {
            FileClassifierUnderTest classifier = new();

            var volume = NativeMethodsShared.IsWindows ? @"X:\" : "/home/usr";
            classifier.RegisterImmutableDirectory($"{Path.Combine(volume, "Test1")}");

            if (NativeMethodsShared.IsLinux)
            {
                classifier.IsNonModifiable(Path.Combine(volume, "Test1", "File.ext")).ShouldBeTrue();
                classifier.IsNonModifiable(Path.Combine(volume, "test1", "File.ext")).ShouldBeFalse();
            }
            else
            {
                classifier.IsNonModifiable(Path.Combine(volume, "Test1", "File.ext")).ShouldBeTrue();
                classifier.IsNonModifiable(Path.Combine(volume, "test1", "File.ext")).ShouldBeTrue();
            }
        }

        [Fact]
        public void IsNonModifiable_DoesntThrowWhenPackageFoldersAreNotRegistered()
        {
            FileClassifierUnderTest classifier = new();

            classifier.IsNonModifiable("X:\\Test3\\File.ext").ShouldBeFalse();
        }
    }
}
