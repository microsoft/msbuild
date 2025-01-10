// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Configuration.Assemblies;
using System.Globalization;
using System.IO;
using System.Reflection;
#if !TASKHOST
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
#endif
using System.Runtime.Serialization;
using System.Text;
using Microsoft.Build.BackEnd;

#nullable disable

namespace Microsoft.Build.Shared
{
    /// <summary>
    /// Specifies the parts of the assembly name to partially match.
    /// </summary>
    [FlagsAttribute]
    internal enum PartialComparisonFlags : int
    {
        /// <summary>
        /// Compare SimpleName  A.PartialCompare(B,SimpleName)  match the simple name on A and B if the simple name on A is not null.
        /// </summary>
        SimpleName = 1, // 0000 0000 0000 0001

        /// <summary>
        /// Compare Version A.PartialCompare(B, Version)  match the Version on A and B if the Version on A is not null.
        /// </summary>
        Version = 2, // 0000 0000 0000 0010

        /// <summary>
        /// Compare Culture A.PartialCompare(B, Culture)  match the Culture on A and B if the Culture on A is not null.
        /// </summary>
        Culture = 4, // 0000 0000 0000 0100

        /// <summary>
        /// Compare PublicKeyToken A.PartialCompare(B, PublicKeyToken)  match the PublicKeyToken on A and B if the PublicKeyToken on A is not null.
        /// </summary>
        PublicKeyToken = 8, // 0000 0000 0000 1000

        /// <summary>
        /// When doing a comparison   A.PartialCompare(B, Default) compare all fields of A which are not null with B.
        /// </summary>
        Default = 15, // 0000 0000 0000 1111
    }

    /// <summary>
    /// A replacement for AssemblyName that optimizes calls to FullName which is expensive.
    /// The assembly name is represented internally by an AssemblyName and a string, conversion
    /// between the two is done lazily on demand.
    /// </summary>
    [Serializable]
    internal sealed class AssemblyNameExtension : ISerializable, IEquatable<AssemblyNameExtension>, ITranslatable
    {
        private AssemblyName asAssemblyName = null;
        private string asString = null;
        private bool isSimpleName = false;
        private bool hasProcessorArchitectureInFusionName;
        private bool immutable;

        /// <summary>
        /// Set of assemblyNameExtensions that THIS assemblyname was remapped from.
        /// </summary>
        private HashSet<AssemblyNameExtension> remappedFrom;

        private static readonly AssemblyNameExtension s_unnamedAssembly = new AssemblyNameExtension();

        /// <summary>
        /// Construct an unnamed assembly.
        /// Private because we want only one of these.
        /// </summary>
        private AssemblyNameExtension()
        {
        }

        /// <summary>
        /// Construct with AssemblyName.
        /// </summary>
        /// <param name="assemblyName"></param>
        internal AssemblyNameExtension(AssemblyName assemblyName) : this()
        {
            asAssemblyName = assemblyName;
        }

        /// <summary>
        /// Construct with string.
        /// </summary>
        /// <param name="assemblyName"></param>
        internal AssemblyNameExtension(string assemblyName) : this()
        {
            asString = assemblyName;
        }

        /// <summary>
        /// Construct from a string, but immediately construct a real AssemblyName.
        /// This will cause an exception to be thrown up front if the assembly name
        /// isn't well formed.
        /// </summary>
        /// <param name="assemblyName">
        /// The string version of the assembly name.
        /// </param>
        /// <param name="validate">
        /// Used when the assembly name comes from a user-controlled source like a project file or config file.
        /// Does extra checking on the assembly name and will throw exceptions if something is invalid.
        /// </param>
        internal AssemblyNameExtension(string assemblyName, bool validate) : this()
        {
            asString = assemblyName;

            if (validate)
            {
                // This will throw...
                CreateAssemblyName();
            }
        }

        /// <summary>
        /// Ctor for deserializing from state file (binary serialization).
        /// <remarks>This is required because AssemblyName is not Serializable on .NET Core.</remarks>
        /// </summary>
        /// <param name="info"></param>
        /// <param name="context"></param>
        private AssemblyNameExtension(SerializationInfo info, StreamingContext context)
        {
            var hasAssemblyName = info.GetBoolean("hasAN");

            if (hasAssemblyName)
            {
                var name = info.GetString("name");
                var publicKey = (byte[])info.GetValue("pk", typeof(byte[]));
                var publicKeyToken = (byte[])info.GetValue("pkt", typeof(byte[]));
                var version = (Version)info.GetValue("ver", typeof(Version));
                var flags = (AssemblyNameFlags)info.GetInt32("flags");
                var processorArchitecture = (ProcessorArchitecture)info.GetInt32("cpuarch");

                CultureInfo cultureInfo = null;
                var hasCultureInfo = info.GetBoolean("hasCI");
                if (hasCultureInfo)
                {
                    cultureInfo = new CultureInfo(info.GetInt32("ci"));
                }

                var hashAlgorithm = (System.Configuration.Assemblies.AssemblyHashAlgorithm)info.GetInt32("hashAlg");
                var versionCompatibility = (AssemblyVersionCompatibility)info.GetInt32("verCompat");
                var codeBase = info.GetString("codebase");

                asAssemblyName = new AssemblyName
                {
                    Name = name,
                    Version = version,
                    Flags = flags,
                    ProcessorArchitecture = processorArchitecture,
                    CultureInfo = cultureInfo,
                    HashAlgorithm = hashAlgorithm,
                    VersionCompatibility = versionCompatibility,
                    CodeBase = codeBase,
                };

                asAssemblyName.SetPublicKey(publicKey);
                asAssemblyName.SetPublicKeyToken(publicKeyToken);
            }

            asString = info.GetString("asStr");
            isSimpleName = info.GetBoolean("isSName");
            hasProcessorArchitectureInFusionName = info.GetBoolean("hasCpuArch");
            immutable = info.GetBoolean("immutable");
            remappedFrom = (HashSet<AssemblyNameExtension>)info.GetValue("remapped", typeof(HashSet<AssemblyNameExtension>));
        }

        /// <summary>
        /// Ctor for deserializing from state file (custom binary serialization) using translator.
        /// </summary>
        internal AssemblyNameExtension(ITranslator translator) : this()
        {
            Translate(translator);
        }

        /// <summary>
        /// To be used as a delegate. Gets the AssemblyName of the given file.
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>
        internal static AssemblyNameExtension GetAssemblyNameEx(string path)
        {
            try
            {
#if TASKHOST
                return new AssemblyNameExtension(AssemblyName.GetAssemblyName(path));
#else

                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var pe = new PEReader(fs);
                if (pe.HasMetadata)
                {
                    var reader = pe.GetMetadataReader();
                    var def = reader.GetAssemblyDefinition();

                    var assemblyName = new AssemblyName
                    {
                        Name = reader.GetString(def.Name),
                        Version = def.Version,
                        CultureName = !def.Culture.IsNil ? reader.GetString(def.Culture) : null,
                        Flags = GetAssemblyNameFlags(def.Flags)
                    };

                    // Set processor architecture from PE headers only
                    var peHeaders = pe.PEHeaders;
                    var machine = peHeaders.CoffHeader.Machine;
                    var characteristics = peHeaders.CoffHeader.Characteristics;
                    var corFlags = peHeaders.CorHeader.Flags;

                    if (!def.PublicKey.IsNil)
                    {
                        var publicKeyBytes = reader.GetBlobBytes(def.PublicKey);
                        assemblyName.SetPublicKey(publicKeyBytes);
                    }

                    return new AssemblyNameExtension(assemblyName);
                }
#endif
            }
            catch (FileLoadException)
            {
                // Its pretty hard to get here, you need an assembly that contains a valid reference
                // to a dependent assembly that, in turn, throws a FileLoadException during GetAssemblyName.
                // Still it happened once, with an older version of the CLR.
            }
            catch (FileNotFoundException)
            {
                // Its pretty hard to get here, also since we do a file existence check right before calling this method so it can only happen if the file got deleted between that check and this call.
            }

            return null;
        }

#if !TASKHOST
        private static AssemblyNameFlags GetAssemblyNameFlags(AssemblyFlags flags)
        {
            var result = AssemblyNameFlags.None;

            if ((flags & AssemblyFlags.PublicKey) != 0)
            {
                result |= AssemblyNameFlags.PublicKey;
            }

            if ((flags & AssemblyFlags.Retargetable) != 0)
            {
                result |= AssemblyNameFlags.Retargetable;
            }

            return result;
        }
#endif

        /// <summary>
        /// Run after the object has been deserialized
        /// </summary>
        [OnDeserialized]
        private void SetRemappedFromDefaultAfterSerialization(StreamingContext sc)
        {
            InitializeRemappedFrom();
        }

        /// <summary>
        /// Initialize the remapped from structure.
        /// </summary>
        private void InitializeRemappedFrom()
        {
            if (remappedFrom == null)
            {
                remappedFrom = CreateRemappedFrom();
            }
        }

        /// <summary>
        /// Create remappedFrom HashSet. Used by deserialization as well.
        /// </summary>
        private static HashSet<AssemblyNameExtension> CreateRemappedFrom()
        {
            return new HashSet<AssemblyNameExtension>(AssemblyNameComparer.GenericComparerConsiderRetargetable);
        }

        /// <summary>
        /// Assume there is a string version, create the AssemblyName version.
        /// </summary>
        private void CreateAssemblyName()
        {
            if (asAssemblyName == null)
            {
                asAssemblyName = GetAssemblyNameFromDisplayName(asString);

                if (asAssemblyName != null)
                {
                    hasProcessorArchitectureInFusionName = asString.IndexOf("ProcessorArchitecture", StringComparison.OrdinalIgnoreCase) != -1;
                    isSimpleName = ((Version == null) && (CultureInfo == null) && (GetPublicKeyToken() == null) && (!hasProcessorArchitectureInFusionName));
                }
            }
        }

        /// <summary>
        /// Assume there is a string version, create the AssemblyName version.
        /// </summary>
        private void CreateFullName()
        {
            if (asString == null)
            {
                asString = asAssemblyName.FullName;
            }
        }

        /// <summary>
        /// The base name of the assembly.
        /// </summary>
        /// <value></value>
        internal string Name
        {
            get
            {
                // Is there a string?
                CreateAssemblyName();
                return asAssemblyName.Name;
            }
        }

        /// <summary>
        /// Gets the backing AssemblyName, this can be None.
        /// </summary>
        internal ProcessorArchitecture ProcessorArchitecture =>
            asAssemblyName?.ProcessorArchitecture ?? ProcessorArchitecture.None;

        /// <summary>
        /// The assembly's version number.
        /// </summary>
        /// <value></value>
        internal Version Version
        {
            get
            {
                // Is there a string?
                CreateAssemblyName();
                return asAssemblyName.Version;
            }
        }

        /// <summary>
        /// Is the assembly a complex name or a simple name. A simple name is where only the name is set
        /// a complex name is where the version, culture or publickeytoken is also set
        /// </summary>
        internal bool IsSimpleName
        {
            get
            {
                CreateAssemblyName();
                return isSimpleName;
            }
        }

        /// <summary>
        /// Does the fullName have the processor architecture defined
        /// </summary>
        internal bool HasProcessorArchitectureInFusionName
        {
            get
            {
                CreateAssemblyName();
                return hasProcessorArchitectureInFusionName;
            }
        }

        /// <summary>
        /// Replace the current version with a new version.
        /// </summary>
        /// <param name="version"></param>
        internal void ReplaceVersion(Version version)
        {
            ErrorUtilities.VerifyThrow(!immutable, "Object is immutable cannot replace the version");
            CreateAssemblyName();
            if (asAssemblyName.Version != version)
            {
                asAssemblyName.Version = version;

                // String would now be invalid.
                asString = null;
            }
        }

        /// <summary>
        /// The assembly's Culture
        /// </summary>
        /// <value></value>
        internal CultureInfo CultureInfo
        {
            get
            {
                // Is there a string?
                CreateAssemblyName();
                return asAssemblyName.CultureInfo;
            }
        }

        /// <summary>
        /// The assembly's retargetable bit
        /// </summary>
        /// <value></value>
        internal bool Retargetable
        {
            get
            {
                // Is there a string?
                CreateAssemblyName();
                // Cannot use the HasFlag method on the Flags enum because this class needs to work with 3.5
                return (asAssemblyName.Flags & AssemblyNameFlags.Retargetable) == AssemblyNameFlags.Retargetable;
            }
        }

        /// <summary>
        /// The full name of the original extension we were before being remapped.
        /// </summary>
        internal IEnumerable<AssemblyNameExtension> RemappedFromEnumerator
        {
            get
            {
                InitializeRemappedFrom();
                return remappedFrom;
            }
        }

        /// <summary>
        /// Add an assemblyNameExtension which represents an assembly name which was mapped to THIS assemblyName.
        /// </summary>
        internal void AddRemappedAssemblyName(AssemblyNameExtension extensionToAdd)
        {
            ErrorUtilities.VerifyThrow(extensionToAdd.Immutable, "ExtensionToAdd is not immutable");
            InitializeRemappedFrom();
            remappedFrom.Add(extensionToAdd);
        }

        /// <summary>
        /// As an AssemblyName
        /// </summary>
        /// <value></value>
        internal AssemblyName AssemblyName
        {
            get
            {
                // Is there a string?
                CreateAssemblyName();
                return asAssemblyName;
            }
        }

        /// <summary>
        /// The assembly's full name.
        /// </summary>
        /// <value></value>
        internal string FullName
        {
            get
            {
                // Is there a string?
                CreateFullName();
                return asString;
            }
        }

        /// <summary>
        /// Get the assembly's public key token.
        /// </summary>
        /// <returns></returns>
        internal byte[] GetPublicKeyToken()
        {
            // Is there a string?
            CreateAssemblyName();
            return asAssemblyName.GetPublicKeyToken();
        }


        /// <summary>
        /// A special "unnamed" instance of AssemblyNameExtension.
        /// </summary>
        /// <value></value>
        internal static AssemblyNameExtension UnnamedAssembly => s_unnamedAssembly;

        /// <summary>
        /// Compare one assembly name to another.
        /// </summary>
        /// <param name="that"></param>
        /// <returns></returns>
        internal int CompareTo(AssemblyNameExtension that)
        {
            return CompareTo(that, false);
        }

        /// <summary>
        /// Compare one assembly name to another.
        /// </summary>
        internal int CompareTo(AssemblyNameExtension that, bool considerRetargetableFlag)
        {
            // Are they identical?
            if (this.Equals(that, considerRetargetableFlag))
            {
                return 0;
            }

            // Are the base names not identical?
            int result = CompareBaseNameTo(that);
            if (result != 0)
            {
                return result;
            }

            // We would like to compare the version numerically rather than alphabetically (because for example version 10.0.0. should be below 9 not between 1 and 2)
            if (this.Version != that.Version)
            {
                if (this.Version == null)
                {
                    // This is therefore less than that. Since this is null and that is not null
                    return -1;
                }

                // Will not return 0 as the this != that check above takes care of the case where they are equal.
                return this.Version.CompareTo(that.Version);
            }

            // We need some final collating order for these, alphabetical by FullName seems as good as any.
            return string.Compare(this.FullName, that.FullName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Get a hash code for this assembly name.
        /// </summary>
        /// <returns></returns>
        internal new int GetHashCode()
        {
            // Ok, so this isn't a great hashing algorithm. However, basenames with different
            // versions or PKTs are relatively uncommon and so collisions should be low.
            // Hashing on FullName is wrong because the order of tuple fields is undefined.
            int hash = StringComparer.OrdinalIgnoreCase.GetHashCode(this.Name);
            return hash;
        }

        /// <summary>
        /// Compare two base names as quickly as possible.
        /// </summary>
        /// <param name="that"></param>
        /// <returns></returns>
        internal int CompareBaseNameTo(AssemblyNameExtension that)
        {
            int result = CompareBaseNameToImpl(that);
#if DEBUG
            // Now, compare to the real value to make sure the result was accurate.
            AssemblyName a1 = asAssemblyName;
            AssemblyName a2 = that.asAssemblyName;
            if (a1 == null)
            {
                a1 = new AssemblyName(asString);
            }
            if (a2 == null)
            {
                a2 = new AssemblyName(that.asString);
            }

            int baselineResult = string.Compare(a1.Name, a2.Name, StringComparison.OrdinalIgnoreCase);
            ErrorUtilities.VerifyThrow(result == baselineResult, "Optimized version of CompareBaseNameTo didn't return the same result as the baseline.");
#endif
            return result;
        }

        /// <summary>
        /// An implementation of compare that compares two base
        /// names as quickly as possible.
        /// </summary>
        /// <param name="that"></param>
        /// <returns></returns>
        private int CompareBaseNameToImpl(AssemblyNameExtension that)
        {
            // Pointer compare, if identical then base names are equal.
            if (this == that)
            {
                return 0;
            }

            // Do both have assembly names?
            if (asAssemblyName != null && that.asAssemblyName != null)
            {
                // Pointer compare or base name compare.
                return asAssemblyName == that.asAssemblyName
                    ? 0
                    : string.Compare(asAssemblyName.Name, that.asAssemblyName.Name, StringComparison.OrdinalIgnoreCase);
            }

            // Do both have strings?
            if (asString != null && that.asString != null)
            {
                // If we have two random-case strings, then we need to compare case sensitively.
                return CompareBaseNamesStringWise(asString, that.asString);
            }

            // Fall back to comparing by name. This is the slow path.
            return string.Compare(this.Name, that.Name, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Compare two basenames.
        /// </summary>
        /// <param name="asString1"></param>
        /// <param name="asString2"></param>
        /// <returns></returns>
        private static int CompareBaseNamesStringWise(string asString1, string asString2)
        {
            // Identical strings just match.
            if (asString1 == asString2)
            {
                return 0;
            }

            // Get the lengths of base names to compare.
            int baseLenThis = asString1.IndexOf(',');
            int baseLenThat = asString2.IndexOf(',');
            if (baseLenThis == -1)
            {
                baseLenThis = asString1.Length;
            }
            if (baseLenThat == -1)
            {
                baseLenThat = asString2.Length;
            }

            // If the lengths are the same then we can compare without copying.
            if (baseLenThis == baseLenThat)
            {
                return string.Compare(asString1, 0, asString2, 0, baseLenThis, StringComparison.OrdinalIgnoreCase);
            }

            // Lengths are different, so string copy is required.
            string nameThis = asString1.Substring(0, baseLenThis);
            string nameThat = asString2.Substring(0, baseLenThat);
            return string.Compare(nameThis, nameThat, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Clone this assemblyNameExtension
        /// </summary>
        internal AssemblyNameExtension Clone()
        {
            AssemblyNameExtension newExtension = new();

            if (asAssemblyName != null)
            {
                newExtension.asAssemblyName = asAssemblyName.CloneIfPossible();
            }

            newExtension.asString = asString;
            newExtension.isSimpleName = isSimpleName;
            newExtension.hasProcessorArchitectureInFusionName = hasProcessorArchitectureInFusionName;
            newExtension.remappedFrom = remappedFrom;

            // We are cloning so we can now party on the object even if the parent was immutable
            newExtension.immutable = false;

            return newExtension;
        }

        /// <summary>
        /// Clone the object but mark and mark the cloned object as immutable
        /// </summary>
        /// <returns></returns>
        internal AssemblyNameExtension CloneImmutable()
        {
            AssemblyNameExtension clonedExtension = Clone();
            clonedExtension.MarkImmutable();
            return clonedExtension;
        }

        /// <summary>
        /// Is this object immutable
        /// </summary>
        public bool Immutable => immutable;

        /// <summary>
        /// Mark this object as immutable
        /// </summary>
        internal void MarkImmutable()
        {
            immutable = true;
        }

        /// <summary>
        /// Compare two assembly names for equality.
        /// </summary>
        /// <param name="that"></param>
        /// <returns></returns>
        internal bool Equals(AssemblyNameExtension that)
        {
            return EqualsImpl(that, false, false);
        }

        /// <summary>
        /// Interface method for IEquatable&lt;AssemblyNameExtension&gt;
        /// </summary>
        /// <param name="other"></param>
        /// <returns></returns>
        bool IEquatable<AssemblyNameExtension>.Equals(AssemblyNameExtension other)
        {
            return Equals(other);
        }

        /// <summary>
        /// Compare two assembly names for equality ignoring version.
        /// </summary>
        /// <param name="that"></param>
        /// <returns></returns>
        internal bool EqualsIgnoreVersion(AssemblyNameExtension that)
        {
            return EqualsImpl(that, true, false);
        }

        /// <summary>
        /// Compare two assembly names and consider the retargetable flag during the comparison
        /// </summary>
        internal bool Equals(AssemblyNameExtension that, bool considerRetargetableFlag)
        {
            return EqualsImpl(that, false, considerRetargetableFlag);
        }

        /// <summary>
        /// Compare two assembly names for equality.
        /// </summary>
        private bool EqualsImpl(AssemblyNameExtension that, bool ignoreVersion, bool considerRetargetableFlag)
        {
            // Pointer compare.
            if (object.ReferenceEquals(this, that))
            {
                return true;
            }

            // If that is null then this and that are not equal. Also, this would cause a crash on the next line.
            if (that is null)
            {
                return false;
            }

            // Do both have assembly names?
            if (asAssemblyName != null && that.asAssemblyName != null)
            {
                // Pointer compare.
                if (object.ReferenceEquals(asAssemblyName, that.asAssemblyName))
                {
                    return true;
                }
            }

            // Do both have strings that equal each-other?
            if (asString != null && that.asString != null)
            {
                if (asString == that.asString)
                {
                    return true;
                }

                // If they weren't identical then they might still differ only by
                // case. So we can't assume that they don't match. So fall through...
            }

            // Do the names match?
            if (!string.Equals(Name, that.Name, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!ignoreVersion && (this.Version != that.Version))
            {
                return false;
            }

            if (!CompareCultures(AssemblyName, that.AssemblyName))
            {
                return false;
            }

            if (!ComparePublicKeyToken(that))
            {
                return false;
            }

            if (considerRetargetableFlag && this.Retargetable != that.Retargetable)
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Allows the comparison of the culture.
        /// </summary>
        internal static bool CompareCultures(AssemblyName a, AssemblyName b)
        {
            // Do the Cultures match?
            CultureInfo aCulture = a.CultureInfo;
            CultureInfo bCulture = b.CultureInfo;
            if (aCulture == null)
            {
                aCulture = CultureInfo.InvariantCulture;
            }
            if (bCulture == null)
            {
                bCulture = CultureInfo.InvariantCulture;
            }

            return aCulture.LCID == bCulture.LCID;
        }

        /// <summary>
        ///  Allows the comparison of just the PublicKeyToken
        /// </summary>
        internal bool ComparePublicKeyToken(AssemblyNameExtension that)
        {
            // Do the PKTs match?
            byte[] aPKT = GetPublicKeyToken();
            byte[] bPKT = that.GetPublicKeyToken();
            return ComparePublicKeyTokens(aPKT, bPKT);
        }

        /// <summary>
        /// Compare two public key tokens.
        /// </summary>
        internal static bool ComparePublicKeyTokens(byte[] aPKT, byte[] bPKT)
        {
            // Some assemblies (real case was interop assembly) may have null PKTs.
            if (aPKT == null)
            {
#pragma warning disable CA1825 // Avoid zero-length array allocations
                aPKT = new byte[0];
#pragma warning restore CA1825 // Avoid zero-length array allocations
            }
            if (bPKT == null)
            {
#pragma warning disable CA1825 // Avoid zero-length array allocations
                bPKT = new byte[0];
#pragma warning restore CA1825 // Avoid zero-length array allocations
            }

            if (aPKT.Length != bPKT.Length)
            {
                return false;
            }
            for (int i = 0; i < aPKT.Length; ++i)
            {
                if (aPKT[i] != bPKT[i])
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Only the unnamed assembly has both null assemblyname and null string.
        /// </summary>
        /// <returns></returns>
        internal bool IsUnnamedAssembly => asAssemblyName == null && asString == null;

        /// <summary>
        /// Given a display name, construct an assembly name.
        /// </summary>
        /// <param name="displayName">The display name.</param>
        /// <returns>The assembly name.</returns>
        private static AssemblyName GetAssemblyNameFromDisplayName(string displayName)
        {
            AssemblyName assemblyName = new AssemblyName(displayName);
            return assemblyName;
        }

        /// <summary>
        /// Return a string that has AssemblyName special characters escaped.
        /// Those characters are Equals(=), Comma(,), Quote("), Apostrophe('), Backslash(\).
        /// </summary>
        /// <remarks>
        /// WARNING! This method is not meant as a general purpose escaping method for assembly names.
        /// Use only if you really know that this does what you need.
        /// </remarks>
        /// <param name="displayName"></param>
        /// <returns></returns>
        internal static string EscapeDisplayNameCharacters(string displayName)
        {
            StringBuilder sb = new StringBuilder(displayName);
            sb = sb.Replace("\\", "\\\\");
            sb = sb.Replace("=", "\\=");
            sb = sb.Replace(",", "\\,");
            sb = sb.Replace("\"", "\\\"");
            sb = sb.Replace("'", "\\'");
            return sb.ToString();
        }

        /// <summary>
        /// Convert to a string for display.
        /// </summary>
        /// <returns></returns>
        public override string ToString()
        {
            CreateFullName();
            return asString;
        }

        /// <summary>
        /// Compare the fields of this with that if they are not null.
        /// </summary>
        internal bool PartialNameCompare(AssemblyNameExtension that)
        {
            return PartialNameCompare(that, PartialComparisonFlags.Default, false /* do not consider retargetable flag*/);
        }

        /// <summary>
        /// Compare the fields of this with that if they are not null.
        /// </summary>
        internal bool PartialNameCompare(AssemblyNameExtension that, bool considerRetargetableFlag)
        {
            return PartialNameCompare(that, PartialComparisonFlags.Default, considerRetargetableFlag);
        }

        /// <summary>
        /// Do a partial comparison between two assembly name extensions.
        /// Compare the fields of A and B on the following conditions:
        /// 1) A.Field has a non null value
        /// 2) The field has been selected in the comparison flags or the default comparison flags are passed in.
        ///
        /// If A.Field is null then we will not compare A.Field and B.Field even when the comparison flag is set for that field unless skipNullFields is false.
        /// </summary>
        internal bool PartialNameCompare(AssemblyNameExtension that, PartialComparisonFlags comparisonFlags)
        {
            return PartialNameCompare(that, comparisonFlags, false /* do not consider retargetable flag*/);
        }

        /// <summary>
        /// Do a partial comparison between two assembly name extensions.
        /// Compare the fields of A and B on the following conditions:
        /// 1) A.Field has a non null value
        /// 2) The field has been selected in the comparison flags or the default comparison flags are passed in.
        ///
        /// If A.Field is null then we will not compare A.Field and B.Field even when the comparison flag is set for that field unless skipNullFields is false.
        /// </summary>
        internal bool PartialNameCompare(AssemblyNameExtension that, PartialComparisonFlags comparisonFlags, bool considerRetargetableFlag)
        {
            // Pointer compare.
            if (object.ReferenceEquals(this, that))
            {
                return true;
            }

            // If that is null then this and that are not equal. Also, this would cause a crash on the next line.
            if (that is null)
            {
                return false;
            }

            // Do both have assembly names?
            if (asAssemblyName != null && that.asAssemblyName != null)
            {
                // Pointer compare.
                if (object.ReferenceEquals(asAssemblyName, that.asAssemblyName))
                {
                    return true;
                }
            }

            // Do the names match?
            if ((comparisonFlags & PartialComparisonFlags.SimpleName) != 0 && Name != null && !string.Equals(Name, that.Name, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if ((comparisonFlags & PartialComparisonFlags.Version) != 0 && Version != null && this.Version != that.Version)
            {
                return false;
            }

            if ((comparisonFlags & PartialComparisonFlags.Culture) != 0 && CultureInfo != null && (that.CultureInfo == null || !CompareCultures(AssemblyName, that.AssemblyName)))
            {
                return false;
            }

            if ((comparisonFlags & PartialComparisonFlags.PublicKeyToken) != 0 && GetPublicKeyToken() != null && !ComparePublicKeyToken(that))
            {
                return false;
            }

            if (considerRetargetableFlag && (Retargetable != that.Retargetable))
            {
                return false;
            }
            return true;
        }

        public void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            info.AddValue("hasAN", asAssemblyName != null);
            if (asAssemblyName != null)
            {
                info.AddValue("name", asAssemblyName.Name);
                info.AddValue("pk", asAssemblyName.GetPublicKey());
                info.AddValue("pkt", asAssemblyName.GetPublicKeyToken());
                info.AddValue("ver", asAssemblyName.Version);
                info.AddValue("flags", (int)asAssemblyName.Flags);
                info.AddValue("cpuarch", (int)asAssemblyName.ProcessorArchitecture);

                info.AddValue("hasCI", asAssemblyName.CultureInfo != null);
                if (asAssemblyName.CultureInfo != null)
                {
                    info.AddValue("ci", asAssemblyName.CultureInfo.LCID);
                }

                info.AddValue("hashAlg", asAssemblyName.HashAlgorithm);
                info.AddValue("verCompat", asAssemblyName.VersionCompatibility);
                info.AddValue("codebase", asAssemblyName.CodeBase);
            }

            info.AddValue("asStr", asString);
            info.AddValue("isSName", isSimpleName);
            info.AddValue("hasCpuArch", hasProcessorArchitectureInFusionName);
            info.AddValue("immutable", immutable);
            info.AddValue("remapped", remappedFrom);
        }

        /// <summary>
        /// Reads/writes this class
        /// </summary>
        /// <param name="translator"></param>
        public void Translate(ITranslator translator)
        {
            translator.Translate(ref asAssemblyName);
            translator.Translate(ref asString);
            translator.Translate(ref isSimpleName);
            translator.Translate(ref hasProcessorArchitectureInFusionName);
            translator.Translate(ref immutable);

            // TODO: consider some kind of protection against infinite loop during serialization, hint: pre serialize check for cycle in graph
            translator.TranslateHashSet(ref remappedFrom,
                (ITranslator t) => new AssemblyNameExtension(t),
                (int capacity) => CreateRemappedFrom());
        }
    }
}
