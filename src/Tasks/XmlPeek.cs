// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;
using System.Xml.XPath;

using Microsoft.Build.Framework;
using Microsoft.Build.Shared;
using Microsoft.Build.Utilities;

namespace Microsoft.Build.Tasks
{
    /// <summary>
    /// A task that returns values as specified by XPath Query
    /// from an XML file.
    /// </summary>
    public class XmlPeek : TaskExtension
    {
        #region Members

        /// <summary>
        /// The XPath Query.
        /// </summary>
        private string _query;

        #endregion

        #region Properties
        /// <summary>
        /// The XML input as a file path.
        /// </summary>
        public ITaskItem XmlInputPath { get; set; }

        /// <summary>
        /// The XML input as a string.
        /// </summary>
        public string XmlContent { get; set; }

        /// <summary>
        /// The XPath Query.
        /// </summary>
        public string Query
        {
            get
            {
                ErrorUtilities.VerifyThrowArgumentNull(_query, "Query");
                return _query;
            }

            set => _query = value;
        }

        /// <summary>
        /// The results returned by this task.
        /// </summary>
        [Output]
        public ITaskItem[] Result { get; private set; }

        /// <summary>
        /// The namespaces for XPath query's prefixes.
        /// </summary>
        public string Namespaces { get; set; }

        /// <summary>
        /// Set to true to prohibit loading XML with embedded DTD and produce error MSB3733
        /// if DTD is present. This was a pre-v15 behavior. By default, a DTD clause if any is ignored.
        /// </summary>
        public bool ProhibitDtd { get; set; }
        #endregion

        /// <summary>
        /// Executes the XMLPeek task.
        /// </summary>
        /// <returns>true if transformation succeeds.</returns>
        public override bool Execute()
        {
            XmlInput xmlinput;
            ErrorUtilities.VerifyThrowArgumentNull(_query, nameof(Query));

            try
            {
                xmlinput = new XmlInput(XmlInputPath, XmlContent);
            }
            catch (Exception e)
            {
                if (ExceptionHandling.IsCriticalException(e))
                {
                    throw;
                }

                Log.LogErrorWithCodeFromResources("XmlPeek.ArgumentError", e.Message);
                return false;
            }

            XPathDocument xpathdoc;
            try
            {
                // Load the XPath Document
                using (XmlReader xr = xmlinput.CreateReader(ProhibitDtd))
                {
                    xpathdoc = new XPathDocument(xr);
                    xr.Dispose();
                }
            }
            catch (Exception e)
            {
                if (ExceptionHandling.IsCriticalException(e))
                {
                    throw;
                }

                Log.LogErrorWithCodeFromResources("XmlPeekPoke.InputFileError", XmlInputPath.ItemSpec, e.Message);
                return false;
            }
            finally
            {
                xmlinput.CloseReader();
            }

            XPathNavigator nav = xpathdoc.CreateNavigator();
            XPathExpression expr;
            try
            {
                // Create the expression from query
                expr = nav.Compile(_query);
            }
            catch (Exception e)
            {
                if (ExceptionHandling.IsCriticalException(e))
                {
                    throw;
                }

                Log.LogErrorWithCodeFromResources("XmlPeekPoke.XPathError", _query, e.Message);
                return false;
            }

            // Create the namespace manager and parse the input.
            var xmlNamespaceManager = new XmlNamespaceManager(nav.NameTable);

            try
            {
                LoadNamespaces(ref xmlNamespaceManager, Namespaces);
            }
            catch (Exception e)
            {
                if (ExceptionHandling.IsCriticalException(e))
                {
                    throw;
                }

                Log.LogErrorWithCodeFromResources("XmlPeek.NamespacesError", e.Message);
                return false;
            }

            try
            {
                expr.SetContext(xmlNamespaceManager);
            }
            catch (XPathException e)
            {
                Log.LogErrorWithCodeFromResources("XmlPeek.XPathContextError", e.Message);
                return false;
            }

            XPathNodeIterator iter = nav.Select(expr);

            var peekValues = new List<string>();
            while (iter.MoveNext())
            {
                if (iter.Current.NodeType == XPathNodeType.Attribute
                    || iter.Current.NodeType == XPathNodeType.Text)
                {
                    peekValues.Add(iter.Current.Value);
                }
                else
                {
                    peekValues.Add(iter.Current.OuterXml);
                }
            }

            Result = new ITaskItem[peekValues.Count];
            int i = 0;
            foreach (string item in peekValues)
            {
                Result[i++] = new TaskItem(item);

                // This can be logged a lot, so low importance
                Log.LogMessageFromResources(MessageImportance.Low, "XmlPeek.Found", item);
            }

            if (Result.Length == 0)
            {
                // Logged no more than once per execute of this task
                Log.LogMessageFromResources("XmlPeek.NotFound");
            }

            return true;
        }

        /// <summary>
        /// Loads the namespaces specified at Namespaces parameter to XmlNSManager.
        /// </summary>
        /// <param name="namespaceManager">The namespace manager to load namespaces to.</param>
        /// <param name="namepaces">The namespaces as XML snippet.</param>
        private static void LoadNamespaces(ref XmlNamespaceManager namespaceManager, string namepaces)
        {
            var doc = new XmlDocument();
            try
            {
                var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit };
                using (XmlReader reader = XmlReader.Create(new StringReader("<Namespaces>" + namepaces + "</Namespaces>"), settings))
                {
                    doc.Load(reader);
                }
            }
            catch (XmlException xe)
            {
                throw new ArgumentException(ResourceUtilities.GetResourceString("XmlPeek.NamespacesParameterNotWellFormed"), xe);
            }

            XmlNodeList xnl = doc.SelectNodes("/Namespaces/*[local-name() = 'Namespace']");
            for (int i = 0; i < xnl?.Count; i++)
            {
                XmlNode xn = xnl[i];

                const string prefixAttr = "Prefix";
                XmlAttribute prefix = xn.Attributes?[prefixAttr];
                if (prefix == null)
                {
                    throw new ArgumentException(ResourceUtilities.FormatResourceStringStripCodeAndKeyword("XmlPeek.NamespacesParameterNoAttribute", prefixAttr));
                }

                const string uriAttr = "Uri";
                XmlAttribute uri = xn.Attributes[uriAttr];
                if (uri == null)
                {
                    throw new ArgumentException(ResourceUtilities.FormatResourceStringStripCodeAndKeyword("XmlPeek.NamespacesParameterNoAttribute", uriAttr));
                }

                namespaceManager.AddNamespace(prefix.Value, uri.Value);
            }
        }

        /// <summary>
        /// This class prepares XML input from XMLInputPath and XMLContent parameters
        /// </summary>
        internal class XmlInput
        {
            /// <summary>
            /// This either contains the raw Xml or the path to Xml file.
            /// </summary>
            private readonly string _data;

            /// <summary>
            /// Filestream used to read XML.
            /// </summary>
            private FileStream _fs;

            /// <summary>
            /// Constructor.
            /// Only one parameter should be non null or will throw ArgumentException.
            /// </summary>
            /// <param name="xmlInputPath">The path to XML file or null.</param>
            /// <param name="xmlContent">The raw XML.</param>
            public XmlInput(ITaskItem xmlInputPath, string xmlContent)
            {
                if (xmlInputPath != null && xmlContent != null)
                {
                    throw new ArgumentException(ResourceUtilities.GetResourceString("XmlPeek.XmlInput.TooMany"));
                }
                else if (xmlInputPath == null && xmlContent == null)
                {
                    throw new ArgumentException(ResourceUtilities.GetResourceString("XmlPeek.XmlInput.TooFew"));
                }

                if (xmlInputPath != null)
                {
                    XmlMode = XmlModes.XmlFile;
                    _data = xmlInputPath.ItemSpec;
                }
                else
                {
                    XmlMode = XmlModes.Xml;
                    _data = xmlContent;
                }
            }

            /// <summary>
            /// Possible accepted types of XML input.
            /// </summary>
            public enum XmlModes
            {
                /// <summary>
                /// If the mode is a XML file.
                /// </summary>
                XmlFile,

                /// <summary>
                /// If the mode is a raw XML.
                /// </summary>
                Xml
            }

            /// <summary>
            /// Returns the current mode of the XmlInput
            /// </summary>
            public XmlModes XmlMode { get; }

            /// <summary>
            /// Creates correct reader based on the input type.
            /// </summary>
            /// <returns>The XmlReader object</returns>
            public XmlReader CreateReader(bool prohibitDtd = true)
            {
                var settings = new XmlReaderSettings
                {
                    DtdProcessing = prohibitDtd ? DtdProcessing.Prohibit : DtdProcessing.Ignore
                };
                if (XmlMode == XmlModes.XmlFile)
                {
                    _fs = new FileStream(_data, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    return XmlReader.Create(_fs, settings: settings);
                }
                else // xmlModes.Xml
                {
                    return XmlReader.Create(new StringReader(_data), settings: settings);
                }
            }

            /// <summary>
            /// Closes the reader.
            /// </summary>
            public void CloseReader()
            {
                if (_fs != null)
                {
                    _fs.Dispose();
                    _fs = null;
                }
            }
        }
    }
}
