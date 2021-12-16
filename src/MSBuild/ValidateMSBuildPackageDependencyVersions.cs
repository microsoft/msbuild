// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml;
namespace MSBuild
{
    public class ValidateMSBuildPackageDependencyVersions : Task
    {
        [Required]
        public string AppConfig { get; set; }
        [Required]
        public string AssemblyPath { get; set; }

        private string[] assembliesToIgnore = { "Microsoft.Build.Conversion.Core", "Microsoft.NET.StringTools.net35", "Microsoft.Build.Engine", "Microsoft.Activities.Build", "XamlBuildTask" };

        public override bool Execute()
        {
            XmlDocument doc = new XmlDocument();
            doc.Load(AppConfig);
            XmlNamespaceManager namespaceManager = new(doc.NameTable);
            namespaceManager.AddNamespace("asm", "urn:schemas-microsoft-com:asm.v1");
            bool foundSystemValueTuple = false;
            foreach (XmlElement dependentAssemblyElement in doc.DocumentElement.SelectNodes("/configuration/runtime/asm:assemblyBinding/asm:dependentAssembly[asm:assemblyIdentity][asm:bindingRedirect]", namespaceManager))
            {
                string name = (dependentAssemblyElement.SelectSingleNode("asm:assemblyIdentity", namespaceManager) as XmlElement).GetAttribute("name");
                string version = (dependentAssemblyElement.SelectSingleNode("asm:bindingRedirect", namespaceManager) as XmlElement).GetAttribute("newVersion");
                if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(version) && !assembliesToIgnore.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    string path = Path.Combine(AssemblyPath, name + ".dll");
                    string assemblyVersion = AssemblyName.GetAssemblyName(path).Version.ToString();
                    if (!version.Equals(assemblyVersion))
                    {
                        // It is unusual to want to redirect down, but in this case it's ok: 4.0.3.0 forwards to 4.0.0.0 in the GAC, so this just removes the need to redistribute a file
                        // and makes that resolution faster. Still verify that the versions are exactly as in this comment, as that may change.
                        if (String.Equals(name, "System.ValueTuple", StringComparison.OrdinalIgnoreCase) && String.Equals(version, "4.0.0.0") && String.Equals(assemblyVersion, "4.0.3.0"))
                        {
                            foundSystemValueTuple = true;
                        }
                        else
                        {
                            Log.LogError($"Binding redirect for '{name}' redirects to a different version ({version}) than MSBuild ships ({assemblyVersion}).");
                        }
                    }
                }
            }
            if (!foundSystemValueTuple)
            {
                Log.LogError("Binding redirect for 'System.ValueTuple' missing.");
            }
            return !Log.HasLoggedErrors;
        }
    }
}
