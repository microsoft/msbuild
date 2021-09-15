// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
//
// mansign.cs
//

using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.Xml;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Xml;
using System.Runtime.InteropServices;

#if RUNTIME_TYPE_NETCORE
using System.Runtime.Versioning;
#endif

using _FILETIME = System.Runtime.InteropServices.ComTypes.FILETIME;

namespace System.Deployment.Internal.CodeSigning
{
    internal static class Win32
    {
        //
        // PInvoke dll's.
        //
        internal const String CRYPT32 = "crypt32.dll";
        internal const String KERNEL32 = "kernel32.dll";
#if (true)

#if FEATURE_MAIN_CLR_MODULE_USES_CORE_NAME
        internal const String MSCORWKS = "coreclr.dll";
#elif USE_OLD_MSCORWKS_NAME // for updating devdiv toolset until it has clr.dll
        internal const String MSCORWKS = "mscorwks.dll";
#else //FEATURE_MAIN_CLR_MODULE_USES_CORE_NAME
        internal const String MSCORWKS = "clr.dll";
#endif //FEATURE_MAIN_CLR_MODULE_USES_CORE_NAME

#else
        internal const String MSCORWKS = "isowhidbey.dll";
#endif
        //
        // Constants.
        //
        internal const int S_OK = unchecked((int)0x00000000);
        internal const int NTE_BAD_KEY = unchecked((int)0x80090003);

        // Trust errors.
        internal const int TRUST_E_SYSTEM_ERROR = unchecked((int)0x80096001);
        internal const int TRUST_E_NO_SIGNER_CERT = unchecked((int)0x80096002);
        internal const int TRUST_E_COUNTER_SIGNER = unchecked((int)0x80096003);
        internal const int TRUST_E_CERT_SIGNATURE = unchecked((int)0x80096004);
        internal const int TRUST_E_TIME_STAMP = unchecked((int)0x80096005);
        internal const int TRUST_E_BAD_DIGEST = unchecked((int)0x80096010);
        internal const int TRUST_E_BASIC_CONSTRAINTS = unchecked((int)0x80096019);
        internal const int TRUST_E_FINANCIAL_CRITERIA = unchecked((int)0x8009601E);
        internal const int TRUST_E_PROVIDER_UNKNOWN = unchecked((int)0x800B0001);
        internal const int TRUST_E_ACTION_UNKNOWN = unchecked((int)0x800B0002);
        internal const int TRUST_E_SUBJECT_FORM_UNKNOWN = unchecked((int)0x800B0003);
        internal const int TRUST_E_SUBJECT_NOT_TRUSTED = unchecked((int)0x800B0004);
        internal const int TRUST_E_NOSIGNATURE = unchecked((int)0x800B0100);
        internal const int CERT_E_UNTRUSTEDROOT = unchecked((int)0x800B0109);
        internal const int TRUST_E_FAIL = unchecked((int)0x800B010B);
        internal const int TRUST_E_EXPLICIT_DISTRUST = unchecked((int)0x800B0111);
        internal const int CERT_E_CHAINING = unchecked((int)0x800B010A);

        // Values for dwFlags of CertVerifyAuthenticodeLicense.
        internal const int AXL_REVOCATION_NO_CHECK = unchecked((int)0x00000001);
        internal const int AXL_REVOCATION_CHECK_END_CERT_ONLY = unchecked((int)0x00000002);
        internal const int AXL_REVOCATION_CHECK_ENTIRE_CHAIN = unchecked((int)0x00000004);
        internal const int AXL_URL_CACHE_ONLY_RETRIEVAL = unchecked((int)0x00000008);
        internal const int AXL_LIFETIME_SIGNING = unchecked((int)0x00000010);
        internal const int AXL_TRUST_MICROSOFT_ROOT_ONLY = unchecked((int)0x00000020);

        // Wintrust Policy Flag
        //  These are set during install and can be modified by the user
        //  through various means.  The SETREG.EXE utility (found in the Authenticode
        //  Tools Pack) will select/deselect each of them.
        internal const int WTPF_IGNOREREVOKATION = (int)0x00000200;  // Do revocation check

        // The default WinVerifyTrust Authenticode policy is to treat all time stamped
        // signatures as being valid forever. This OID limits the valid lifetime of the
        // signature to the lifetime of the certificate. This allows timestamped
        // signatures to expire. Normally this OID will be used in conjunction with
        // szOID_PKIX_KP_CODE_SIGNING to indicate new time stamp semantics should be
        // used. Support for this OID was added in WXP.
        internal const string szOID_KP_LIFETIME_SIGNING = "1.3.6.1.4.1.311.10.3.13";
        internal const string szOID_RSA_signingTime = "1.2.840.113549.1.9.5";

        //
        // Structures.
        //
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct CRYPT_DATA_BLOB
        {
            internal uint cbData;
            internal IntPtr pbData;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct AXL_SIGNER_INFO
        {
            internal uint cbSize;             // sizeof(AXL_SIGNER_INFO).
            internal uint dwError;            // Error code.
            internal uint algHash;            // Hash algorithm (ALG_ID).
            internal IntPtr pwszHash;           // Hash.
            internal IntPtr pwszDescription;    // Description.
            internal IntPtr pwszDescriptionUrl; // Description URL.
            internal IntPtr pChainContext;      // Signer's chain context.
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct AXL_TIMESTAMPER_INFO
        {
            internal uint cbSize;             // sizeof(AXL_TIMESTAMPER_INFO).
            internal uint dwError;            // Error code.
            internal uint algHash;            // Hash algorithm (ALG_ID).
            internal _FILETIME ftTimestamp;        // Timestamp time.
            internal IntPtr pChainContext;      // Timestamper's chain context.
        }

        //
        // DllImport declarations.
        //
        [DllImport(KERNEL32, CharSet = CharSet.Auto, SetLastError = true)]
        internal extern static
        IntPtr GetProcessHeap();

        [DllImport(KERNEL32, CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal extern static
        bool HeapFree(
            [In]    IntPtr hHeap,
            [In]    uint dwFlags,
            [In]    IntPtr lpMem);

        [DllImport(MSCORWKS, CharSet = CharSet.Auto, SetLastError = true)]
        internal extern static
        int CertTimestampAuthenticodeLicense(
            [In]      ref CRYPT_DATA_BLOB pSignedLicenseBlob,
            [In]      string pwszTimestampURI,
            [In, Out]  ref CRYPT_DATA_BLOB pTimestampSignatureBlob);

        [DllImport(MSCORWKS, CharSet = CharSet.Auto, SetLastError = true)]
        internal extern static
        int CertVerifyAuthenticodeLicense(
            [In]      ref CRYPT_DATA_BLOB pLicenseBlob,
            [In]      uint dwFlags,
            [In, Out]  ref AXL_SIGNER_INFO pSignerInfo,
            [In, Out]  ref AXL_TIMESTAMPER_INFO pTimestamperInfo);

        [DllImport(MSCORWKS, CharSet = CharSet.Auto, SetLastError = true)]
        internal extern static
        int CertFreeAuthenticodeSignerInfo(
            [In]      ref AXL_SIGNER_INFO pSignerInfo);

        [DllImport(MSCORWKS, CharSet = CharSet.Auto, SetLastError = true)]
        internal extern static
        int CertFreeAuthenticodeTimestamperInfo(
            [In]      ref AXL_TIMESTAMPER_INFO pTimestamperInfo);

        [DllImport(MSCORWKS, CharSet = CharSet.Auto, SetLastError = true)]
        internal extern static
        int _AxlGetIssuerPublicKeyHash(
            [In]     IntPtr pCertContext,
            [In, Out] ref IntPtr ppwszPublicKeyHash);

        [DllImport(MSCORWKS, CharSet = CharSet.Auto, SetLastError = true)]
        internal extern static
        int _AxlRSAKeyValueToPublicKeyToken(
            [In]     ref CRYPT_DATA_BLOB pModulusBlob,
            [In]     ref CRYPT_DATA_BLOB pExponentBlob,
            [In, Out] ref IntPtr ppwszPublicKeyToken);

        [DllImport(MSCORWKS, CharSet = CharSet.Auto, SetLastError = true)]
        internal extern static
        int _AxlPublicKeyBlobToPublicKeyToken(
            [In]     ref CRYPT_DATA_BLOB pCspPublicKeyBlob,
            [In, Out] ref IntPtr ppwszPublicKeyToken);

        // RFC3161 timestamp support

        // hash algorithm OIDs
        internal const string szOID_OIWSEC_sha1 = "1.3.14.3.2.26";
        internal const string szOID_NIST_sha256 = "2.16.840.1.101.3.4.2.1";

        [StructLayout(LayoutKind.Sequential)]
        internal struct CRYPT_TIMESTAMP_CONTEXT
        {
            internal uint cbEncoded;      // DWORD->unsigned int
            internal IntPtr pbEncoded;      // BYTE*
            internal IntPtr pTimeStamp;     // PCRYPT_TIMESTAMP_INFO->_CRYPT_TIMESTAMP_INFO*
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct CRYPTOAPI_BLOB
        {
            internal uint cbData;
            internal IntPtr pbData;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct CRYPT_TIMESTAMP_PARA
        {
            internal IntPtr pszTSAPolicyId;
            internal bool fRequestCerts;
            internal CRYPTOAPI_BLOB Nonce;
            internal int cExtension;
            internal IntPtr rgExtension;
        }

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport(CRYPT32, CallingConvention = CallingConvention.Winapi, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal extern static
        bool CryptRetrieveTimeStamp(
            [In]     [MarshalAs(UnmanagedType.LPWStr)]  string wszUrl,
            [In]     uint dwRetrievalFlags,
            [In]     int dwTimeout,
            [In]     [MarshalAs(UnmanagedType.LPStr)]   string pszHashId,
            [In, Out] ref CRYPT_TIMESTAMP_PARA pPara,
            [In]     byte[] pbData,
            [In]     int cbData,
            [In, Out] ref IntPtr ppTsContext,
            [In, Out] ref IntPtr ppTsSigner,
            [In, Out] ref IntPtr phStore);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport(CRYPT32, CallingConvention = CallingConvention.Winapi, SetLastError = true)]
        internal static extern bool CertFreeCertificateContext(IntPtr pCertContext);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport(CRYPT32, CallingConvention = CallingConvention.Winapi, SetLastError = true)]
        internal static extern bool CertCloseStore(IntPtr pCertContext, int dwFlags);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport(CRYPT32, CallingConvention = CallingConvention.Winapi)]
        internal static extern void CryptMemFree(IntPtr pv);
    }

    internal class ManifestSignedXml2 : SignedXml
    {
        private bool _verify = false;
        private const string Sha256SignatureMethodUri = @"http://www.w3.org/2000/09/xmldsig#rsa-sha256";
        private const string Sha256DigestMethod = @"http://www.w3.org/2000/09/xmldsig#sha256";

        internal ManifestSignedXml2()
            : base()
        {
            init();
        }
        internal ManifestSignedXml2(XmlElement elem)
            : base(elem)
        {
            init();
        }
        internal ManifestSignedXml2(XmlDocument document)
            : base(document)
        {
            init();
        }

        internal ManifestSignedXml2(XmlDocument document, bool verify)
            : base(document)
        {
            _verify = verify;
            init();
        }

        private void init()
        {
            CryptoConfig.AddAlgorithm(typeof(RSAPKCS1SHA256SignatureDescription),
                               Sha256SignatureMethodUri);

#if RUNTIME_TYPE_NETCORE
            CryptoConfig.AddAlgorithm(typeof(SHA256Managed),
                               Sha256DigestMethod);
#else
            CryptoConfig.AddAlgorithm(typeof(System.Security.Cryptography.SHA256Cng),
                               Sha256DigestMethod);
#endif
        }

        private static XmlElement FindIdElement(XmlElement context, string idValue)
        {
            if (context == null)
                return null;

            XmlElement idReference = context.SelectSingleNode("//*[@Id=\"" + idValue + "\"]") as XmlElement;
            if (idReference != null)
                return idReference;
            idReference = context.SelectSingleNode("//*[@id=\"" + idValue + "\"]") as XmlElement;
            if (idReference != null)
                return idReference;
            return context.SelectSingleNode("//*[@ID=\"" + idValue + "\"]") as XmlElement;
        }

        public override XmlElement GetIdElement(XmlDocument document, string idValue)
        {
            // We only care about Id references inside of the KeyInfo section
            if (_verify)
                return base.GetIdElement(document, idValue);

            KeyInfo keyInfo = this.KeyInfo;
            if (keyInfo.Id != idValue)
                return null;
            return keyInfo.GetXml();
        }
    }

    internal class SignedCmiManifest2
    {
        private XmlDocument _manifestDom = null;
        private CmiStrongNameSignerInfo _strongNameSignerInfo = null;
        private CmiAuthenticodeSignerInfo _authenticodeSignerInfo = null;
        private bool _useSha256;

        private const string Sha256SignatureMethodUri = @"http://www.w3.org/2000/09/xmldsig#rsa-sha256";
        private const string Sha256DigestMethod = @"http://www.w3.org/2000/09/xmldsig#sha256";
        private const string Sha1SignatureMethodUri = @"http://www.w3.org/2000/09/xmldsig#rsa-sha1";
        private const string Sha1DigestMethod = @"http://www.w3.org/2000/09/xmldsig#sha1";

        private const string wintrustPolicyFlagsRegPath = "Software\\Microsoft\\Windows\\CurrentVersion\\WinTrust\\Trust Providers\\Software Publishing";
        private const string wintrustPolicyFlagsRegName = "State";

        private SignedCmiManifest2() { }

        internal SignedCmiManifest2(XmlDocument manifestDom, bool useSha256)
        {
            _manifestDom = manifestDom ?? throw new ArgumentNullException(nameof(manifestDom));
            _useSha256 = useSha256;
        }
#if RUNTIME_TYPE_NETCORE
        [SupportedOSPlatform("windows")]
#endif
        internal void Sign(CmiManifestSigner2 signer)
        {
            Sign(signer, null);
        }

#if RUNTIME_TYPE_NETCORE
        [SupportedOSPlatform("windows")]
#endif
        internal void Sign(CmiManifestSigner2 signer, string timeStampUrl)
        {
            // Reset signer infos.
            _strongNameSignerInfo = null;
            _authenticodeSignerInfo = null;

            // Signer cannot be null.
            if (signer == null || signer.StrongNameKey == null)
            {
                throw new ArgumentNullException(nameof(signer));
            }

            // Remove existing SN signature.
            RemoveExistingSignature(_manifestDom);

            // Replace public key token in assemblyIdentity if requested.
            if ((signer.Flag & CmiManifestSignerFlag.DontReplacePublicKeyToken) == 0)
            {
                ReplacePublicKeyToken(_manifestDom, signer.StrongNameKey, _useSha256);
            }

            // No cert means don't Authenticode sign and timestamp.
            XmlDocument licenseDom = null;
            if (signer.Certificate != null)
            {
                // Yes. We will Authenticode sign, so first insert <publisherIdentity />
                // element, if necessary.
                InsertPublisherIdentity(_manifestDom, signer.Certificate);

                // Now create the license DOM, and then sign it.
                licenseDom = CreateLicenseDom(signer, ExtractPrincipalFromManifest(), ComputeHashFromManifest(_manifestDom, _useSha256));
                AuthenticodeSignLicenseDom(licenseDom, signer, timeStampUrl, _useSha256);
            }
            StrongNameSignManifestDom(_manifestDom, licenseDom, signer, _useSha256);
        }

        internal CmiStrongNameSignerInfo StrongNameSignerInfo
        {
            get
            {
                return _strongNameSignerInfo;
            }
        }

        internal CmiAuthenticodeSignerInfo AuthenticodeSignerInfo
        {
            get
            {
                return _authenticodeSignerInfo;
            }
        }

        //
        // Privates.
        //
        private XmlElement ExtractPrincipalFromManifest()
        {
            XmlNamespaceManager nsm = new XmlNamespaceManager(_manifestDom.NameTable);
            nsm.AddNamespace("asm", AssemblyNamespaceUri);
            XmlNode assemblyIdentityNode = _manifestDom.SelectSingleNode("asm:assembly/asm:assemblyIdentity", nsm);
            if (assemblyIdentityNode == null)
                throw new CryptographicException(Win32.TRUST_E_SUBJECT_FORM_UNKNOWN);
            return assemblyIdentityNode as XmlElement;
        }

        //
        // Statics.
        //
        private static void InsertPublisherIdentity(XmlDocument manifestDom, X509Certificate2 signerCert)
        {
            XmlNamespaceManager nsm = new XmlNamespaceManager(manifestDom.NameTable);
            nsm.AddNamespace("asm", AssemblyNamespaceUri);
            nsm.AddNamespace("asm2", AssemblyV2NamespaceUri);
            nsm.AddNamespace("ds", SignedXml.XmlDsigNamespaceUrl);

            XmlElement assembly = manifestDom.SelectSingleNode("asm:assembly", nsm) as XmlElement;
            XmlElement assemblyIdentity = manifestDom.SelectSingleNode("asm:assembly/asm:assemblyIdentity", nsm) as XmlElement;
            if (assemblyIdentity == null)
            {
                throw new CryptographicException(Win32.TRUST_E_SUBJECT_FORM_UNKNOWN);
            }

            // Reuse existing node if exists
            XmlElement publisherIdentity = manifestDom.SelectSingleNode("asm:assembly/asm2:publisherIdentity", nsm) as XmlElement;
            if (publisherIdentity == null)
            {
                // create new if not exist
                publisherIdentity = manifestDom.CreateElement("publisherIdentity", AssemblyV2NamespaceUri);
            }
            // Get the issuer's public key blob hash.
            IntPtr pIssuerKeyHash = new IntPtr();
            int hr = Win32._AxlGetIssuerPublicKeyHash(signerCert.Handle, ref pIssuerKeyHash);
            if (hr != Win32.S_OK)
            {
                throw new CryptographicException(hr);
            }

            string issuerKeyHash = Marshal.PtrToStringUni(pIssuerKeyHash);
            Win32.HeapFree(Win32.GetProcessHeap(), 0, pIssuerKeyHash);

            publisherIdentity.SetAttribute("name", signerCert.SubjectName.Name);
            publisherIdentity.SetAttribute("issuerKeyHash", issuerKeyHash);

            XmlElement signature = manifestDom.SelectSingleNode("asm:assembly/ds:Signature", nsm) as XmlElement;
            if (signature != null)
            {
                assembly.InsertBefore(publisherIdentity, signature);
            }
            else
            {
                assembly.AppendChild(publisherIdentity);
            }
        }

        private static void RemoveExistingSignature(XmlDocument manifestDom)
        {
            XmlNamespaceManager nsm = new XmlNamespaceManager(manifestDom.NameTable);
            nsm.AddNamespace("asm", AssemblyNamespaceUri);
            nsm.AddNamespace("ds", SignedXml.XmlDsigNamespaceUrl);
            XmlNode signatureNode = manifestDom.SelectSingleNode("asm:assembly/ds:Signature", nsm);
            if (signatureNode != null)
                signatureNode.ParentNode.RemoveChild(signatureNode);
        }

        /// <summary>
        /// The reason you need provider type 24, is because that’s the only RSA provider type that supports SHA-2 operations.   (For instance, PROV_RSA_FULL does not support SHA-2).
        /// As for official guidance – I’m not sure of any.    For workarounds though, if you’re using the Microsoft software CSPs, they share the underlying key store.  You can get the key container name from your RSA object, then open up a new RSA object with the same key container name but with PROV_RSA_AES.   At that point, you should be able to use SHA-2 algorithms.
        /// </summary>
        /// <param name="oldCsp"></param>
        /// <param name="useSha256">Whether to use sha256</param>
        /// <returns></returns>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Cryptographic.Standard", "CA5358:RSAProviderNeeds2048bitKey", Justification = "SHA1 is retained for compatibility reasons as an option in VisualStudio signing page and consequently in the trust manager, default is SHA2.")]
#if RUNTIME_TYPE_NETCORE
        [SupportedOSPlatform("windows")]
#endif
        internal static RSACryptoServiceProvider GetFixedRSACryptoServiceProvider(RSACryptoServiceProvider oldCsp, bool useSha256)
        {
            if (!useSha256)
            {
                return oldCsp;
            }

            // 3rd party crypto providers in general don't need to be forcefully upgraded.
            // This not an ideal way to check for that but is the best we have available.
            if (!oldCsp.CspKeyContainerInfo.ProviderName.StartsWith("Microsoft", StringComparison.Ordinal))
            {
                return oldCsp;
            }

            const int PROV_RSA_AES = 24;    // CryptoApi provider type for an RSA provider supporting sha-256 digital signatures
            CspParameters csp = new CspParameters();
            csp.ProviderType = PROV_RSA_AES;
            csp.KeyContainerName = oldCsp.CspKeyContainerInfo.KeyContainerName;
            csp.KeyNumber = (int)oldCsp.CspKeyContainerInfo.KeyNumber;
            if (oldCsp.CspKeyContainerInfo.MachineKeyStore)
            {
                csp.Flags = CspProviderFlags.UseMachineKeyStore;
            }
            RSACryptoServiceProvider fixedRsa = new RSACryptoServiceProvider(csp);

            return fixedRsa;
        }

#if RUNTIME_TYPE_NETCORE
        [SupportedOSPlatform("windows")]
#endif
        private static void ReplacePublicKeyToken(XmlDocument manifestDom, AsymmetricAlgorithm snKey, bool useSha256)
        {
            // Make sure we can find the publicKeyToken attribute.
            XmlNamespaceManager nsm = new XmlNamespaceManager(manifestDom.NameTable);
            nsm.AddNamespace("asm", AssemblyNamespaceUri);
            XmlElement assemblyIdentity = manifestDom.SelectSingleNode("asm:assembly/asm:assemblyIdentity", nsm) as XmlElement;
            if (assemblyIdentity == null)
            {
                throw new CryptographicException(Win32.TRUST_E_SUBJECT_FORM_UNKNOWN);
            }

            if (!assemblyIdentity.HasAttribute("publicKeyToken"))
            {
                throw new CryptographicException(Win32.TRUST_E_SUBJECT_FORM_UNKNOWN);
            }

            byte[] cspPublicKeyBlob;

            if(snKey is RSACryptoServiceProvider){
                cspPublicKeyBlob = (GetFixedRSACryptoServiceProvider((RSACryptoServiceProvider)snKey, useSha256)).ExportCspBlob(false);
                if (cspPublicKeyBlob == null || cspPublicKeyBlob.Length == 0)
                {
                    throw new CryptographicException(Win32.NTE_BAD_KEY);
                }
            }
            else
            {
                using (RSACryptoServiceProvider rsaCsp = new RSACryptoServiceProvider())
                {
                    rsaCsp.ImportParameters(((RSA)snKey).ExportParameters(false));
                    cspPublicKeyBlob = rsaCsp.ExportCspBlob(false);
                }
            }
            // Now compute the public key token.
            unsafe
            {
                fixed (byte* pbPublicKeyBlob = cspPublicKeyBlob)
                {
                    Win32.CRYPT_DATA_BLOB publicKeyBlob = new Win32.CRYPT_DATA_BLOB();
                    publicKeyBlob.cbData = (uint)cspPublicKeyBlob.Length;
                    publicKeyBlob.pbData = new IntPtr(pbPublicKeyBlob);
                    IntPtr pPublicKeyToken = new IntPtr();

                    int hr = Win32._AxlPublicKeyBlobToPublicKeyToken(ref publicKeyBlob, ref pPublicKeyToken);
                    if (hr != Win32.S_OK)
                    {
                        throw new CryptographicException(hr);
                    }

                    string publicKeyToken = Marshal.PtrToStringUni(pPublicKeyToken);
                    Win32.HeapFree(Win32.GetProcessHeap(), 0, pPublicKeyToken);

                    assemblyIdentity.SetAttribute("publicKeyToken", publicKeyToken);
                }
            }
        }

        private static byte[] ComputeHashFromManifest(XmlDocument manifestDom, bool useSha256)
        {
#if (true) // BUGBUG: Remove before RTM when old format support is no longer needed.
            return ComputeHashFromManifest(manifestDom, false, useSha256);
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Cryptographic.Standard", "CA5354:SHA1CannotBeUsed", Justification = "SHA1 is retained for compatibility reasons as an option in VisualStudio signing page and consequently in the trust manager, default is SHA2.")]
        private static byte[] ComputeHashFromManifest(XmlDocument manifestDom, bool oldFormat, bool useSha256)
        {
            if (oldFormat)
            {
                XmlDsigExcC14NTransform exc = new XmlDsigExcC14NTransform();
                exc.LoadInput(manifestDom);

                if (useSha256)
                {
                    using (SHA256CryptoServiceProvider sha2 = new SHA256CryptoServiceProvider())
                    {
                        byte[] hash = sha2.ComputeHash(exc.GetOutput() as MemoryStream);
                        if (hash == null)
                        {
                            throw new CryptographicException(Win32.TRUST_E_BAD_DIGEST);
                        }

                        return hash;
                    }
                }
                else
                {
                    using (SHA1CryptoServiceProvider sha1 = new SHA1CryptoServiceProvider())
                    {
                        byte[] hash = sha1.ComputeHash(exc.GetOutput() as MemoryStream);
                        if (hash == null)
                        {
                            throw new CryptographicException(Win32.TRUST_E_BAD_DIGEST);
                        }

                        return hash;
                    }
                }
            }
            else
            {
#endif
                // Since the DOM given to us is not guaranteed to be normalized,
                // we need to normalize it ourselves. Also, we always preserve
                // white space as Fusion XML engine always preserve white space.
                XmlDocument normalizedDom = new XmlDocument();
                normalizedDom.PreserveWhitespace = true;

                // Normalize the document
                using (TextReader stringReader = new StringReader(manifestDom.OuterXml))
                {
                    XmlReaderSettings settings = new XmlReaderSettings();
                    settings.DtdProcessing = DtdProcessing.Parse;
                    XmlReader reader = XmlReader.Create(stringReader, settings, manifestDom.BaseURI);
                    normalizedDom.Load(reader);
                }

                XmlDsigExcC14NTransform exc = new XmlDsigExcC14NTransform();
                exc.LoadInput(normalizedDom);

                if (useSha256)
                {
                    using (SHA256CryptoServiceProvider sha2 = new SHA256CryptoServiceProvider())
                    {
                        byte[] hash = sha2.ComputeHash(exc.GetOutput() as MemoryStream);
                        if (hash == null)
                        {
                            throw new CryptographicException(Win32.TRUST_E_BAD_DIGEST);
                        }

                        return hash;
                    }
                }
                else
                {
                    using (SHA1CryptoServiceProvider sha1 = new SHA1CryptoServiceProvider())
                    {
                        byte[] hash = sha1.ComputeHash(exc.GetOutput() as MemoryStream);
                        if (hash == null)
                        {
                            throw new CryptographicException(Win32.TRUST_E_BAD_DIGEST);
                        }

                        return hash;
                    }
                }

#if (true) // BUGBUG: Remove before RTM when old format support is no longer needed.
            }
#endif
        }

        private const string AssemblyNamespaceUri = "urn:schemas-microsoft-com:asm.v1";
        private const string AssemblyV2NamespaceUri = "urn:schemas-microsoft-com:asm.v2";
        private const string MSRelNamespaceUri = "http://schemas.microsoft.com/windows/rel/2005/reldata";
        private const string LicenseNamespaceUri = "urn:mpeg:mpeg21:2003:01-REL-R-NS";
        private const string AuthenticodeNamespaceUri = "http://schemas.microsoft.com/windows/pki/2005/Authenticode";
        private const string licenseTemplate = "<r:license xmlns:r=\"" + LicenseNamespaceUri + "\" xmlns:as=\"" + AuthenticodeNamespaceUri + "\">" +
                                                    @"<r:grant>" +
                                                    @"<as:ManifestInformation>" +
                                                    @"<as:assemblyIdentity />" +
                                                    @"</as:ManifestInformation>" +
                                                    @"<as:SignedBy/>" +
                                                    @"<as:AuthenticodePublisher>" +
                                                    @"<as:X509SubjectName>CN=dummy</as:X509SubjectName>" +
                                                    @"</as:AuthenticodePublisher>" +
                                                    @"</r:grant><r:issuer></r:issuer></r:license>";

        [SuppressMessage("Microsoft.Security.Xml", "CA3057: DoNotUseLoadXml.", Justification = "Suppressed since the xml being loaded is a constant defined in this file.")]
        private static XmlDocument CreateLicenseDom(CmiManifestSigner2 signer, XmlElement principal, byte[] hash)
        {
            XmlDocument licenseDom = new XmlDocument();
            licenseDom.PreserveWhitespace = true;
            // CA3057: DoNotUseLoadXml. Suppressed since the xml being loaded is a constant defined in this file.
            licenseDom.LoadXml(licenseTemplate);
            XmlNamespaceManager nsm = new XmlNamespaceManager(licenseDom.NameTable);
            nsm.AddNamespace("r", LicenseNamespaceUri);
            nsm.AddNamespace("as", AuthenticodeNamespaceUri);
            XmlElement assemblyIdentityNode = licenseDom.SelectSingleNode("r:license/r:grant/as:ManifestInformation/as:assemblyIdentity", nsm) as XmlElement;
            assemblyIdentityNode.RemoveAllAttributes();
            foreach (XmlAttribute attribute in principal.Attributes)
            {
                assemblyIdentityNode.SetAttribute(attribute.Name, attribute.Value);
            }

            XmlElement manifestInformationNode = licenseDom.SelectSingleNode("r:license/r:grant/as:ManifestInformation", nsm) as XmlElement;

            manifestInformationNode.SetAttribute("Hash", hash.Length == 0 ? "" : BytesToHexString(hash, 0, hash.Length));
            manifestInformationNode.SetAttribute("Description", signer.Description ?? "");
            manifestInformationNode.SetAttribute("Url", signer.DescriptionUrl ?? "");

            XmlElement authenticodePublisherNode = licenseDom.SelectSingleNode("r:license/r:grant/as:AuthenticodePublisher/as:X509SubjectName", nsm) as XmlElement;
            authenticodePublisherNode.InnerText = signer.Certificate.SubjectName.Name;

            return licenseDom;
        }

#if RUNTIME_TYPE_NETCORE
        [SupportedOSPlatform("windows")]
#endif
        private static void AuthenticodeSignLicenseDom(XmlDocument licenseDom, CmiManifestSigner2 signer, string timeStampUrl, bool useSha256)
        {
            // Make sure it is RSA, as this is the only one Fusion will support.
#if RUNTIME_TYPE_NETCORE
            RSA rsaPrivateKey = signer.Certificate.GetRSAPrivateKey();
#else
            RSA rsaPrivateKey = CngLightup.GetRSAPrivateKey(signer.Certificate);
#endif
            if (rsaPrivateKey == null)
            {
                throw new NotSupportedException();
            }

            // Setup up XMLDSIG engine.
            ManifestSignedXml2 signedXml = new ManifestSignedXml2(licenseDom);
            // only needs to change the provider type when RSACryptoServiceProvider is used
            var rsaCsp = rsaPrivateKey is RSACryptoServiceProvider ?
                            GetFixedRSACryptoServiceProvider(rsaPrivateKey as RSACryptoServiceProvider, useSha256) : rsaPrivateKey;
            signedXml.SigningKey = rsaCsp;
            signedXml.SignedInfo.CanonicalizationMethod = SignedXml.XmlDsigExcC14NTransformUrl;
            if (signer.UseSha256)
            {
                signedXml.SignedInfo.SignatureMethod = Sha256SignatureMethodUri;
            }
            else
            {
                signedXml.SignedInfo.SignatureMethod = Sha1SignatureMethodUri;
            }

            // Add the key information.
            signedXml.KeyInfo.AddClause(new RSAKeyValue(rsaCsp));
            signedXml.KeyInfo.AddClause(new KeyInfoX509Data(signer.Certificate, signer.IncludeOption));

            // Add the enveloped reference.
            Reference reference = new Reference();
            reference.Uri = "";
            if (signer.UseSha256)
            {
                reference.DigestMethod = Sha256DigestMethod;
            }
            else
            {
                reference.DigestMethod = Sha1DigestMethod;
            }

            // Add an enveloped and an Exc-C14N transform.
            reference.AddTransform(new XmlDsigEnvelopedSignatureTransform());
#if (false) // BUGBUG: LTA transform complaining about issuer node not found.
            reference.AddTransform(new XmlLicenseTransform()); 
#endif
            reference.AddTransform(new XmlDsigExcC14NTransform());

            // Add the reference.
            signedXml.AddReference(reference);

            // Compute the signature.
            signedXml.ComputeSignature();

            // Get the XML representation
            XmlElement xmlDigitalSignature = signedXml.GetXml();
            xmlDigitalSignature.SetAttribute("Id", "AuthenticodeSignature");

            // Insert the signature node under the issuer element.
            XmlNamespaceManager nsm = new XmlNamespaceManager(licenseDom.NameTable);
            nsm.AddNamespace("r", LicenseNamespaceUri);
            XmlElement issuerNode = licenseDom.SelectSingleNode("r:license/r:issuer", nsm) as XmlElement;
            issuerNode.AppendChild(licenseDom.ImportNode(xmlDigitalSignature, true));

            // Time stamp it if requested.
            if (!string.IsNullOrEmpty(timeStampUrl))
            {
                TimestampSignedLicenseDom(licenseDom, timeStampUrl, useSha256);
            }

            // Wrap it inside a RelData element.
            licenseDom.DocumentElement.ParentNode.InnerXml = "<msrel:RelData xmlns:msrel=\"" +
                                                             MSRelNamespaceUri + "\">" +
                                                             licenseDom.OuterXml + "</msrel:RelData>";
        }

        //
        // ObtainRFC3161Timestamp
        //
        // This function is from mage.exe in .NET FX and is used to implement RFC 3161 timestamping.
        //
        private static string ObtainRFC3161Timestamp(string timeStampUrl, string signatureValue, bool useSha256)
        {
            byte[] sigValueBytes = Convert.FromBase64String(signatureValue);
            string timestamp = String.Empty;

            string algId = useSha256 ? Win32.szOID_NIST_sha256 : Win32.szOID_OIWSEC_sha1;

            unsafe
            {
                IntPtr ppTsContext = IntPtr.Zero;
                IntPtr ppTsSigner = IntPtr.Zero;
                IntPtr phStore = IntPtr.Zero;

                try
                {
                    byte[] nonce = new byte[24];

                    using (RandomNumberGenerator rng = RandomNumberGenerator.Create())
                    {
                        rng.GetBytes(nonce);
                    }

                    Win32.CRYPT_TIMESTAMP_PARA para = new Win32.CRYPT_TIMESTAMP_PARA()
                    {
                        fRequestCerts = true,
                        pszTSAPolicyId = IntPtr.Zero,
                    };

                    fixed (byte* pbNonce = nonce)
                    {
                        para.Nonce.cbData = (uint)nonce.Length;
                        para.Nonce.pbData = (IntPtr)pbNonce;

                        if (!Win32.CryptRetrieveTimeStamp(
                            timeStampUrl,
                            0,
                            60 * 1000,  // 1 minute timeout
                            algId,
                            ref para,
                            sigValueBytes,
                            sigValueBytes.Length,
                            ref ppTsContext,
                            ref ppTsSigner,
                            ref phStore))
                        {
                            throw new CryptographicException(Marshal.GetLastWin32Error());
                        }
                    }

                    var timestampContext = (Win32.CRYPT_TIMESTAMP_CONTEXT)Marshal.PtrToStructure(ppTsContext, typeof(Win32.CRYPT_TIMESTAMP_CONTEXT));
                    byte[] encodedBytes = new byte[(int)timestampContext.cbEncoded];
                    Marshal.Copy(timestampContext.pbEncoded, encodedBytes, 0, (int)timestampContext.cbEncoded);
                    timestamp = Convert.ToBase64String(encodedBytes);
                }
                finally
                {
                    if (ppTsContext != IntPtr.Zero)
                        Win32.CryptMemFree(ppTsContext);

                    if (ppTsSigner != IntPtr.Zero)
                        Win32.CertFreeCertificateContext(ppTsSigner);

                    if (phStore != IntPtr.Zero)
                        Win32.CertCloseStore(phStore, 0);
                }
            }

            return timestamp;
        }

        private static void TimestampSignedLicenseDom(XmlDocument licenseDom, string timeStampUrl, bool useSha256)
        {
            XmlNamespaceManager nsm = new XmlNamespaceManager(licenseDom.NameTable);
            nsm.AddNamespace("r", LicenseNamespaceUri);
            nsm.AddNamespace("ds", SignedXml.XmlDsigNamespaceUrl);
            nsm.AddNamespace("as", AuthenticodeNamespaceUri);

            string timestamp = String.Empty;

            try
            {
                // Try RFC3161 first
                XmlElement signatureValueNode = licenseDom.SelectSingleNode("r:license/r:issuer/ds:Signature/ds:SignatureValue", nsm) as XmlElement;
                string signatureValue = signatureValueNode.InnerText;
                timestamp = ObtainRFC3161Timestamp(timeStampUrl, signatureValue, useSha256);
            }
            // Catch CryptographicException to ensure fallback to old code (non-RFC3161)
            catch (CryptographicException)
            {
                Win32.CRYPT_DATA_BLOB timestampBlob = new Win32.CRYPT_DATA_BLOB();

                byte[] licenseXml = Encoding.UTF8.GetBytes(licenseDom.OuterXml);

                unsafe
                {
                    fixed (byte* pbLicense = licenseXml)
                    {
                        Win32.CRYPT_DATA_BLOB licenseBlob = new Win32.CRYPT_DATA_BLOB();
                        IntPtr pvLicense = new IntPtr(pbLicense);
                        licenseBlob.cbData = (uint)licenseXml.Length;
                        licenseBlob.pbData = pvLicense;

                        int hr = Win32.CertTimestampAuthenticodeLicense(ref licenseBlob, timeStampUrl, ref timestampBlob);
                        if (hr != Win32.S_OK)
                        {
                            throw new CryptographicException(hr);
                        }
                    }
                }

                byte[] timestampSignature = new byte[timestampBlob.cbData];
                Marshal.Copy(timestampBlob.pbData, timestampSignature, 0, timestampSignature.Length);
                Win32.HeapFree(Win32.GetProcessHeap(), 0, timestampBlob.pbData);
                timestamp = Encoding.UTF8.GetString(timestampSignature);
            }

            XmlElement asTimestamp = licenseDom.CreateElement("as", "Timestamp", AuthenticodeNamespaceUri);
            asTimestamp.InnerText = timestamp;

            XmlElement dsObject = licenseDom.CreateElement("Object", SignedXml.XmlDsigNamespaceUrl);
            dsObject.AppendChild(asTimestamp);

            XmlElement signatureNode = licenseDom.SelectSingleNode("r:license/r:issuer/ds:Signature", nsm) as XmlElement;
            signatureNode.AppendChild(dsObject);
        }

#if RUNTIME_TYPE_NETCORE
        [SupportedOSPlatform("windows")]
#endif
        private static void StrongNameSignManifestDom(XmlDocument manifestDom, XmlDocument licenseDom, CmiManifestSigner2 signer, bool useSha256)
        {
            RSA snKey = signer.StrongNameKey as RSA;

            // Make sure it is RSA, as this is the only one Fusion will support.
            if (snKey == null)
            {
                throw new NotSupportedException();
            }

            // Setup namespace manager.
            XmlNamespaceManager nsm = new XmlNamespaceManager(manifestDom.NameTable);
            nsm.AddNamespace("asm", AssemblyNamespaceUri);

            // Get to root element.
            XmlElement signatureParent = manifestDom.SelectSingleNode("asm:assembly", nsm) as XmlElement;
            if (signatureParent == null)
            {
                throw new CryptographicException(Win32.TRUST_E_SUBJECT_FORM_UNKNOWN);
            }

            if (!(signer.StrongNameKey is RSA))
            {
                throw new NotSupportedException();
            }

            // Setup up XMLDSIG engine.
            ManifestSignedXml2 signedXml = new ManifestSignedXml2(signatureParent);
            if (signer.StrongNameKey is RSACryptoServiceProvider)
            {
                signedXml.SigningKey = GetFixedRSACryptoServiceProvider(signer.StrongNameKey as RSACryptoServiceProvider, useSha256);
            }
            else
            {
                signedXml.SigningKey = signer.StrongNameKey;
            }
            signedXml.SignedInfo.CanonicalizationMethod = SignedXml.XmlDsigExcC14NTransformUrl;
            if (signer.UseSha256)
            {
                signedXml.SignedInfo.SignatureMethod = Sha256SignatureMethodUri;
            }
            else
            {
                signedXml.SignedInfo.SignatureMethod = Sha1SignatureMethodUri;
            }

            // Add the key information.
            signedXml.KeyInfo.AddClause(new RSAKeyValue(snKey));
            if (licenseDom != null)
            {
                signedXml.KeyInfo.AddClause(new KeyInfoNode(licenseDom.DocumentElement));
            }
            signedXml.KeyInfo.Id = "StrongNameKeyInfo";

            // Add the enveloped reference.
            Reference enveloped = new Reference();
            enveloped.Uri = "";
            if (signer.UseSha256)
            {
                enveloped.DigestMethod = Sha256DigestMethod;
            }
            else
            {
                enveloped.DigestMethod = Sha1DigestMethod;
            }

            // Add an enveloped then Exc-C14N transform.
            enveloped.AddTransform(new XmlDsigEnvelopedSignatureTransform());
            enveloped.AddTransform(new XmlDsigExcC14NTransform());
            signedXml.AddReference(enveloped);

#if (false) // DSIE: New format does not sign KeyInfo.
            // Add the key info reference.
            Reference strongNameKeyInfo = new Reference();
            strongNameKeyInfo.Uri = "#StrongNameKeyInfo";
            strongNameKeyInfo.AddTransform(new XmlDsigExcC14NTransform());
            signedXml.AddReference(strongNameKeyInfo);
#endif
            // Compute the signature.
            signedXml.ComputeSignature();

            // Get the XML representation
            XmlElement xmlDigitalSignature = signedXml.GetXml();
            xmlDigitalSignature.SetAttribute("Id", "StrongNameSignature");

            // Insert the signature now.
            signatureParent.AppendChild(xmlDigitalSignature);
        }
        private static readonly char[] s_hexValues = { '0', '1', '2', '3', '4', '5', '6', '7', '8', '9', 'a', 'b', 'c', 'd', 'e', 'f' };

        private static string BytesToHexString(byte[] array, int start, int end)
        {
            string result = null;
            if (array != null)
            {
                char[] hexOrder = new char[(end - start) * 2];
                int i = end;
                int digit, j = 0;
                while (i-- > start)
                {
                    digit = (array[i] & 0xf0) >> 4;
                    hexOrder[j++] = s_hexValues[digit];
                    digit = (array[i] & 0x0f);
                    hexOrder[j++] = s_hexValues[digit];
                }
                result = new String(hexOrder);
            }
            return result;
        }
    }

    [Flags]
    internal enum CmiManifestSignerFlag
    {
        None = 0x00000000,
        DontReplacePublicKeyToken = 0x00000001
    }

    [Flags]
    internal enum CmiManifestVerifyFlags
    {
        None = 0x00000000,
        RevocationNoCheck = 0x00000001,
        RevocationCheckEndCertOnly = 0x00000002,
        RevocationCheckEntireChain = 0x00000004,
        UrlCacheOnlyRetrieval = 0x00000008,
        LifetimeSigning = 0x00000010,
        TrustMicrosoftRootOnly = 0x00000020,
        StrongNameOnly = 0x00010000
    }

    internal class CmiManifestSigner2
    {
        private AsymmetricAlgorithm _strongNameKey;
        private X509Certificate2 _certificate;
        private string _description;
        private string _url;
        private X509Certificate2Collection _certificates;
        private X509IncludeOption _includeOption;
        private CmiManifestSignerFlag _signerFlag;
        private bool _useSha256;

        private CmiManifestSigner2() { }

        internal CmiManifestSigner2(AsymmetricAlgorithm strongNameKey) :
            this(strongNameKey, null, false)
        { }

        internal CmiManifestSigner2(AsymmetricAlgorithm strongNameKey, X509Certificate2 certificate, bool useSha256)
        {
            if (strongNameKey == null)
                throw new ArgumentNullException(nameof(strongNameKey));

#if (true) // BUGBUG: Fusion only supports RSA. Do we throw if not RSA???
            RSA rsa = strongNameKey as RSA;
            if (rsa == null)
                throw new ArgumentNullException(nameof(strongNameKey));
#endif
            _strongNameKey = strongNameKey;
            _certificate = certificate;
            _certificates = new X509Certificate2Collection();
            _includeOption = X509IncludeOption.ExcludeRoot;
            _signerFlag = CmiManifestSignerFlag.None;
            _useSha256 = useSha256;
        }

        internal bool UseSha256
        {
            get
            {
                return _useSha256;
            }
        }

        internal AsymmetricAlgorithm StrongNameKey
        {
            get
            {
                return _strongNameKey;
            }
        }

        internal X509Certificate2 Certificate
        {
            get
            {
                return _certificate;
            }
        }

        internal string Description
        {
            get
            {
                return _description;
            }
            set
            {
                _description = value;
            }
        }

        internal string DescriptionUrl
        {
            get
            {
                return _url;
            }
            set
            {
                _url = value;
            }
        }

        internal X509Certificate2Collection ExtraStore
        {
            get
            {
                return _certificates;
            }
        }

        internal X509IncludeOption IncludeOption
        {
            get
            {
                return _includeOption;
            }
            set
            {
                if (value < X509IncludeOption.None || value > X509IncludeOption.WholeChain)
                    throw new ArgumentException("value");
                if (_includeOption == X509IncludeOption.None)
                    throw new NotSupportedException();
                _includeOption = value;
            }
        }

        internal CmiManifestSignerFlag Flag
        {
            get
            {
                return _signerFlag;
            }
            set
            {
                unchecked
                {
                    if ((value & ((CmiManifestSignerFlag)~CimManifestSignerFlagMask)) != 0)
                        throw new ArgumentException("value");
                }
                _signerFlag = value;
            }
        }

        internal const uint CimManifestSignerFlagMask = (uint)0x00000001;
    }

    internal class CmiStrongNameSignerInfo
    {
        private int _error = 0;
        private string _publicKeyToken = null;
        private AsymmetricAlgorithm _snKey = null;

        internal CmiStrongNameSignerInfo() { }

        internal CmiStrongNameSignerInfo(int errorCode, string publicKeyToken)
        {
            _error = errorCode;
            _publicKeyToken = publicKeyToken;
        }

        internal int ErrorCode
        {
            get
            {
                return _error;
            }

            set
            {
                _error = value;
            }
        }

        internal string PublicKeyToken
        {
            get
            {
                return _publicKeyToken;
            }

            set
            {
                _publicKeyToken = value;
            }
        }

        internal AsymmetricAlgorithm PublicKey
        {
            get
            {
                return _snKey;
            }

            set
            {
                _snKey = value;
            }
        }
    }

    internal class CmiAuthenticodeSignerInfo
    {
        private int _error = 0;
        private X509Chain _signerChain = null;
        private uint _algHash = 0;
        private string _hash = null;
        private string _description = null;
        private string _descriptionUrl = null;
        private CmiAuthenticodeTimestamperInfo _timestamperInfo = null;

        internal CmiAuthenticodeSignerInfo() { }

        internal CmiAuthenticodeSignerInfo(int errorCode)
        {
            _error = errorCode;
        }

#if RUNTIME_TYPE_NETCORE
        [SupportedOSPlatform("windows")]
#endif
        internal CmiAuthenticodeSignerInfo(Win32.AXL_SIGNER_INFO signerInfo,
                                            Win32.AXL_TIMESTAMPER_INFO timestamperInfo)
        {
            _error = (int)signerInfo.dwError;
            if (signerInfo.pChainContext != IntPtr.Zero)
            {
                _signerChain = new X509Chain(signerInfo.pChainContext);
            }

            _algHash = signerInfo.algHash;
            if (signerInfo.pwszHash != IntPtr.Zero)
            {
                _hash = Marshal.PtrToStringUni(signerInfo.pwszHash);
            }
            if (signerInfo.pwszDescription != IntPtr.Zero)
            {
                _description = Marshal.PtrToStringUni(signerInfo.pwszDescription);
            }
            if (signerInfo.pwszDescriptionUrl != IntPtr.Zero)
            {
                _descriptionUrl = Marshal.PtrToStringUni(signerInfo.pwszDescriptionUrl);
            }
            if ((int)timestamperInfo.dwError != Win32.TRUST_E_NOSIGNATURE)
            {
                _timestamperInfo = new CmiAuthenticodeTimestamperInfo(timestamperInfo);
            }
        }

        internal int ErrorCode
        {
            get
            {
                return _error;
            }
            set
            {
                _error = value;
            }
        }

        internal uint HashAlgId
        {
            get
            {
                return _algHash;
            }
            set
            {
                _algHash = value;
            }
        }

        internal string Hash
        {
            get
            {
                return _hash;
            }
            set
            {
                _hash = value;
            }
        }

        internal string Description
        {
            get
            {
                return _description;
            }
            set
            {
                _description = value;
            }
        }

        internal string DescriptionUrl
        {
            get
            {
                return _descriptionUrl;
            }
            set
            {
                _descriptionUrl = value;
            }
        }

        internal CmiAuthenticodeTimestamperInfo TimestamperInfo
        {
            get
            {
                return _timestamperInfo;
            }
        }

        internal X509Chain SignerChain
        {
            get
            {
                return _signerChain;
            }
            set
            {
                _signerChain = value;
            }
        }
    }

    internal class CmiAuthenticodeTimestamperInfo
    {
        private int _error = 0;
        private X509Chain _timestamperChain = null;
        private DateTime _timestampTime;
        private uint _algHash = 0;

        private CmiAuthenticodeTimestamperInfo() { }

#if RUNTIME_TYPE_NETCORE
        [SupportedOSPlatform("windows")]
#endif
        internal CmiAuthenticodeTimestamperInfo(Win32.AXL_TIMESTAMPER_INFO timestamperInfo)
        {
            _error = (int)timestamperInfo.dwError;
            _algHash = timestamperInfo.algHash;
            long dt = (((long)(uint)timestamperInfo.ftTimestamp.dwHighDateTime) << 32) | ((long)(uint)timestamperInfo.ftTimestamp.dwLowDateTime);
            _timestampTime = DateTime.FromFileTime(dt);
            if (timestamperInfo.pChainContext != IntPtr.Zero)
            {
                _timestamperChain = new X509Chain(timestamperInfo.pChainContext);
            }
        }

        internal int ErrorCode
        {
            get
            {
                return _error;
            }
        }

        internal uint HashAlgId
        {
            get
            {
                return _algHash;
            }
        }

        internal DateTime TimestampTime
        {
            get
            {
                return _timestampTime;
            }
        }

        internal X509Chain TimestamperChain
        {
            get
            {
                return _timestamperChain;
            }
        }
    }
}

