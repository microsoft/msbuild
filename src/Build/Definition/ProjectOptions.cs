using System.Collections.Generic;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Evaluation.Context;
using Microsoft.Build.FileSystem;

namespace Microsoft.Build.Definition
{
    /// <summary>
    ///     Common <see cref="Project" /> constructor arguments.
    /// </summary>
    public class ProjectOptions
    {
        /// <summary>
        /// Global properties to evaluate with.
        /// </summary>
        public IDictionary<string, string> GlobalProperties { get; set; }

        /// <summary>
        /// Tools version to evaluate with
        /// </summary>
        public string ToolsVersion { get; set; }

        /// <summary>
        /// Sub-toolset version to explicitly evaluate the toolset with.
        /// </summary>
        public string SubToolsetVersion { get; set; }

        /// <summary>
        /// The <see cref="ProjectCollection"/> the project is added to. Default is <see cref="ProjectCollection.GlobalProjectCollection"/>/>
        /// </summary>
        public ProjectCollection ProjectCollection { get; set; }

        /// <summary>
        /// The <see cref="ProjectLoadSettings"/> to use for evaluation.
        /// </summary>
        public ProjectLoadSettings LoadSettings { get; set; } = ProjectLoadSettings.Default;

        /// <summary>
        /// The <see cref="EvaluationContext"/> to use for evaluation.
        /// </summary>
        public EvaluationContext EvaluationContext { get; set; }

        /// <summary>
        /// Provides <see cref="IDirectoryCache"/> to be used for evaluation.
        /// </summary>
        public IDirectoryCacheFactory DirectoryCacheFactory { get; set; }
    }
}
