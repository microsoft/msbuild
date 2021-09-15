// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.Build.Shared;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Xml;
using System.Xml.XPath;
using System.Xml.Xsl;
using Microsoft.Build.Shared.FileSystem;

namespace Microsoft.Build.Tasks.Deployment.Bootstrapper
{
    /// <summary>
    /// This class is the top-level object for the bootstrapper system.
    /// </summary>
    [ComVisible(true), Guid("1D9FE38A-0226-4b95-9C6B-6DFFA2236270"), ClassInterface(ClassInterfaceType.None)]
    public class BootstrapperBuilder : IBootstrapperBuilder
    {
        private static readonly bool s_logging = !String.IsNullOrEmpty(Environment.GetEnvironmentVariable("VSPLOG"));
        private static readonly string s_logPath = GetLogPath();

        private string _path;
        private XmlDocument _document;

        private XmlNamespaceManager _xmlNamespaceManager;
        private readonly ProductCollection _products = new ProductCollection();
        private readonly Dictionary<string, XmlNode> _cultures = new Dictionary<string, XmlNode>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ProductValidationResults> _validationResults = new Dictionary<string, ProductValidationResults>(StringComparer.Ordinal);
        private BuildResults _results;
        private BuildResults _loopDependenciesWarnings;
        private bool _fInitialized;

        private const string SETUP_EXE = "setup.exe";
        private const string SETUP_BIN = "setup.bin";
        private const string SETUP_RESOURCES_FILE = "setup.xml";

        private const string ENGINE_PATH = "Engine"; // relative to bootstrapper path
        private const string SCHEMA_PATH = "Schemas"; // relative to bootstrapper path
        private const string PACKAGE_PATH = "Packages"; // relative to bootstrapper path 
        private const string RESOURCES_PATH = "";

        private const string BOOTSTRAPPER_NAMESPACE = "http://schemas.microsoft.com/developer/2004/01/bootstrapper";

        private const string BOOTSTRAPPER_PREFIX = "bootstrapper";

        private const string ROOT_MANIFEST_FILE = "product.xml";
        private const string CHILD_MANIFEST_FILE = "package.xml";
        private const string MANIFEST_FILE_SCHEMA = "package.xsd";
        private const string CONFIG_TRANSFORM = "xmltoconfig.xsl";

        private const string EULA_ATTRIBUTE = "LicenseAgreement";
        private const string HOMESITE_ATTRIBUTE = "HomeSite";
        private const string PUBLICKEY_ATTRIBUTE = "PublicKey";
        private const string URLNAME_ATTRIBUTE = "UrlName";
        private const string HASH_ATTRIBUTE = "Hash";

        private const int MESSAGE_TABLE = 43;
        private const int RESOURCE_TABLE = 45;

        /// <summary>
        /// Creates a new BootstrapperBuilder.
        /// </summary>
        public BootstrapperBuilder()
        {
            _path = Util.DefaultPath;
        }

        /// <summary>
        /// Creates a new BootstrapperBuilder.
        /// </summary>
        /// <param name="visualStudioVersion">The version of Visual Studio that is used to build this bootstrapper.</param>
        public BootstrapperBuilder(string visualStudioVersion)
        {
            _path = Util.GetDefaultPath(visualStudioVersion);
        }

        #region IBootstrapperBuilder Members

        /// <summary>
        /// Specifies the location of the required bootstrapper files.
        /// </summary>
        /// <value>Path to bootstrapper files.</value>
        public string Path
        {
            get => _path;
            set
            {
                if (!_fInitialized || !string.Equals(_path, value, StringComparison.OrdinalIgnoreCase))
                {
                    _path = value;
                    Refresh();
                }
            }
        }

        /// <summary>
        /// Returns all products available at the current bootstrapper Path
        /// </summary>
        public ProductCollection Products
        {
            get
            {
                if (!_fInitialized)
                {
                    Refresh();
                }

                return _products;
            }
        }

        /// <summary>
        /// Generates a bootstrapper based on the specified settings.
        /// </summary>
        /// <param name="settings">The properties used to build this bootstrapper.</param>
        /// <returns>The results of the bootstrapper generation</returns>
        public BuildResults Build(BuildSettings settings)
        {
            _results = new BuildResults();
            try
            {
                if (settings.ApplicationFile == null && (settings.ProductBuilders == null || settings.ProductBuilders.Count == 0))
                {
                    _results.AddMessage(BuildMessage.CreateMessage(BuildMessageSeverity.Error, "GenerateBootstrapper.InvalidInput"));
                    return _results;
                }

                if (String.IsNullOrEmpty(settings.OutputPath))
                {
                    _results.AddMessage(BuildMessage.CreateMessage(BuildMessageSeverity.Error, "GenerateBootstrapper.NoOutputPath"));
                    return _results;
                }

                if (!_fInitialized)
                {
                    Refresh();
                }

                if (String.IsNullOrEmpty(settings.Culture))
                {
                    settings.Culture = MapLCIDToCultureName(settings.LCID);
                }
                if (String.IsNullOrEmpty(settings.FallbackCulture))
                {
                    settings.FallbackCulture = MapLCIDToCultureName(settings.FallbackLCID);
                }

                if (String.IsNullOrEmpty(settings.Culture) || settings.Culture == "*")
                {
                    settings.Culture = settings.FallbackCulture;
                }

                AddBuiltProducts(settings);

                var componentFilesCopied = new List<string>();

                // Copy setup.bin to the output directory
                string strOutputExe = System.IO.Path.Combine(settings.OutputPath, SETUP_EXE);
                if (!CopySetupToOutputDirectory(settings, strOutputExe))
                {
                    // Appropriate messages should have been stuffed into the results already
                    return _results;
                }

                var resourceUpdater = new ResourceUpdater();

                // Build up the String table for setup.exe
                if (!BuildResources(settings, resourceUpdater))
                {
                    // Appropriate messages should have been stuffed into the results already
                    return _results;
                }

                AddStringResourceForUrl(resourceUpdater, "BASEURL", settings.ApplicationUrl, "ApplicationUrl");
                AddStringResourceForUrl(resourceUpdater, "COMPONENTSURL", settings.ComponentsUrl, "ComponentsUrl");
                AddStringResourceForUrl(resourceUpdater, "SUPPORTURL", settings.SupportUrl, "SupportUrl");
                if (settings.ComponentsLocation == ComponentsLocation.HomeSite)
                {
                    resourceUpdater.AddStringResource(40, "HOMESITE", true.ToString());
                }

                XmlElement configElement = _document.CreateElement("Configuration");
                XmlElement applicationElement = CreateApplicationElement(configElement, settings);
                if (applicationElement != null)
                {
                    configElement.AppendChild(applicationElement);
                }

                // Key: File hash, Value: A DictionaryEntry whose Key is "EULAx" and value is a 
                // fully qualified path to a eula. It can be any eula that matches the hash.
                var eulas = new Dictionary<string, KeyValuePair<string, string>>(StringComparer.Ordinal);

                // Copy package files, add each Package config info to the config file
                if (!BuildPackages(settings, configElement, resourceUpdater, componentFilesCopied, eulas))
                {
                    return _results;
                }

                // Transform the configuration xml into something the bootstrapper will understand
                DumpXmlToFile(configElement, "bootstrapper.cfg.xml");
                string config = XmlToConfigurationFile(configElement);
                resourceUpdater.AddStringResource(41, "SETUPCFG", config);
                DumpStringToFile(config, "bootstrapper.cfg", false);

                // Put eulas in the resource stream
                foreach (KeyValuePair<string, string> de in eulas.Values)
                {
                    string data;
                    var fi = new FileInfo(de.Value);
                    using (FileStream fs = fi.OpenRead())
                    {
                        data = new StreamReader(fs).ReadToEnd();
                    }

                    resourceUpdater.AddStringResource(44, de.Key, data);
                }

                resourceUpdater.AddStringResource(44, "COUNT", eulas.Count.ToString(CultureInfo.InvariantCulture));
                if (!resourceUpdater.UpdateResources(strOutputExe, _results))
                {
                    return _results;
                }

                _results.SetKeyFile(strOutputExe);
                string[] componentFiles = new string[componentFilesCopied.Count];
                componentFilesCopied.CopyTo(componentFiles);
                _results.AddComponentFiles(componentFiles);
                _results.BuildSucceeded();
            }
            catch (Exception ex)
            {
                _results.AddMessage(BuildMessage.CreateMessage(BuildMessageSeverity.Error, "GenerateBootstrapper.General", ex.Message));
            }
            return _results;
        }

        private static void Merge(Dictionary<string, Product> output, Dictionary<string, Product> input)
        {
            foreach (Product product in input.Values)
            {
                AddProduct(output, product);
            }
        }

        private static void AddProduct(Dictionary<string, Product> output, Product product)
        {
            if (!output.ContainsKey(product.ProductCode.ToLowerInvariant()))
            {
                output.Add(product.ProductCode.ToLowerInvariant(), product);
            }
        }

        private void AddBuiltProducts(BuildSettings settings)
        {
            var builtProducts = new Dictionary<string, ProductBuilder>();
            var productsAndIncludes = new Dictionary<string, Product>();

            if (_loopDependenciesWarnings?.Messages != null)
            {
                foreach (BuildMessage message in _loopDependenciesWarnings.Messages)
                {
                    _results.AddMessage(message);
                }
            }

            foreach (ProductBuilder builder in settings.ProductBuilders)
            {
                builtProducts.Add(builder.Product.ProductCode.ToLowerInvariant(), builder);
                Merge(productsAndIncludes, GetIncludedProducts(builder.Product));
                AddProduct(productsAndIncludes, builder.Product);
            }

            foreach (ProductBuilder builder in settings.ProductBuilders)
            {
                Dictionary<string, Product> includes = GetIncludedProducts(builder.Product);
                foreach (Product p in includes.Values)
                {
                    if (builtProducts.ContainsKey(p.ProductCode.ToLowerInvariant()))
                    {
                        _results.AddMessage(BuildMessage.CreateMessage(BuildMessageSeverity.Warning, "GenerateBootstrapper.IncludedProductIncluded", builder.Name, p.Name));
                    }
                }

                foreach (List<Product> productDependency in builder.Product.Dependencies)
                {
                    bool foundDependency = false;
                    foreach (Product p in productDependency)
                    {
                        if (productsAndIncludes.ContainsKey(p.ProductCode.ToLowerInvariant()))
                        {
                            foundDependency = true;
                            break;
                        }
                    }

                    if (!foundDependency)
                    {
                        if (productDependency.Count == 1)
                        {
                            _results.AddMessage(BuildMessage.CreateMessage(BuildMessageSeverity.Warning, "GenerateBootstrapper.MissingDependency", productDependency[0].Name, builder.Name));
                        }
                        else
                        {
                            StringBuilder missingProductCodes = new StringBuilder();
                            foreach (Product product in productDependency)
                            {
                                missingProductCodes.Append(product.Name);
                                missingProductCodes.Append(", ");
                            }

                            string productCodes = missingProductCodes.ToString();
                            productCodes = productCodes.Substring(0, productCodes.Length - 2);
                            _results.AddMessage(BuildMessage.CreateMessage(BuildMessageSeverity.Warning, "GenerateBootstrapper.MissingDependencyMultiple", productCodes, builder.Name));
                        }
                    }
                }

                foreach (List<string> missingDependecies in builder.Product.MissingDependencies)
                {
                    if (missingDependecies.Count == 1)
                    {
                        _results.AddMessage(BuildMessage.CreateMessage(BuildMessageSeverity.Warning, "GenerateBootstrapper.DependencyNotFound", builder.Name, missingDependecies[0]));
                    }
                    else
                    {
                        var missingProductCodes = new StringBuilder();
                        foreach (string productCode in missingDependecies)
                        {
                            missingProductCodes.Append(productCode);
                            missingProductCodes.Append(", ");
                        }

                        string productCodes = missingProductCodes.ToString();
                        productCodes = productCodes.Substring(0, productCodes.Length - 2);
                        _results.AddMessage(BuildMessage.CreateMessage(BuildMessageSeverity.Warning, "GenerateBootstrapper.MultipleDependeciesNotFound", builder.Name, productCodes));
                    }
                }
            }
        }

        private bool CopySetupToOutputDirectory(BuildSettings settings, string strOutputExe)
        {
            string bootstrapperPath = BootstrapperPath;
            string setupSourceFile = System.IO.Path.Combine(bootstrapperPath, SETUP_BIN);

            if (!FileSystems.Default.FileExists(setupSourceFile))
            {
                _results.AddMessage(BuildMessage.CreateMessage(BuildMessageSeverity.Error, "GenerateBootstrapper.MissingSetupBin", SETUP_BIN, bootstrapperPath));
                return false;
            }

            try
            {
                EnsureFolderExists(settings.OutputPath);
                File.Copy(setupSourceFile, strOutputExe, true);
                ClearReadOnlyAttribute(strOutputExe);
            }
            catch (IOException ex)
            {
                _results.AddMessage(BuildMessage.CreateMessage(BuildMessageSeverity.Error, "GenerateBootstrapper.CopyError", setupSourceFile, strOutputExe, ex.Message));
                return false;
            }
            catch (UnauthorizedAccessException ex)
            {
                _results.AddMessage(BuildMessage.CreateMessage(BuildMessageSeverity.Error, "GenerateBootstrapper.CopyError", setupSourceFile, strOutputExe, ex.Message));
                return false;
            }
            catch (ArgumentException ex)
            {
                _results.AddMessage(BuildMessage.CreateMessage(BuildMessageSeverity.Error, "GenerateBootstrapper.CopyError", setupSourceFile, strOutputExe, ex.Message));
                return false;
            }
            catch (NotSupportedException ex)
            {
                _results.AddMessage(BuildMessage.CreateMessage(BuildMessageSeverity.Error, "GenerateBootstrapper.CopyError", setupSourceFile, strOutputExe, ex.Message));
                return false;
            }

            return true;
        }

        private void AddStringResourceForUrl(ResourceUpdater resourceUpdater, string name, string url, string nameToUseInLog)
        {
            if (!String.IsNullOrEmpty(url))
            {
                resourceUpdater.AddStringResource(40, name, url);
                if (!Util.IsWebUrl(url) && !Util.IsUncPath(url))
                {
                    _results.AddMessage(BuildMessage.CreateMessage(BuildMessageSeverity.Warning, "GenerateBootstrapper.InvalidUrl", nameToUseInLog, url));
                }
            }
        }

        #endregion

        /// <summary>
        /// Returns the directories bootstrapper component files would be copied to when built given the specified settings
        /// </summary>
        /// <param name="productCodes">The productCodes of the selected components</param>
        /// <param name="culture">The culture used to build the bootstrapper</param>
        /// <param name="fallbackCulture">The fallback culture used to build the bootstrapper</param>
        /// <param name="componentsLocation">How the bootstrapper would package the selected components</param>
        public string[] GetOutputFolders(string[] productCodes, string culture, string fallbackCulture, ComponentsLocation componentsLocation)
        {
            if (!_fInitialized)
            {
                Refresh();
            }

            var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var settings = new BuildSettings();
            string invariantPath = PackagePath.ToLowerInvariant();
            invariantPath = Util.AddTrailingChar(invariantPath, System.IO.Path.DirectorySeparatorChar);
            settings.CopyComponents = false;
            settings.Culture = culture;
            settings.FallbackCulture = fallbackCulture;
            settings.ComponentsLocation = componentsLocation;
            if (String.IsNullOrEmpty(settings.Culture) || settings.Culture == "*")
            {
                settings.Culture = settings.FallbackCulture;
            }

            foreach (string productCode in productCodes)
            {
                Product product = Products.Product(productCode);
                if (product != null)
                {
                    settings.ProductBuilders.Add(product.ProductBuilder);
                }
            }

            var files = new List<string>();
            BuildPackages(settings, null, null, files, null);

            List<string> packagePaths = new List<string>() { invariantPath };
            packagePaths.AddRange(Util.AdditionalPackagePaths.Select(p => Util.AddTrailingChar(p.ToLowerInvariant(), System.IO.Path.DirectorySeparatorChar)));

            foreach (string file in files)
            {
                string folder = System.IO.Path.GetDirectoryName(file);

                foreach (string packagePath in packagePaths)
                {
                    if (folder.Length >= packagePath.Length && folder.Substring(0, packagePath.Length).ToLowerInvariant().CompareTo(packagePath) == 0)
                    {
                        string relPath = folder.Substring(packagePath.Length);
                        if (!folders.Contains(relPath))
                        {
                            folders.Add(relPath);
                        }

                        break;
                    }
                }
            }

            return folders.ToArray();
        }

        internal bool ContainsCulture(string culture)
        {
            if (!_fInitialized)
            {
                Refresh();
            }
            return _cultures.ContainsKey(culture);
        }

        internal string[] Cultures
        {
            get
            {
                if (!_fInitialized)
                {
                    Refresh();
                }

                List<string> list = _cultures.Values.Select(v => v.ToString()).ToList();
                list.Sort();
                return list.ToArray();
            }
        }

        internal bool Validate { get; set; } = true;

        private string BootstrapperPath => System.IO.Path.Combine(Path, ENGINE_PATH);

        private string PackagePath => System.IO.Path.Combine(Path, PACKAGE_PATH);

        private string SchemaPath => System.IO.Path.Combine(Path, SCHEMA_PATH);

        private void Refresh()
        {
            RefreshResources();
            RefreshProducts();
            _fInitialized = true;

            if (s_logging)
            {
                StringBuilder productsOrder = new StringBuilder();
                foreach (Product p in Products)
                {
                    productsOrder.Append(p.ProductCode).Append(Environment.NewLine);
                }
                DumpStringToFile(productsOrder.ToString(), "BootstrapperInstallOrder.txt", false);
            }
        }

        private void RefreshResources()
        {
            string startDirectory = System.IO.Path.Combine(BootstrapperPath, RESOURCES_PATH);
            _cultures.Clear();

            if (FileSystems.Default.DirectoryExists(startDirectory))
            {
                foreach (string subDirectory in Directory.GetDirectories(startDirectory))
                {
                    string resourceDirectory = System.IO.Path.Combine(startDirectory, subDirectory);
                    string resourceFile = System.IO.Path.Combine(resourceDirectory, SETUP_RESOURCES_FILE);
                    if (FileSystems.Default.FileExists(resourceFile))
                    {
                        var resourceDoc = new XmlDocument();
                        try
                        {
                            var xrs = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore };
                            using (var xr = XmlReader.Create(resourceFile, xrs))
                            {
                                resourceDoc.Load(xr);
                            }
                        }
                        catch (XmlException ex)
                        {
                            // UNDONE: Log exception due to bad resource file
                            Debug.Fail(ex.Message);
                            continue;
                        }

                        XmlNode rootNode = resourceDoc.SelectSingleNode("Resources");
                        XmlAttribute cultureAttribute = (XmlAttribute) rootNode?.Attributes.GetNamedItem("Culture");
                        if (cultureAttribute != null)
                        {
                            XmlNode stringsNode = rootNode.SelectSingleNode("Strings");
                            XmlNode stringNode = stringsNode?.SelectSingleNode(string.Format(CultureInfo.InvariantCulture, "String[@Name='{0}']", cultureAttribute.Value));
                            if (stringNode != null)
                            {
                                string culture = stringNode.InnerText;

                                XmlNode resourcesNode = rootNode.OwnerDocument.ImportNode(rootNode, true);
                                resourcesNode.Attributes.RemoveNamedItem("Culture");
                                var newAttribute = (XmlAttribute)rootNode.OwnerDocument.ImportNode(cultureAttribute, false);
                                newAttribute.Value = stringNode.InnerText;
                                resourcesNode.Attributes.Append(newAttribute);
                                if (!_cultures.ContainsKey(culture))
                                {
                                    _cultures.Add(culture, resourcesNode);
                                }
                                else
                                {
                                    Debug.Fail("Already found resources for culture " + stringNode.InnerText);
                                }
                            }
                        }
                    }
                }
            }
        }

        private void RefreshProducts()
        {
            _products.Clear();
            _validationResults.Clear();
            _document = new XmlDocument();
            _xmlNamespaceManager = new XmlNamespaceManager(_document.NameTable);

            _xmlNamespaceManager.AddNamespace(BOOTSTRAPPER_PREFIX, BOOTSTRAPPER_NAMESPACE);

            XmlElement rootElement = _document.CreateElement("Products", BOOTSTRAPPER_NAMESPACE);

            List<string> packagePaths = new List<string>() { PackagePath };
            packagePaths.AddRange(Util.AdditionalPackagePaths);
            foreach (string packagePath in packagePaths)
            {
                if (FileSystems.Default.DirectoryExists(packagePath))
                {
                    foreach (string strSubDirectory in Directory.GetDirectories(packagePath))
                    {
                        int nStartIndex = packagePath.Length;
                        if ((strSubDirectory.ToCharArray())[nStartIndex] == System.IO.Path.DirectorySeparatorChar)
                        {
                            nStartIndex++;
                        }

                        ExploreDirectory(strSubDirectory.Substring(nStartIndex), rootElement, packagePath);
                    }
                }
            }

            _document.AppendChild(rootElement);

            var availableProducts = new Dictionary<string, Product>(StringComparer.Ordinal);
            // A second copy of all the project which will get destroyed during the generation of the build order
            var buildQueue = new Dictionary<string, Product>(StringComparer.Ordinal);

            XmlNodeList productsFound = rootElement.SelectNodes(BOOTSTRAPPER_PREFIX + ":Product", _xmlNamespaceManager);
            foreach (XmlNode productNode in productsFound)
            {
                Product p = CreateProduct(productNode);
                if (p != null)
                {
                    availableProducts.Add(p.ProductCode, p);
                    buildQueue.Add(p.ProductCode, CreateProduct(productNode));
                }
            }

            // Set the product and included products for each product
            foreach (Product p in availableProducts.Values)
            {
                AddDependencies(p, availableProducts);
                AddIncludes(p, availableProducts);
            }

            // We need only the dependencies to generate the bulid order
            foreach (Product p in buildQueue.Values)
            {
                AddDependencies(p, buildQueue);
            }

            // Scan the products and their dependencies to calculate install order
            OrderProducts(availableProducts, buildQueue);
        }

        private void AddDependencies(Product p, Dictionary<string, Product> availableProducts)
        {
            foreach (string relatedProductCode in SelectRelatedProducts(p, "DependsOnProduct"))
            {
                if (availableProducts.TryGetValue(relatedProductCode, out Product product))
                {
                    p.AddDependentProduct(product);
                }
                else
                {
                    p.AddMissingDependency(new List<string> { relatedProductCode });
                }
            }

            foreach (XmlNode eitherProductNode in SelectEitherProducts(p))
            {
                var foundDependencies = new List<Product>();
                var allDependencies = new List<string>();

                foreach (XmlNode relatedProductNode in eitherProductNode.SelectNodes(String.Format(CultureInfo.InvariantCulture, "{0}:DependsOnProduct", BOOTSTRAPPER_PREFIX), _xmlNamespaceManager))
                {
                    var relatedProductAttribute = (XmlAttribute)(relatedProductNode.Attributes.GetNamedItem("Code"));
                    if (relatedProductAttribute != null)
                    {
                        string dependency = relatedProductAttribute.Value;
                        if (availableProducts.TryGetValue(dependency, out Product product))
                        {
                            foundDependencies.Add(product);
                        }
                        allDependencies.Add(dependency);
                    }
                }

                if (foundDependencies.Count > 0)
                {
                    if (!p.ContainsDependencies(foundDependencies))
                    {
                        p.Dependencies.Add(foundDependencies);
                    }
                }
                else if (allDependencies.Count > 0)
                {
                    p.AddMissingDependency(allDependencies);
                }
            }
        }

        private void AddIncludes(Product p, Dictionary<string, Product> availableProducts)
        {
            foreach (string relatedProductCode in SelectRelatedProducts(p, "IncludesProduct"))
            {
                if (availableProducts.TryGetValue(relatedProductCode, out Product product))
                {
                    p.Includes.Add(product);
                }
            }
        }

        private string[] SelectRelatedProducts(Product p, string nodeName)
        {
            var list = new List<string>();

            XmlNodeList relatedProducts = p.Node.SelectNodes(string.Format(CultureInfo.InvariantCulture, "{0}:Package/{1}:RelatedProducts/{2}:{3}", BOOTSTRAPPER_PREFIX, BOOTSTRAPPER_PREFIX, BOOTSTRAPPER_PREFIX, nodeName), _xmlNamespaceManager);
            if (relatedProducts != null)
            {
                foreach (XmlNode relatedProduct in relatedProducts)
                {
                    XmlAttribute relatedProductAttribute = (XmlAttribute)(relatedProduct.Attributes.GetNamedItem("Code"));
                    if (relatedProductAttribute != null)
                    {
                        list.Add(relatedProductAttribute.Value);
                    }
                }
            }

            return list.ToArray();
        }

        private XmlNodeList SelectEitherProducts(Product p)
        {
            XmlNodeList eitherProducts = p.Node.SelectNodes(string.Format(CultureInfo.InvariantCulture, "{0}:Package/{1}:RelatedProducts/{2}:EitherProducts", BOOTSTRAPPER_PREFIX, BOOTSTRAPPER_PREFIX, BOOTSTRAPPER_PREFIX), _xmlNamespaceManager);
            return eitherProducts;
        }

        private void OrderProducts(Dictionary<string, Product> availableProducts, Dictionary<string, Product> buildQueue)
        {
            bool loopDetected = false;
            _loopDependenciesWarnings = new BuildResults();
            var productsInLoop = new StringBuilder();
            var productsToRemove = new List<string>();
            while (buildQueue.Count > 0)
            {
                productsToRemove.Clear();
                foreach (Product p in buildQueue.Values)
                {
                    if (p.Dependencies.Count == 0)
                    {
                        _products.Add(availableProducts[p.ProductCode]);
                        RemoveDependency(buildQueue, p);
                        productsToRemove.Add(p.ProductCode);
                    }
                }

                foreach (string productCode in productsToRemove)
                {
                    buildQueue.Remove(productCode);
                    if (loopDetected)
                    {
                        productsInLoop.Append(productCode);
                        productsInLoop.Append(", ");
                    }
                }

                // If we could not remove any products and there are still products in the queue
                // there must be a loop in it. We'll break the loop by removing the dependencies 
                // of the first project in the queue;
                if (buildQueue.Count > 0 && productsToRemove.Count == 0)
                {
                    Product p = buildQueue.Values.First();
                    p.Dependencies.RemoveAll(m => true);
                    loopDetected = true;
                }

                // If we've been in a loop and there are no more products left
                // or no more products can be installed, we have completely walked that loop
                // and now is a good time to show the warning message for the loop
                if (productsInLoop.Length > 0 && (buildQueue.Count == 0 || productsToRemove.Count == 0))
                {
                    productsInLoop.Remove(productsInLoop.Length - 2, 2);
                    _loopDependenciesWarnings.AddMessage(BuildMessage.CreateMessage(BuildMessageSeverity.Warning, "GenerateBootstrapper.CircularDependency", productsInLoop.ToString()));
                    productsInLoop.Remove(0, productsInLoop.Length);
                }
            }
        }

        private static void RemoveDependency(Dictionary<string, Product> availableProducts, Product product)
        {
            foreach (Product p in availableProducts.Values)
            {
                foreach (List<Product> dependency in p.Dependencies)
                {
                    dependency.RemoveAll(m => m == product);
                }
                p.Dependencies.RemoveAll(m => m.Count == 0);
            }
        }

        private XmlDocument LoadAndValidateXmlDocument(string filePath, bool validateFilePresent, string schemaPath, string schemaNamespace, XmlValidationResults results)
        {
            XmlDocument xmlDocument = null;

            Debug.Assert(filePath != null, "null filePath?");
            Debug.Assert(schemaPath != null, "null schemaPath?");
            Debug.Assert(schemaNamespace != null, "null schemaNamespace?");

            if ((filePath != null) && (schemaPath != null) && (schemaNamespace != null))
            {
                // set up our validation logic by detecting the trace-switch enabled and whether or
                //   not our files exist.
                bool validate = true;
                bool fileExists = FileSystems.Default.FileExists(filePath);
                bool schemaExists = FileSystems.Default.FileExists(schemaPath);

                // if we're being asked to validate but we can't find the schema file, then
                //   output something useful to tell user that we can't find the schema.
                if (!schemaExists)
                {
                    Debug.Fail("Could not locate schema '" + schemaPath + "', so no validation of '" + filePath + "' is possible.");
                    validate = false;
                }

                // if we're being asked to validate but we can't find the data file, then
                //   output something useful to tell user that we can't find the file and that we
                //   can't do anything useful.
                if (validate && (!fileExists) && validateFilePresent)
                {
                    Debug.Fail("Could not locate data file '" + filePath + "'.");
                    validate = false;
                }

                if (fileExists)
                {
                    var xmlTextReader = new XmlTextReader(filePath) { DtdProcessing = DtdProcessing.Ignore };

                    XmlReader xmlReader = xmlTextReader;

                    if (validate)
                    {
#pragma warning disable 618 // Using XmlValidatingReader. TODO: We need to switch to using XmlReader.Create() with validation.
                        var validatingReader = new XmlValidatingReader(xmlReader);
#pragma warning restore 618
                        var xrSettings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore };
                        using (XmlReader xr = XmlReader.Create(schemaPath, xrSettings))
                        {
                            try
                            {
                                // first, add our schema to the validating reader's collection of schemas
                                var xmlSchema = validatingReader.Schemas.Add(null, xr);

                                // if our schema namespace gets out of sync,
                                //   then all of our calls to SelectNodes and SelectSingleNode will fail
                                Debug.Assert((xmlSchema != null) &&
                                    string.Equals(schemaNamespace, xmlSchema.TargetNamespace, StringComparison.Ordinal),
                                    System.IO.Path.GetFileName(schemaPath) + " and BootstrapperBuilder.vb have mismatched namespaces, so the BootstrapperBuilder will fail to work.");

                                // if we're supposed to be validating, then hook up our handler
                                validatingReader.ValidationEventHandler += results.SchemaValidationEventHandler;

                                // switch readers so the doc does the actual read over the validating
                                //   reader so we get validation events as we load the document
                                xmlReader = validatingReader;
                            }
                            catch (XmlException ex)
                            {
                                Debug.Fail("Failed to load schema '" + schemaPath + "' due to the following exception:\r\n" + ex.Message);
                                validate = false;
                            }
                            catch (System.Xml.Schema.XmlSchemaException ex)
                            {
                                Debug.Fail("Failed to load schema '" + schemaPath + "' due to the following exception:\r\n" + ex.Message);
                                validate = false;
                            }
                        }
                    }

                    try
                    {
                        Debug.Assert(_document != null, "our document should have been created by now!");
                        xmlDocument = new XmlDocument(_document.NameTable);
                        xmlDocument.Load(xmlReader);
                    }
                    catch (XmlException ex)
                    {
                        Debug.Fail("Failed to load document '" + filePath + "' due to the following exception:\r\n" + ex.Message);
                        return null;
                    }
                    catch (System.Xml.Schema.XmlSchemaException ex)
                    {
                        Debug.Fail("Failed to load document '" + filePath + "' due to the following exception:\r\n" + ex.Message);
                        return null;
                    }
                    finally
                    {
                        xmlReader.Close();
                    }

                    // Note that the xml document's default namespace must match the schema namespace
                    //   or none of our SelectNodes/SelectSingleNode calls will succeed
                    Debug.Assert(xmlDocument.DocumentElement != null &&
                        string.Equals(xmlDocument.DocumentElement.NamespaceURI, schemaNamespace, StringComparison.Ordinal),
                        "'" + xmlDocument.DocumentElement.NamespaceURI + "' is not '" + schemaNamespace + "'...");

                    if ((xmlDocument.DocumentElement == null) ||
                       (!string.Equals(xmlDocument.DocumentElement.NamespaceURI, schemaNamespace, StringComparison.Ordinal)))
                    {
                    }
                }
            }

            return xmlDocument;
        }

        private void ExploreDirectory(string strSubDirectory, XmlElement rootElement, string packagePath)
        {
            try
            {
                string strSubDirectoryFullPath = System.IO.Path.Combine(packagePath, strSubDirectory);

                // figure out our product file paths based on the directory full path
                string strBaseManifestFilename = System.IO.Path.Combine(strSubDirectoryFullPath, ROOT_MANIFEST_FILE);
                string strBaseManifestSchemaFileName = System.IO.Path.Combine(SchemaPath, MANIFEST_FILE_SCHEMA);

                var productValidationResults = new ProductValidationResults(strBaseManifestFilename);

                // open the XmlDocument for this product.xml
                XmlDocument productDoc = LoadAndValidateXmlDocument(strBaseManifestFilename, false, strBaseManifestSchemaFileName, BOOTSTRAPPER_NAMESPACE, productValidationResults);
                if (productDoc != null)
                {
                    bool packageAdded = false;

                    XmlNode baseNode = productDoc.SelectSingleNode(BOOTSTRAPPER_PREFIX + ":Product", _xmlNamespaceManager);
                    if (baseNode != null)
                    {
                        // Get the ProductCode attribute for this product
                        var productCodeAttribute = (XmlAttribute)(baseNode.Attributes.GetNamedItem("ProductCode"));
                        if (productCodeAttribute != null)
                        {
                            // now add it to our full document if it's not already present
                            XmlNode productNode = rootElement.SelectSingleNode(BOOTSTRAPPER_PREFIX + ":Product[@ProductCode='" + productCodeAttribute.Value + "']", _xmlNamespaceManager);
                            if (productNode == null)
                            {
                                productNode = CreateProductNode(baseNode);
                            }
                            else
                            {
                                _validationResults.TryGetValue(
                                    productCodeAttribute.Value,
                                    out productValidationResults);
                            }

                            // Fix-up the <PackageFiles> of the base node to include the SourcePath and TargetPath
                            XmlNode packageFilesNode = baseNode.SelectSingleNode(BOOTSTRAPPER_PREFIX + ":PackageFiles", _xmlNamespaceManager);
                            XmlNode checksNode = baseNode.SelectSingleNode(BOOTSTRAPPER_PREFIX + ":InstallChecks", _xmlNamespaceManager);
                            XmlNode commandsNode = baseNode.SelectSingleNode(BOOTSTRAPPER_PREFIX + ":Commands", _xmlNamespaceManager);

                            // if there was a packageFiles node, then add it in to our full document with the rest
                            if (packageFilesNode != null)
                            {
                                UpdatePackageFileNodes(packageFilesNode, System.IO.Path.Combine(packagePath, strSubDirectory), strSubDirectory);

                                ReplacePackageFileAttributes(checksNode, "PackageFile", packageFilesNode, "PackageFile", "OldName", "Name");
                                ReplacePackageFileAttributes(commandsNode, "PackageFile", packageFilesNode, "PackageFile", "OldName", "Name");
                                ReplacePackageFileAttributes(baseNode, EULA_ATTRIBUTE, packageFilesNode, "PackageFile", "OldName", "SourcePath");
                            }

                            foreach (string strLanguageDirectory in Directory.GetDirectories(strSubDirectoryFullPath))
                            {
                                // The base node would get destroyed as we build-up this new node.
                                // Thus, we want to use a copy of the baseNode
                                var baseElement = (XmlElement)(_document.ImportNode(baseNode, true));

                                string strLangManifestFilename = System.IO.Path.Combine(strLanguageDirectory, CHILD_MANIFEST_FILE);
                                string strLangManifestSchemaFileName = System.IO.Path.Combine(SchemaPath, MANIFEST_FILE_SCHEMA);

                                if (FileSystems.Default.FileExists(strLangManifestFilename))
                                {
                                    // Load Package.xml
                                    XmlValidationResults packageValidationResults = new XmlValidationResults(strLangManifestFilename);
                                    XmlDocument langDoc = LoadAndValidateXmlDocument(strLangManifestFilename, false, strLangManifestSchemaFileName, BOOTSTRAPPER_NAMESPACE, packageValidationResults);

                                    Debug.Assert(langDoc != null, "we couldn't load package.xml in '" + strLangManifestFilename + "'...?");
                                    if (langDoc == null)
                                    {
                                        continue;
                                    }

                                    XmlNode langNode = langDoc.SelectSingleNode(BOOTSTRAPPER_PREFIX + ":Package", _xmlNamespaceManager);
                                    Debug.Assert(langNode != null, string.Format(CultureInfo.CurrentCulture, "Unable to find a package node in {0}", strLangManifestFilename));
                                    if (langNode != null)
                                    {
                                        XmlElement langElement = (XmlElement)(_document.ImportNode(langNode, true));
                                        XmlElement mergeElement = _document.CreateElement("Package", BOOTSTRAPPER_NAMESPACE);

                                        // Update the "PackageFiles" section to reflect this language subdirectory
                                        XmlNode packageFilesNodePackage = langElement.SelectSingleNode(BOOTSTRAPPER_PREFIX + ":PackageFiles", _xmlNamespaceManager);
                                        checksNode = langElement.SelectSingleNode(BOOTSTRAPPER_PREFIX + ":InstallChecks", _xmlNamespaceManager);
                                        commandsNode = langElement.SelectSingleNode(BOOTSTRAPPER_PREFIX + ":Commands", _xmlNamespaceManager);

                                        if (packageFilesNodePackage != null)
                                        {
                                            int nStartIndex = packagePath.Length;

                                            if ((strLanguageDirectory.ToCharArray())[nStartIndex] ==
                                                System.IO.Path.DirectorySeparatorChar)
                                            {
                                                nStartIndex++;
                                            }
                                            UpdatePackageFileNodes(packageFilesNodePackage, strLanguageDirectory, strSubDirectory);

                                            ReplacePackageFileAttributes(checksNode, "PackageFile", packageFilesNodePackage, "PackageFile", "OldName", "Name");
                                            ReplacePackageFileAttributes(commandsNode, "PackageFile", packageFilesNodePackage, "PackageFile", "OldName", "Name");
                                            ReplacePackageFileAttributes(langElement, EULA_ATTRIBUTE, packageFilesNodePackage, "PackageFile", "OldName", "SourcePath");
                                        }

                                        if (packageFilesNode != null)
                                        {
                                            ReplacePackageFileAttributes(checksNode, "PackageFile", packageFilesNode, "PackageFile", "OldName", "Name");
                                            ReplacePackageFileAttributes(commandsNode, "PackageFile", packageFilesNode, "PackageFile", "OldName", "Name");
                                            ReplacePackageFileAttributes(langElement, EULA_ATTRIBUTE, packageFilesNode, "PackageFile", "OldName", "SourcePath");
                                        }

                                        // in general, we prefer the attributes of the language document over the 
                                        //  attributes of the base document.  Copy attributes from the lang to the merged,
                                        //  and then merge all unique elements into merge
                                        foreach (XmlAttribute attribute in langElement.Attributes)
                                        {
                                            mergeElement.Attributes.Append((XmlAttribute)(mergeElement.OwnerDocument.ImportNode(attribute, false)));
                                        }

                                        foreach (XmlAttribute attribute in baseElement.Attributes)
                                        {
                                            var convertedAttribute = (XmlAttribute)(mergeElement.OwnerDocument.ImportNode(attribute, false));
                                            MergeAttribute(mergeElement, convertedAttribute);
                                        }

                                        // And append all of the nodes
                                        //  There is a well-known set of nodes which may have inherit children
                                        //  When merging these nodes, there may be subnodes taken from both the lang element and the base element.
                                        //  There will never be multiple nodes with the same name in the same manifest
                                        //  The function which performs this action is CombineElements(...)
                                        CombineElements(langElement, baseElement, "Commands", "PackageFile", mergeElement);
                                        CombineElements(langElement, baseElement, "InstallChecks", "Property", mergeElement);
                                        CombineElements(langElement, baseElement, "PackageFiles", "Name", mergeElement);
                                        CombineElements(langElement, baseElement, "Schedules", "Name", mergeElement);
                                        CombineElements(langElement, baseElement, "Strings", "Name", mergeElement);

                                        ReplaceStrings(mergeElement);
                                        CorrectPackageFiles(mergeElement);

                                        AppendNode(baseElement, "RelatedProducts", mergeElement);

                                        // Create a unique identifier for this package
                                        var cultureAttribute = (XmlAttribute)mergeElement.Attributes.GetNamedItem("Culture");
                                        if (!String.IsNullOrEmpty(cultureAttribute?.Value))
                                        {
                                            string packageCode = productCodeAttribute.Value + "." + cultureAttribute.Value;
                                            AddAttribute(mergeElement, "PackageCode", packageCode);

                                            if (productValidationResults != null && packageValidationResults != null)
                                            {
                                                productValidationResults.AddPackageResults(cultureAttribute.Value, packageValidationResults);
                                            }

                                            // Only add this package if there is a culture apecified.
                                            productNode.AppendChild(mergeElement);
                                            packageAdded = true;
                                        }
                                    }
                                }
                            }
                            if (packageAdded)
                            {
                                rootElement.AppendChild(productNode);
                                if (!_validationResults.ContainsKey(productCodeAttribute.Value))
                                {
                                    _validationResults.Add(productCodeAttribute.Value, productValidationResults);
                                }
                                else
                                {
                                    Debug.WriteLine(String.Format(CultureInfo.CurrentCulture, "Validation results already added for Product Code '{0}'", productCodeAttribute));
                                }
                            }
                        }
                    }
                }
            }
            catch (XmlException ex)
            {
                Debug.Fail(ex.Message);
            }
            catch (IOException ex)
            {
                Debug.Fail(ex.Message);
            }
            catch (ArgumentException ex)
            {
                Debug.Fail(ex.Message);
            }
        }

        private Product CreateProduct(XmlNode node)
        {
            bool fPackageAdded = false;
            string productCode = ReadAttribute(node, "ProductCode");
            Product product = null;
            if (!String.IsNullOrEmpty(productCode))
            {
                _validationResults.TryGetValue(productCode, out ProductValidationResults results);

                XmlNode packageFilesNode = node.SelectSingleNode(BOOTSTRAPPER_PREFIX + ":Package/" + BOOTSTRAPPER_PREFIX + ":PackageFiles", _xmlNamespaceManager);
                string copyAllPackageFiles = String.Empty;

                if (packageFilesNode != null) copyAllPackageFiles = ReadAttribute(packageFilesNode, "CopyAllPackageFiles");

                product = new Product(node, productCode, results, copyAllPackageFiles);
                XmlNodeList packageNodeList = node.SelectNodes(BOOTSTRAPPER_PREFIX + ":Package", _xmlNamespaceManager);

                foreach (XmlNode packageNode in packageNodeList)
                {
                    Package package = CreatePackage(packageNode, product);
                    if (package != null)
                    {
                        product.AddPackage(package);
                        fPackageAdded = true;
                    }
                }
            }

            if (fPackageAdded)
                return product;
            return null;
        }

        private static Package CreatePackage(XmlNode node, Product product)
        {
            string culture = ReadAttribute(node, "Culture");

            XmlValidationResults results;
            if (culture != null)
            {
                results = product.GetPackageValidationResults(culture);
            }
            else
            {
                return null;
            }

            return new Package(product, node, results, ReadAttribute(node, "Name"), ReadAttribute(node, "Culture"));
        }

        private void ReplaceAttributes(XmlNode targetNode, string attributeName, string oldValue, string newValue)
        {
            if (targetNode != null)
            {
                // select all nodes where the attributeName equals the oldValue
                XmlNodeList nodeList = targetNode.SelectNodes(BOOTSTRAPPER_PREFIX + string.Format(CultureInfo.InvariantCulture, ":*[@{0}='{1}']", attributeName, oldValue), _xmlNamespaceManager);

                foreach (XmlNode node in nodeList)
                {
                    ReplaceAttribute(node, attributeName, newValue);
                }

                // replace attributes on the node itself
                XmlAttribute attrib = targetNode.Attributes[attributeName];
                if (attrib != null && attrib.Value == oldValue)
                {
                    attrib.Value = newValue;
                }
            }
        }

        private static void ReplaceAttribute(XmlNode targetNode, string attributeName, string attributeValue)
        {
            XmlAttribute attribute = targetNode.OwnerDocument.CreateAttribute(attributeName);
            attribute.Value = attributeValue;
            targetNode.Attributes.SetNamedItem(attribute);
        }

        private static void MergeAttribute(XmlNode targetNode, XmlAttribute attribute)
        {
            var targetAttribute = (XmlAttribute)(targetNode.Attributes.GetNamedItem(attribute.Name));
            if (targetAttribute == null)
            {
                // This node does not already contain the attribute.  Add the parameter
                targetNode.Attributes.Append(attribute);
            }
        }

        private void UpdatePackageFileNodes(XmlNode packageFilesNode, string strSourcePath, string strTargetPath)
        {
            XmlNodeList packageFileNodeList = packageFilesNode.SelectNodes(BOOTSTRAPPER_PREFIX + ":PackageFile", _xmlNamespaceManager);

            foreach (XmlNode packageFileNode in packageFileNodeList)
            {
                var nameAttribute = (XmlAttribute)(packageFileNode.Attributes.GetNamedItem("Name"));

                // the name attribute is required -- we can't do anything if it's not present
                if (nameAttribute != null)
                {
                    string relativePath = nameAttribute.Value;

                    XmlAttribute sourcePathAttribute = packageFilesNode.OwnerDocument.CreateAttribute("SourcePath");
                    string strSourceFile = System.IO.Path.Combine(strSourcePath, relativePath);
                    sourcePathAttribute.Value = strSourceFile;

                    XmlAttribute targetPathAttribute = packageFilesNode.OwnerDocument.CreateAttribute("TargetPath");
                    targetPathAttribute.Value = System.IO.Path.Combine(strTargetPath, relativePath);

                    string oldNameValue = nameAttribute.Value;
                    string newNameValue = System.IO.Path.Combine(strTargetPath, relativePath);

                    XmlAttribute oldNameAttribute = packageFilesNode.OwnerDocument.CreateAttribute("OldName");
                    oldNameAttribute.Value = oldNameValue;

                    ReplaceAttribute(packageFileNode, "Name", newNameValue);
                    MergeAttribute(packageFileNode, sourcePathAttribute);
                    MergeAttribute(packageFileNode, targetPathAttribute);
                    MergeAttribute(packageFileNode, oldNameAttribute);
                }
            }
        }

        private void AppendNode(XmlElement element, string nodeName, XmlElement mergeElement)
        {
            XmlNode node = element.SelectSingleNode(BOOTSTRAPPER_PREFIX + ":" + nodeName, _xmlNamespaceManager);
            if (node != null)
            {
                mergeElement.AppendChild(node);
            }
        }

        private void CombineElements(XmlElement langElement, XmlElement baseElement, string strNodeName, string strSubNodeKey, XmlElement mergeElement)
        {
            XmlNode langNode = langElement.SelectSingleNode(BOOTSTRAPPER_PREFIX + ":" + strNodeName, _xmlNamespaceManager);
            XmlNode baseNode = baseElement.SelectSingleNode(BOOTSTRAPPER_PREFIX + ":" + strNodeName, _xmlNamespaceManager);

            // There are 4 basic cases to be dealt with:
            // Case #    1       2       3       4      
            // base      null    null    present present
            // lang      null    present null    present
            // Result    null    lang    base    combine
            //
            // Cases 1 - 3 are pretty trivial.  
            if (baseNode == null)
            {
                if (langNode != null)
                {
                    // Case 2
                    mergeElement.AppendChild(langNode);
                }
                // Case 1 is to do nothing
            }
            else
            {
                if (langNode == null)
                {
                    // Case 3
                    mergeElement.AppendChild(baseNode);
                }
                else
                {
                    XmlNode mergeSubNode = _document.CreateElement(strNodeName, BOOTSTRAPPER_NAMESPACE);
                    XmlNode nextNode = baseNode.FirstChild;

                    // Begin case 4
                    // Go through every element in the base node
                    while (nextNode != null)
                    {
                        if (nextNode.NodeType == XmlNodeType.Element)
                        {
                            XmlAttribute keyAttribute = (XmlAttribute)(nextNode.Attributes.GetNamedItem(strSubNodeKey));
                            if (keyAttribute != null)
                            {
                                XmlNode queryResultNode = QueryForSubNode(langNode, strSubNodeKey, keyAttribute.Value);
                                // if there is no match in the lang node, use the current base node
                                //  Otherwise use that node and remove it later
                                if (queryResultNode == null)
                                {
                                    mergeSubNode.AppendChild(mergeSubNode.OwnerDocument.ImportNode(nextNode, true));
                                }
                                else
                                {
                                    mergeSubNode.AppendChild(mergeSubNode.OwnerDocument.ImportNode(queryResultNode, true));
                                    langNode.RemoveChild(queryResultNode);
                                }
                            }
                            else
                            {
                                Debug.Assert(false, "Specified key does not exist for node " + nextNode.InnerXml);
                            }
                        }
                        nextNode = nextNode.NextSibling;
                    }

                    // Append all remaining lang nodes
                    nextNode = langNode.FirstChild;

                    while (nextNode != null)
                    {
                        mergeSubNode.AppendChild(mergeSubNode.OwnerDocument.ImportNode(nextNode, true));
                        nextNode = nextNode.NextSibling;
                    }

                    // Copy all attributes.  The langnode again has priority
                    foreach (XmlAttribute attribute in langNode.Attributes)
                    {
                        AddAttribute(mergeSubNode, attribute.Name, attribute.Value);
                    }
                    foreach (XmlAttribute attribute in baseNode.Attributes)
                    {
                        if (mergeSubNode.Attributes.GetNamedItem(attribute.Name) == null)
                        {
                            AddAttribute(mergeSubNode, attribute.Name, attribute.Value);
                        }
                    }

                    mergeElement.AppendChild(mergeSubNode);
                }
            }
        }

        private XmlNode QueryForSubNode(XmlNode subNode, string strSubNodeKey, string strTargetValue)
        {
            string strQuery = string.Format(CultureInfo.InvariantCulture, "{0}:*[@{1}='{2}']", BOOTSTRAPPER_PREFIX, strSubNodeKey, strTargetValue);
            return subNode.SelectSingleNode(strQuery, _xmlNamespaceManager);
        }

        private void CorrectPackageFiles(XmlNode node)
        {
            XmlNode packageFilesNode = node.SelectSingleNode(BOOTSTRAPPER_PREFIX + ":PackageFiles", _xmlNamespaceManager);

            if (packageFilesNode != null)
            {
                // Map all StringKey attributes to corresponding String values
                XmlNodeList packageFileNodeList = node.SelectNodes("//" + BOOTSTRAPPER_PREFIX + ":*[@PackageFile]", _xmlNamespaceManager);
                foreach (XmlNode currentNode in packageFileNodeList)
                {
                    var attribute = (XmlAttribute)(currentNode.Attributes.GetNamedItem("PackageFile"));
                    string strQuery = BOOTSTRAPPER_PREFIX + ":PackageFile[@Name='" + attribute.Value + "']";
                    XmlNode packageFileNode = packageFilesNode.SelectSingleNode(strQuery, _xmlNamespaceManager);
                    if (packageFileNode != null)
                    {
                        var targetPathAttribute = (XmlAttribute)(packageFileNode.Attributes.GetNamedItem("TargetPath"));
                        attribute.Value = targetPathAttribute.Value;
                    }
                }
            }
        }

        private void ReplaceStrings(XmlNode node)
        {
            XmlNode stringsNode = node.SelectSingleNode(BOOTSTRAPPER_PREFIX + ":Strings", _xmlNamespaceManager);

            if (stringsNode != null)
            {
                string stringNodeLookupTemplate = BOOTSTRAPPER_PREFIX + ":String[@Name='{0}']";

                // The name attribute at the package level is an entry into the String table
                ReplaceAttributeString(node, "Name", stringsNode);
                ReplaceAttributeString(node, "Culture", stringsNode);

                // Homesite information is also carried in the String table
                XmlNode packageFilesNode = node.SelectSingleNode(BOOTSTRAPPER_PREFIX + ":PackageFiles", _xmlNamespaceManager);
                if (packageFilesNode != null)
                {
                    XmlNodeList packageFileNodeList = packageFilesNode.SelectNodes(BOOTSTRAPPER_PREFIX + ":PackageFile", _xmlNamespaceManager);
                    foreach (XmlNode packageFileNode in packageFileNodeList)
                    {
                        ReplaceAttributeString(packageFileNode, HOMESITE_ATTRIBUTE, stringsNode);
                    }
                }

                // Map all String attributes to corresponding String values
                //  It is currently expected that these come from either an ExitCode or FailIf
                XmlNodeList stringKeyNodeList = node.SelectNodes("//" + BOOTSTRAPPER_PREFIX + ":*[@String]", _xmlNamespaceManager);
                foreach (XmlNode currentNode in stringKeyNodeList)
                {
                    var attribute = (XmlAttribute)(currentNode.Attributes.GetNamedItem("String"));
                    XmlNode stringNode = stringsNode.SelectSingleNode(string.Format(CultureInfo.InvariantCulture, stringNodeLookupTemplate, attribute.Value), _xmlNamespaceManager);
                    if (stringNode != null)
                    {
                        AddAttribute(currentNode, "Text", stringNode.InnerText);
                    }
                    currentNode.Attributes.Remove(attribute);
                }

                // The Strings node is no longer necessary.  Remove it.
                node.RemoveChild(stringsNode);
            }
        }

        private bool BuildPackages(BuildSettings settings, XmlElement configElement, ResourceUpdater resourceUpdater, List<string> filesCopied, Dictionary<string, KeyValuePair<string, string>> eulas)
        {
            bool fSucceeded = true;

            foreach (ProductBuilder builder in settings.ProductBuilders)
            {
                if (Validate && !builder.Product.ValidationPassed)
                {
                    if (_results != null)
                    {
                        _results.AddMessage(BuildMessage.CreateMessage(BuildMessageSeverity.Warning, "GenerateBootstrapper.ProductValidation", builder.Name, builder.Product.ValidationResults.FilePath));
                        foreach (string validationMessage in builder.Product.ValidationResults.ValidationErrors)
                        {
                            _results.AddMessage(BuildMessage.CreateMessage(BuildMessageSeverity.Warning, "GenerateBootstrapper.ValidationError", builder.Product.ValidationResults.FilePath, validationMessage));
                        }
                        foreach (string validationMessage in builder.Product.ValidationResults.ValidationWarnings)
                        {
                            _results.AddMessage(BuildMessage.CreateMessage(BuildMessageSeverity.Warning, "GenerateBootstrapper.ValidationWarning", builder.Product.ValidationResults.FilePath, validationMessage));
                        }
                    }
                }
                Package package = GetPackageForSettings(settings, builder, _results);
                if (package == null)
                {
                    // GetPackage should have already added the correct message info
                    continue;
                }

                if (Validate && !package.ValidationPassed)
                {
                    if (_results != null)
                    {
                        _results.AddMessage(BuildMessage.CreateMessage(BuildMessageSeverity.Warning, "GenerateBootstrapper.PackageValidation", builder.Name, package.ValidationResults.FilePath));
                        foreach (string validationMessage in package.ValidationResults.ValidationErrors)
                        {
                            _results.AddMessage(BuildMessage.CreateMessage(BuildMessageSeverity.Warning, "GenerateBootstrapper.ValidationError", package.ValidationResults.FilePath, validationMessage));
                        }
                        foreach (string validationMessage in package.ValidationResults.ValidationWarnings)
                        {
                            _results.AddMessage(BuildMessage.CreateMessage(BuildMessageSeverity.Warning, "GenerateBootstrapper.ValidationWarning", package.ValidationResults.FilePath, validationMessage));
                        }
                    }
                }

                XmlNode node = package.Node;
                // Copy the files for this package to the output directory
                XmlAttribute eulaAttribute = node.Attributes[EULA_ATTRIBUTE];
                XmlNodeList packageFileNodes = node.SelectNodes(BOOTSTRAPPER_PREFIX + ":PackageFiles/" + BOOTSTRAPPER_PREFIX + ":PackageFile", _xmlNamespaceManager);
                XmlNode installChecksNode = node.SelectSingleNode(BOOTSTRAPPER_PREFIX + ":InstallChecks", _xmlNamespaceManager);
                foreach (XmlNode packageFileNode in packageFileNodes)
                {
                    var packageFileSource = (XmlAttribute)(packageFileNode.Attributes.GetNamedItem("SourcePath"));
                    var packageFileDestination = (XmlAttribute)(packageFileNode.Attributes.GetNamedItem("TargetPath"));
                    var packageFileName = (XmlAttribute)(packageFileNode.Attributes.GetNamedItem("Name"));
                    var packageFileCopy = (XmlAttribute)(packageFileNode.Attributes.GetNamedItem("CopyOnBuild"));
                    if (packageFileSource != null && !String.IsNullOrEmpty(eulaAttribute?.Value) && packageFileSource.Value == eulaAttribute.Value)
                    {
                        // need to remove EULA from the package file list
                        XmlNode packageFilesNode = node.SelectSingleNode(BOOTSTRAPPER_PREFIX + ":PackageFiles", _xmlNamespaceManager);
                        packageFilesNode.RemoveChild(packageFileNode);
                        continue;
                    }

                    if ((packageFileSource != null) && (packageFileDestination != null) &&
                        (packageFileName != null))
                    {
                        // Calculate the hash of this file and add it to the PackageFileNode
                        if (!AddVerificationInformation(
                            packageFileNode,
                            packageFileSource.Value,
                            packageFileName.Value,
                            builder,
                            settings,
                            _results))
                        {
                            fSucceeded = false;
                        }
                    }

                    if ((packageFileSource != null) && (packageFileDestination != null) &&
                        ((packageFileCopy == null) || (!String.Equals(packageFileCopy.Value, "False", StringComparison.InvariantCulture))))
                    {
                        // if this is the key for an external check, we will add it to the Resource Updater instead of copying the file
                        XmlNode subNode = null;
                        if ((installChecksNode != null) && (packageFileName != null))
                        {
                            subNode = QueryForSubNode(installChecksNode, "PackageFile", packageFileName.Value);
                        }
                        if (subNode != null)
                        {
                            if (resourceUpdater != null)
                            {
                                if (!FileSystems.Default.FileExists(packageFileSource.Value))
                                {
                                    _results?.AddMessage(BuildMessage.CreateMessage(BuildMessageSeverity.Error, "GenerateBootstrapper.PackageResourceFileNotFound", packageFileSource.Value, builder.Name));
                                    fSucceeded = false;
                                    continue;
                                }
                                resourceUpdater.AddFileResource(packageFileSource.Value, packageFileDestination.Value);
                            }
                        }
                        else
                        {
                            if (settings.ComponentsLocation == ComponentsLocation.Relative || !VerifyHomeSiteInformation(packageFileNode, builder, settings, _results))
                            {
                                if (settings.CopyComponents)
                                {
                                    string strDestinationFileName = System.IO.Path.Combine(settings.OutputPath, packageFileDestination.Value);
                                    try
                                    {
                                        if (!FileSystems.Default.FileExists(packageFileSource.Value))
                                        {
                                            _results?.AddMessage(BuildMessage.CreateMessage(BuildMessageSeverity.Error, "GenerateBootstrapper.PackageFileNotFound", packageFileDestination.Value, builder.Name));
                                            fSucceeded = false;
                                            continue;
                                        }
                                        EnsureFolderExists(System.IO.Path.GetDirectoryName(strDestinationFileName));
                                        File.Copy(packageFileSource.Value, strDestinationFileName, true);
                                        ClearReadOnlyAttribute(strDestinationFileName);
                                    }
                                    catch (UnauthorizedAccessException ex)
                                    {
                                        _results?.AddMessage(BuildMessage.CreateMessage(BuildMessageSeverity.Error, "GenerateBootstrapper.CopyPackageError", packageFileSource.Value, builder.Name, ex.Message));
                                        fSucceeded = false;
                                        continue;
                                    }
                                    catch (IOException ex)
                                    {
                                        _results?.AddMessage(BuildMessage.CreateMessage(BuildMessageSeverity.Error, "GenerateBootstrapper.CopyPackageError", packageFileSource.Value, builder.Name, ex.Message));
                                        fSucceeded = false;
                                        continue;
                                    }
                                    catch (ArgumentException ex)
                                    {
                                        _results?.AddMessage(BuildMessage.CreateMessage(BuildMessageSeverity.Error, "GenerateBootstrapper.CopyPackageError", packageFileSource.Value, builder.Name, ex.Message));
                                        fSucceeded = false;
                                        continue;
                                    }
                                    catch (NotSupportedException ex)
                                    {
                                        _results?.AddMessage(BuildMessage.CreateMessage(BuildMessageSeverity.Error, "GenerateBootstrapper.CopyPackageError", packageFileSource.Value, builder.Name, ex.Message));
                                        fSucceeded = false;
                                        continue;
                                    }
                                    filesCopied.Add(strDestinationFileName);
                                }
                                else
                                {
                                    filesCopied.Add(packageFileSource.Value);
                                }

                                // Add the file size to the PackageFileNode
                                XmlAttribute sizeAttribute = packageFileNode.OwnerDocument.CreateAttribute("Size");
                                var fi = new FileInfo(packageFileSource.Value);
                                sizeAttribute.Value = "" + (fi.Length.ToString(CultureInfo.InvariantCulture));
                                MergeAttribute(packageFileNode, sizeAttribute);
                            }
                        }
                    }
                }
                // Add the Eula attribute correctly
                if (eulas != null && !String.IsNullOrEmpty(eulaAttribute?.Value))
                {
                    if (FileSystems.Default.FileExists(eulaAttribute.Value))
                    {
                        string key = GetFileHash(eulaAttribute.Value);
                        if (eulas.TryGetValue(key, out KeyValuePair<string, string> eulaInfo))
                        {
                            eulaAttribute.Value = eulaInfo.Key;
                        }
                        else
                        {
                            string configFileKey = string.Format(CultureInfo.InvariantCulture, "EULA{0}", eulas.Count);
                            var de = new KeyValuePair<string ,string>(configFileKey, eulaAttribute.Value);
                            eulas[key] = de;
                            eulaAttribute.Value = configFileKey;
                        }
                    }
                    else
                    {
                        _results?.AddMessage(BuildMessage.CreateMessage(BuildMessageSeverity.Error, "GenerateBootstrapper.PackageResourceFileNotFound", eulaAttribute.Value, builder.Name));
                        fSucceeded = false;
                        continue;
                    }
                }
                // Write the package node
                if (configElement != null)
                {
                    configElement.AppendChild(configElement.OwnerDocument.ImportNode(node, true));
                    DumpXmlToFile(node, string.Format(CultureInfo.CurrentCulture, "{0}.{1}.xml", package.Product.ProductCode, package.Culture));
                }
            }

            return fSucceeded;
        }

        private XmlNode CreateProductNode(XmlNode node)
        {
            // create a new Product node for the passed-in product
            XmlNode productNode = _document.CreateElement("Product", BOOTSTRAPPER_NAMESPACE);

            // find the ProductCode attribute
            var sourceAttribute = (XmlAttribute)(node.Attributes.GetNamedItem("ProductCode"));
            Debug.Assert(sourceAttribute != null, "we should not be here if there is no ProductCode attribute");

            AddAttribute(productNode, "ProductCode", sourceAttribute.Value);

            node.Attributes.Remove(sourceAttribute);

            return productNode;
        }

        private static string ReadAttribute(XmlNode node, string strAttributeName)
        {
            var attribute = (XmlAttribute)(node.Attributes.GetNamedItem(strAttributeName));

            return attribute?.Value;
        }

        private static void EnsureFolderExists(string strFolderPath)
        {
            if (!FileSystems.Default.DirectoryExists(strFolderPath))
            {
                Directory.CreateDirectory(strFolderPath);
            }
        }

        private static void ClearReadOnlyAttribute(string strFileName)
        {
            FileAttributes attribs = File.GetAttributes(strFileName);
            if ((attribs & FileAttributes.ReadOnly) != 0)
            {
                attribs &= (~FileAttributes.ReadOnly);
                File.SetAttributes(strFileName, attribs);
            }
        }

        private static string ByteArrayToString(byte[] byteArray)
        {
            if (byteArray == null) return null;

            var output = new StringBuilder(byteArray.Length);
            foreach (byte byteValue in byteArray)
            {
                output.Append(byteValue.ToString("X02", CultureInfo.InvariantCulture));
            }

            return output.ToString();
        }

        private static string GetFileHash(string filePath)
        {
            var fi = new FileInfo(filePath);
            String retVal;

            // Bootstrapper is always signed with the SHA-256 algorithm, no matter which version of
            // the .NET Framework we are targeting.  In ideal situations, bootstrapper files will be
            // pre-signed anwyay; this is a fallback in case we ever encounter a bootstrapper that is
            // not signed.  
            System.Security.Cryptography.SHA256CryptoServiceProvider sha = new System.Security.Cryptography.SHA256CryptoServiceProvider();

            using (Stream s = fi.OpenRead())
            {
                retVal = ByteArrayToString(sha.ComputeHash(s));
            }
            return retVal;
        }

        private void ReplaceAttributeString(XmlNode node, string attributeName, XmlNode stringsNode)
        {
            string stringNodeLookupTemplate = BOOTSTRAPPER_PREFIX + ":String[@Name='{0}']";
            var attribute = (XmlAttribute)(node.Attributes.GetNamedItem(attributeName));
            if (attribute != null)
            {
                XmlNode stringNode = stringsNode.SelectSingleNode(string.Format(CultureInfo.InvariantCulture, stringNodeLookupTemplate, attribute.Value), _xmlNamespaceManager);
                if (stringNode != null)
                {
                    attribute.Value = stringNode.InnerText;
                }
            }
        }

        private Package GetPackageForSettings(BuildSettings settings, ProductBuilder builder, BuildResults results)
        {
            CultureInfo ci = Util.GetCultureInfoFromString(settings.Culture);
            CultureInfo fallbackCI = Util.GetCultureInfoFromString(settings.FallbackCulture);
            Package package;

            if (builder.Product.Packages.Count == 0)
            {
                results?.AddMessage(BuildMessage.CreateMessage(BuildMessageSeverity.Error, "GenerateBootstrapper.ProductCultureNotFound", builder.Name));
                return null;
            }

            if (ci != null)
            {
                package = builder.Product.Packages.Package(ci.Name);
                if (package != null) return package;

                // Target culture not found?  Go through the progression of parent cultures (up until but excluding the invariant culture) -> fallback culture -> parent fallback culture -> default culture -> parent default culture -> any culture available
                // Note: there is no warning if the parent culture of the requested culture is found
                CultureInfo parentCulture = ci.Parent;

                // Keep going up the chain of parents, stopping at the invariant culture
                while (parentCulture != CultureInfo.InvariantCulture)
                {
                    package = GetPackageForSettings_Helper(ci, parentCulture, builder, results, false);
                    if (package != null) return package;

                    parentCulture = parentCulture.Parent;
                }
            }

            if (fallbackCI != null)
            {
                package = GetPackageForSettings_Helper(ci, fallbackCI, builder, results, true);
                if (package != null) return package;

                if (!fallbackCI.IsNeutralCulture)
                {
                    package = GetPackageForSettings_Helper(ci, fallbackCI.Parent, builder, results, true);
                    if (package != null) return package;
                }
            }

            package = GetPackageForSettings_Helper(ci, Util.DefaultCultureInfo, builder, results, true);
            if (package != null) return package;

            if (!Util.DefaultCultureInfo.IsNeutralCulture)
            {
                package = GetPackageForSettings_Helper(ci, Util.DefaultCultureInfo.Parent, builder, results, true);
                if (package != null) return package;
            }

            if (results != null && ci != null)
                results.AddMessage(BuildMessage.CreateMessage(BuildMessageSeverity.Warning, "GenerateBootstrapper.UsingProductCulture", ci.Name, builder.Name, builder.Product.Packages.Item(0).Culture));
            return builder.Product.Packages.Item(0);
        }

        private static Package GetPackageForSettings_Helper(CultureInfo culture, CultureInfo altCulture, ProductBuilder builder, BuildResults results, bool fShowWarning)
        {
            if (altCulture == null)
            {
                return null;
            }
            Package package = builder.Product.Packages.Package(altCulture.Name);
            if (package != null)
            {
                if (fShowWarning && culture != null)
                {
                    results?.AddMessage(BuildMessage.CreateMessage(BuildMessageSeverity.Warning, "GenerateBootstrapper.UsingProductCulture", culture.Name, builder.Name, altCulture.Name));
                }
                return package;
            }
            return null;
        }

        private bool BuildResources(BuildSettings settings, ResourceUpdater resourceUpdater)
        {
            if (_cultures.Count == 0)
            {
                _results?.AddMessage(BuildMessage.CreateMessage(BuildMessageSeverity.Error, "GenerateBootstrapper.NoResources"));
                return false;
            }

            int codePage = -1;
            XmlNode resourcesNode = GetResourcesNodeForSettings(settings, _results, ref codePage);
            XmlNode stringsNode = resourcesNode.SelectSingleNode("Strings");
            XmlNode fontsNode = resourcesNode.SelectSingleNode("Fonts");

            if (stringsNode == null)
            {
                _results?.AddMessage(BuildMessage.CreateMessage(BuildMessageSeverity.Error, "GenerateBootstrapper.NoStringsForCulture", resourcesNode.Attributes.GetNamedItem("Culture").Value));
                return false;
            }

            XmlNodeList stringNodes = stringsNode.SelectNodes("String");

            foreach (XmlNode stringNode in stringNodes)
            {
                XmlAttribute resourceIdAttribute = (XmlAttribute)stringNode.Attributes.GetNamedItem("Name");

                if (resourceIdAttribute != null)
                {
                    resourceUpdater.AddStringResource(MESSAGE_TABLE, resourceIdAttribute.Value.ToUpper(CultureInfo.InvariantCulture), stringNode.InnerText);
                }
            }

            if (fontsNode != null)
            {
                foreach (XmlNode fontNode in fontsNode.SelectNodes("Font"))
                {
                    ConvertChildsNodeToAttributes(fontNode);
                }
                string fontsConfig = XmlToConfigurationFile(fontsNode);
                resourceUpdater.AddStringResource(RESOURCE_TABLE, "SETUPRES", fontsConfig);
                DumpXmlToFile(fontsNode, "fonts.cfg.xml");
                DumpStringToFile(fontsConfig, "fonts.cfg", false);
                if (codePage != -1)
                {
                    resourceUpdater.AddStringResource(RESOURCE_TABLE, "CODEPAGE", codePage.ToString(CultureInfo.InvariantCulture));
                }
            }
            return true;
        }

        private XmlNode GetResourcesNodeForSettings(BuildSettings settings, BuildResults results, ref int codepage)
        {
            CultureInfo ci = Util.GetCultureInfoFromString(settings.Culture);
            CultureInfo fallbackCI = Util.GetCultureInfoFromString(settings.FallbackCulture);
            XmlNode cultureNode;
            
            if (ci != null)
            {
                // Work through the progression of parent cultures (up until but excluding the invariant culture) -> fallback culture -> parent fallback culture -> default culture -> parent default culture -> any available culture
                cultureNode = GetResourcesNodeForSettings_Helper(ci, ci, results, ref codepage, false);
                if (cultureNode != null) return cultureNode;
                CultureInfo parentCulture = ci.Parent;

                // Keep going up the chain of parents, stopping at the invariant culture
                while (parentCulture != CultureInfo.InvariantCulture)
                {
                    cultureNode = GetResourcesNodeForSettings_Helper(ci, parentCulture, results, ref codepage, false);
                    if (cultureNode != null) return cultureNode;

                    parentCulture = parentCulture.Parent;
                }
            }

            if (fallbackCI != null)
            {
                cultureNode = GetResourcesNodeForSettings_Helper(ci, fallbackCI, results, ref codepage, true);
                if (cultureNode != null) return cultureNode;

                if (!fallbackCI.IsNeutralCulture)
                {
                    cultureNode = GetResourcesNodeForSettings_Helper(ci, fallbackCI.Parent, results, ref codepage, true);
                    if (cultureNode != null) return cultureNode;
                }
            }

            cultureNode = GetResourcesNodeForSettings_Helper(ci, Util.DefaultCultureInfo, results, ref codepage, true);
            if (cultureNode != null) return cultureNode;

            if (!Util.DefaultCultureInfo.IsNeutralCulture)
            {
                cultureNode = GetResourcesNodeForSettings_Helper(ci, Util.DefaultCultureInfo.Parent, results, ref codepage, true);
                if (cultureNode != null) return cultureNode;
            }

            KeyValuePair<string, XmlNode> altCulturePair = _cultures.FirstOrDefault();
            if (ci != null)
            {
                results?.AddMessage(BuildMessage.CreateMessage(BuildMessageSeverity.Warning, "GenerateBootstrapper.UsingResourcesCulture", ci.Name, altCulturePair.Key));
            }
            GetCodePage(altCulturePair.Key, ref codepage);
            return altCulturePair.Value;
        }

        private XmlNode GetResourcesNodeForSettings_Helper(CultureInfo culture, CultureInfo altCulture, BuildResults results, ref int codepage, bool fShowWarning)
        {
            if (altCulture != null && _cultures.TryGetValue(altCulture.Name, out XmlNode cultureNode))
            {
                if (fShowWarning && culture != null)
                {
                    results?.AddMessage(BuildMessage.CreateMessage(BuildMessageSeverity.Warning, "GenerateBootstrapper.UsingResourcesCulture", culture.Name, altCulture.Name));
                }

                codepage = altCulture.TextInfo.ANSICodePage;
                return cultureNode;
            }

            return null;
        }

        private static void GetCodePage(string culture, ref int codePage)
        {
            try
            {
                var info = new CultureInfo(culture);
                codePage = info.TextInfo.ANSICodePage;
            }
            catch (ArgumentException ex)
            {
                Debug.Fail(ex.Message);
            }
        }

        private void ReplacePackageFileAttributes(XmlNode targetNodes, string targetAttribute, XmlNode sourceNodes, string sourceSubNodeName, string sourceOldName, string sourceNewName)
        {
            XmlNodeList sourceNodeList = sourceNodes.SelectNodes(BOOTSTRAPPER_PREFIX + ":" + sourceSubNodeName, _xmlNamespaceManager);

            foreach (XmlNode sourceNode in sourceNodeList)
            {
                XmlAttribute oldNameAttribute = (XmlAttribute)(sourceNode.Attributes.GetNamedItem(sourceOldName));
                XmlAttribute newNameAttribute = (XmlAttribute)(sourceNode.Attributes.GetNamedItem(sourceNewName));

                if (oldNameAttribute != null && newNameAttribute != null)
                {
                    ReplaceAttributes(targetNodes, targetAttribute, oldNameAttribute.Value, newNameAttribute.Value);
                }
            }
        }

        private static XmlElement CreateApplicationElement(XmlElement configElement, BuildSettings settings)
        {
            XmlElement applicationElement = null;

            if (!String.IsNullOrEmpty(settings.ApplicationName) || !String.IsNullOrEmpty(settings.ApplicationFile))
            {
                applicationElement = configElement.OwnerDocument.CreateElement("Application");
                if (!String.IsNullOrEmpty(settings.ApplicationName))
                {
                    AddAttribute(applicationElement, "Name", settings.ApplicationName);
                }
                AddAttribute(applicationElement, "RequiresElevation", settings.ApplicationRequiresElevation ? "true" : "false");

                if (!String.IsNullOrEmpty(settings.ApplicationFile))
                {
                    XmlElement filesNode = applicationElement.OwnerDocument.CreateElement("Files");
                    XmlElement fileNode = filesNode.OwnerDocument.CreateElement("File");
                    AddAttribute(fileNode, "Name", settings.ApplicationFile);
                    AddAttribute(fileNode, URLNAME_ATTRIBUTE, Uri.EscapeDataString(settings.ApplicationFile));
                    filesNode.AppendChild(fileNode);
                    applicationElement.AppendChild(filesNode);
                }
            }
            return applicationElement;
        }

        private static void AddAttribute(XmlNode node, string attributeName, string attributeValue)
        {
            XmlAttribute attrib = node.OwnerDocument.CreateAttribute(attributeName);
            attrib.Value = attributeValue;
            node.Attributes.Append(attrib);
        }

        [SuppressMessage("Microsoft.Security.Xml", "CA3073: ReviewTrustedXsltUse.", Justification = "Input style sheet comes from our own assemblies. Hence it is a trusted source.")]
        [SuppressMessage("Microsoft.Security.Xml", "CA3059: UseXmlReaderForXPathDocument.", Justification = "Input style sheet comes from our own assemblies. Hence it is a trusted source.")]
        [SuppressMessage("Microsoft.Security.Xml", "CA3052: UseXmlResolver.", Justification = "Input style sheet comes from our own assemblies. Hence it is a trusted source.")]
        public static string XmlToConfigurationFile(XmlNode input)
        {
            using (var reader = new XmlNodeReader(input))
            {
                Stream s = GetEmbeddedResourceStream(CONFIG_TRANSFORM);
                var d = new XPathDocument(s);
                var xslc = new XslCompiledTransform();
                // Using the Trusted Xslt is fine as the style sheet comes from our own assembly.
                xslc.Load(d, XsltSettings.TrustedXslt, new XmlUrlResolver());

                var xml = new XPathDocument(reader);

                using (var m = new MemoryStream())
                {
                    using (var w = new StreamWriter(m))
                    {
                        xslc.Transform(xml, null, w);

                        w.Flush();
                        m.Position = 0;

                        using (StreamReader r = new StreamReader(m))
                        {
                            // HACKHACK
                            string str = r.ReadToEnd();
                            return str.Replace("%NEWLINE%", Environment.NewLine);
                        }
                    }
                }
            }
        }

        private static Stream GetEmbeddedResourceStream(string name)
        {
            Assembly a = Assembly.GetExecutingAssembly();
            Stream s = a.GetManifestResourceStream(String.Format(CultureInfo.InvariantCulture, "{0}.{1}", typeof(BootstrapperBuilder).Namespace, name));
            Debug.Assert(s != null, String.Format(CultureInfo.CurrentCulture, "EmbeddedResource '{0}' not found", name));
            return s;
        }

        private static void DumpXmlToFile(XmlNode node, string fileName)
        {
            if (s_logging)
            {
                try
                {
                    using (var xmlwriter = new XmlTextWriter(System.IO.Path.Combine(s_logPath, fileName), Encoding.UTF8))
                    {
                        xmlwriter.Formatting = Formatting.Indented;
                        xmlwriter.Indentation = 4;
                        xmlwriter.WriteNode(new XmlNodeReader(node), true);
                    }
                }
                catch (IOException)
                {
                    // can't write info to a log file?  This is a trouble-shooting helper only, and 
                    // this exception can be ignored
                }
                catch (UnauthorizedAccessException)
                {
                    // can't write info to a log file?  This is a trouble-shooting helper only, and 
                    // this exception can be ignored
                }
                catch (ArgumentException)
                {
                    // can't write info to a log file?  This is a trouble-shooting helper only, and 
                    // this exception can be ignored
                }
                catch (NotSupportedException)
                {
                    // can't write info to a log file?  This is a trouble-shooting helper only, and 
                    // this exception can be ignored
                }
                catch (XmlException)
                {
                    // can't write info to a log file?  This is a trouble-shooting helper only, and 
                    // this exception can be ignored
                }
            }
        }

        private static void DumpStringToFile(string text, string fileName, bool append)
        {
            if (s_logging)
            {
                try
                {
                    using (var fileWriter = new StreamWriter(System.IO.Path.Combine(s_logPath, fileName), append))
                    {
                        fileWriter.Write(text);
                    }
                }
                catch (IOException)
                {
                    // can't write info to a log file?  This is a trouble-shooting helper only, and 
                    // this exception can be ignored
                }
                catch (UnauthorizedAccessException)
                {
                    // can't write info to a log file?  This is a trouble-shooting helper only, and 
                    // this exception can be ignored
                }
                catch (ArgumentException)
                {
                    // can't write info to a log file?  This is a trouble-shooting helper only, and 
                    // this exception can be ignored
                }
                catch (NotSupportedException)
                {
                    // can't write info to a log file?  This is a trouble-shooting helper only, and 
                    // this exception can be ignored
                }
            }
        }

        private static bool VerifyHomeSiteInformation(XmlNode packageFileNode, ProductBuilder builder, BuildSettings settings, BuildResults results)
        {
            if (settings.ComponentsLocation != ComponentsLocation.HomeSite)
            {
                return true;
            }

            XmlAttribute homesiteAttribute = packageFileNode.Attributes[HOMESITE_ATTRIBUTE];

            if (homesiteAttribute == null && builder.Product.CopyAllPackageFiles != CopyAllFilesType.CopyAllFilesIfNotHomeSite)
            {
                results?.AddMessage(BuildMessage.CreateMessage(BuildMessageSeverity.Warning, "GenerateBootstrapper.PackageHomeSiteMissing", builder.Name));
                return false;
            }

            return true;
        }

        private bool AddVerificationInformation(XmlNode packageFileNode, string fileSource, string fileName, ProductBuilder builder, BuildSettings settings, BuildResults results)
        {
            XmlAttribute hashAttribute = packageFileNode.Attributes[HASH_ATTRIBUTE];
            XmlAttribute publicKeyAttribute = packageFileNode.Attributes[PUBLICKEY_ATTRIBUTE];

            if (FileSystems.Default.FileExists(fileSource))
            {
                string publicKey = GetPublicKeyOfFile(fileSource);
                if (hashAttribute == null && publicKeyAttribute == null)
                {
                    // If neither the Hash nor PublicKey attributes were specified in the manifest, add it
                    if (publicKey != null)
                    {
                        AddAttribute(packageFileNode, PUBLICKEY_ATTRIBUTE, publicKey);
                    }
                    else
                    {
                        AddAttribute(packageFileNode, HASH_ATTRIBUTE, GetFileHash(fileSource));
                    }
                }
                if (publicKeyAttribute != null)
                {
                    // Always use the PublicKey of the file on disk
                    if (publicKey != null)
                    {
                        ReplaceAttribute(packageFileNode, PUBLICKEY_ATTRIBUTE, publicKey);
                    }
                    else
                    {
                        // File on disk is not signed.  Remove the public key info, and make sure the hash is written instead
                        packageFileNode.Attributes.RemoveNamedItem(PUBLICKEY_ATTRIBUTE);
                        if (hashAttribute == null)
                        {
                            AddAttribute(packageFileNode, HASH_ATTRIBUTE, GetFileHash(fileSource));
                        }
                    }

                    // If the public key in the file doesn't match the public key on disk, issue a build warning
                    if (publicKey?.Equals(publicKeyAttribute.Value, StringComparison.OrdinalIgnoreCase) == false)
                    {
                        results?.AddMessage(BuildMessage.CreateMessage(BuildMessageSeverity.Warning, "GenerateBootstrapper.DifferingPublicKeys", PUBLICKEY_ATTRIBUTE, builder.Name, fileSource));
                    }
                }
                if (hashAttribute != null)
                {
                    string fileHash = GetFileHash(fileSource);

                    // Always use the Hash of the file on disk
                    ReplaceAttribute(packageFileNode, HASH_ATTRIBUTE, fileHash);

                    // If the public key in the file doesn't match the public key on disk, issue a build warning
                    if (!fileHash.Equals(hashAttribute.Value, StringComparison.OrdinalIgnoreCase))
                    {
                        results?.AddMessage(BuildMessage.CreateMessage(BuildMessageSeverity.Warning, "GenerateBootstrapper.DifferingPublicKeys", "Hash", builder.Name, fileSource));
                    }
                }
            }
            else if (settings.ComponentsLocation == ComponentsLocation.HomeSite)
            {
                if (hashAttribute == null && publicKeyAttribute == null)
                {
                    results?.AddMessage(BuildMessage.CreateMessage(BuildMessageSeverity.Error, "GenerateBootstrapper.MissingVerificationInformation", fileName, builder.Name));
                    return false;
                }
            }

            return true;
        }

        private static string GetPublicKeyOfFile(string fileSource)
        {
            if (FileSystems.Default.FileExists(fileSource))
            {
                try
                {
                    var cert = new X509Certificate(fileSource);
                    string publicKey = cert.GetPublicKeyString();
                    return publicKey;
                }
                catch (System.Security.Cryptography.CryptographicException)
                {
                    // This just means the file is not signed.
                }
            }

            return null;
        }

        private static void ConvertChildsNodeToAttributes(XmlNode node)
        {
            XmlNode childNode = node.FirstChild;
            while (childNode != null)
            {
                // Need to get the next child node now because when the current node is removed, the NextSibling
                // will be null
                XmlNode currentNode = childNode;
                childNode = currentNode.NextSibling;
                if (currentNode.Attributes.Count == 0 && currentNode.InnerText.Length > 0)
                {
                    AddAttribute(node, currentNode.Name, currentNode.InnerText);
                    node.RemoveChild(currentNode);
                }
            }
        }

        private static string GetLogPath()
        {
            if (!s_logging) return null;
            string logPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Microsoft\VisualStudio\" + VisualStudioConstants.CurrentVisualStudioVersion + @"\VSPLOG");
            Directory.CreateDirectory(logPath);
            return logPath;
        }

        private static Dictionary<string, Product> GetIncludedProducts(Product product)
        {
            var includedProducts = new Dictionary<string, Product>(StringComparer.OrdinalIgnoreCase)
            {
                // Add in this product in case there is a circular includes: 
                // we won't continue to explore this product.  It will be removed later.
                { product.ProductCode, product }
            };

            // Recursively add included products 
            foreach (Product p in product.Includes)
            {
                AddIncludedProducts(p, includedProducts);
            }

            includedProducts.Remove(product.ProductCode);
            return includedProducts;
        }

        private static void AddIncludedProducts(Product product, Dictionary<string, Product> includedProducts)
        {
            if (!includedProducts.ContainsKey(product.ProductCode))
            {
                includedProducts.Add(product.ProductCode, product);
                foreach (Product p in product.Includes)
                {
                    AddIncludedProducts(p, includedProducts);
                }
            }
        }

        private static string MapLCIDToCultureName(int lcid)
        {
            if (lcid == 0)
            {
                return Util.DefaultCultureInfo.Name;
            }

            try
            {
                var ci = new CultureInfo(lcid);
                return ci.Name;
            }
            catch (ArgumentException)
            {
                // Can't convert this lcid to a CultureInfo?  Just return the default CultureInfo instead...
                return Util.DefaultCultureInfo.Name;
            }
        }
    }
}
