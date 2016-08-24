﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
//-----------------------------------------------------------------------
// </copyright>
// <summary>AssemblyInfo for Engine.</summary>
//-----------------------------------------------------------------------

using System;
using System.Reflection;
using System.Resources;
using System.Security.Permissions;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

#pragma warning disable 618
[assembly: SecurityPermission(SecurityAction.RequestMinimum, Flags = SecurityPermissionFlag.Execution)]
#pragma warning restore 618

[assembly: InternalsVisibleTo("Microsoft.Build.Engine.UnitTests, PublicKey=002400000480000094000000060200000024000052534131000400000100010015c01ae1f50e8cc09ba9eac9147cf8fd9fce2cfe9f8dce4f7301c4132ca9fb50ce8cbf1df4dc18dd4d210e4345c744ecb3365ed327efdbc52603faa5e21daa11234c8c4a73e51f03bf192544581ebe107adee3a34928e39d04e524a9ce729d5090bfd7dad9d10c722c0def9ccc08ff0a03790e48bcd1f9b6c476063e1966a1c4")]
[assembly: InternalsVisibleTo("Microsoft.Build.Conversion.Core, PublicKey=002400000480000094000000060200000024000052534131000400000100010007d1fa57c4aed9f0a32e84aa0faefd0de9e8fd6aec8f87fb03766c834c99921eb23be79ad9d5dcc1dd9ad236132102900b723cf980957fc4e177108fc607774f29e8320e92ea05ece4e821c0a5efe8f1645c4c0c93c1ab99285d622caa652c1dfad63d745d6f2de5f17e5eaf0fc4963d261c8a12436518206dc093344d5ad293")]
// DO NOT expose Internals to "Microsoft.Build.UnitTests.OM.OrcasCompatibility" as this assembly is supposed to only see public interface

// This will enable passing the SafeDirectories flag to any P/Invoke calls/implementations within the assembly, 
// so that we don't run into known security issues with loading libraries from unsafe locations 
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]

#if (LOCALIZED_BUILD)
// Needed for the "hub-and-spoke model to locate and retrieve localized resources": https://msdn.microsoft.com/en-us/library/21a15yht(v=vs.110).aspx
[assembly: NeutralResourcesLanguage("en", UltimateResourceFallbackLocation.Satellite)]
#endif

[assembly: CLSCompliant(true)]

[assembly: AssemblyTitle("Microsoft.Build.dll")]
[assembly: AssemblyDescription("Microsoft.Build.dll")]
[assembly: AssemblyCompany("Microsoft Corporation")]
[assembly: AssemblyProduct("MSBuild")]
[assembly: AssemblyCopyright("Microsoft Corporation. All rights reserved.")]
