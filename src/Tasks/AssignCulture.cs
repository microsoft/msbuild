// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using Microsoft.Build.Collections;
#if DEBUG
using System.Diagnostics;
#endif
using Microsoft.Build.Framework;
using Microsoft.Build.Shared;
using Microsoft.Build.Utilities;

#nullable disable

namespace Microsoft.Build.Tasks
{
    /// <summary>
    /// Class: AssignCulture
    ///
    /// This task takes a list of resource file names and sets an attribute that
    /// contains the culture name embedded in the file name:
    ///
    ///      MyResources.fr.resx     ==> Culture='fr'
    ///
    /// The task can also return a list of "Culture-neutral" file names, like:
    ///
    ///      MyGlyph.fr.bmp          ==> MyGlyph.bmp [Culture='fr']
    ///
    /// This is because embedded resources are referred to this way.
    ///
    /// There are plenty of corner cases with this task. See the unit test for
    /// more details.
    /// </summary>
    public class AssignCulture : TaskExtension
    {
        #region Properties

        /// <summary>
        /// The incoming list of files to assign a culture to.
        /// </summary>
        [Required]
        public ITaskItem[] Files { get; set; } = Array.Empty<ITaskItem>();

        /// <summary>
        /// If the flag set to 'true' the incoming list with existing Culture metadata will not be ammended and CultureNeutralAssignedFiles filename will be equal to the original.
        /// In case the Culture metadata was not provided, the logic of RespectAlreadyAssignedItemCulture will not take any effect.
        /// </summary>
        public bool RespectAlreadyAssignedItemCulture { get; set; } = false;

        /// <summary>
        /// If the flag set to 'true' the task will log a warning when the culture metadata is overwritten by the task.
        /// </summary>
        public bool WarnOnCultureOverwritten { get; set; } = false;

        /// <summary>
        /// This outgoing list of files is exactly the same as the incoming Files
        /// list except that an attribute name "Culture" will have been added if
        /// the particular file name is in the form:
        ///
        ///      MyResource.&lt;any-valid-culture-id&gt;.resx
        ///
        /// The value of Culture will be "&lt;any-valid-culture-id&gt;".
        ///
        /// If the incoming item from Files already has a Culture attribute then
        /// that original attribute is used instead.
        /// </summary>
        [Output]
        public ITaskItem[] AssignedFiles { get; private set; }

        /// <summary>
        /// This is a subset of AssignedFiles that has all of the items that
        /// ended up have a Culture assigned to them. This includes items that
        /// already had a Culture in the incoming Files list as well as items
        /// that were assigned a Culture because they had a valid culture ID
        /// embedded in their file name.
        ///
        /// The following is always true:
        ///
        ///      AssignedFiles = AssignedFilesWithCulture + AssignedFilesWithNoCulture
        /// </summary>
        [Output]
        public ITaskItem[] AssignedFilesWithCulture { get; private set; }

        /// <summary>
        /// This is a subset of AssignedFiles that has all of the items that
        /// ended up with no Culture assigned to them.
        ///
        /// The following is always true:
        ///
        ///      AssignedFiles = AssignedFilesWithCulture + AssignedFilesWithNoCulture
        /// </summary>
        [Output]
        public ITaskItem[] AssignedFilesWithNoCulture { get; private set; }

        /// <summary>
        /// This list has the same number of items as the Files list or the
        /// AssignedFiles list.
        ///
        /// Items in this list have the file name from Files or AssignedFiles
        /// but with the culture stripped if it was embedded in the file name.
        ///
        /// So for example, if the incoming item in Files was:
        ///
        ///      MyBitmap.fr.bmp
        ///
        /// then the corresponding file in CultureNeutralAssignedFiles will be:
        ///
        ///      MyBitmap.bmp
        ///
        /// The culture will only be stripped if it is a valid culture identifier.
        /// So for example,
        ///
        ///      MyDifferentFile.XX.txt
        ///
        /// will result in exactly the same file name:
        ///
        ///      MyDifferentFile.XX.txt
        ///
        /// because 'XX' is not a valid culture identifier.
        /// </summary>
        [Output]
        public ITaskItem[] CultureNeutralAssignedFiles { get; private set; }

        #endregion

        #region ITask Members

        /// <summary>
        /// Execute.
        /// </summary>
        /// <returns></returns>
        public override bool Execute()
        {
            AssignedFiles = new ITaskItem[Files.Length];
            CultureNeutralAssignedFiles = new ITaskItem[Files.Length];
            var cultureList = new List<ITaskItem>();
            var noCultureList = new List<ITaskItem>();

            bool retValue = true;

            for (int i = 0; i < Files.Length; ++i)
            {
                try
                {
                    AssignedFiles[i] = new TaskItem(Files[i]);

                    string dependentUpon = AssignedFiles[i].GetMetadata(ItemMetadataNames.dependentUpon);
                    string existingCulture = AssignedFiles[i].GetMetadata(ItemMetadataNames.culture);

                    if (RespectAlreadyAssignedItemCulture && !string.IsNullOrEmpty(existingCulture))
                    {
                        AssignedFiles[i].SetMetadata(ItemMetadataNames.withCulture, "true");
                        cultureList.Add(AssignedFiles[i]);

                        CultureNeutralAssignedFiles[i] = new TaskItem(AssignedFiles[i]);
                    }
                    else
                    {
                        Culture.ItemCultureInfo info = Culture.GetItemCultureInfo(
                            AssignedFiles[i].ItemSpec,
                            dependentUpon,
                            // If 'WithCulture' is explicitly set to false, treat as 'culture-neutral' and keep the original name of the resource.
                            // https://github.com/dotnet/msbuild/issues/3064
                            ConversionUtilities.ValidBooleanFalse(AssignedFiles[i].GetMetadata(ItemMetadataNames.withCulture)));

                        // The culture was explicitly specified, but not opted in via 'RespectAlreadyAssignedItemCulture' and different will be used
                        if (WarnOnCultureOverwritten &&
                            !string.IsNullOrEmpty(existingCulture) &&
                            !MSBuildNameIgnoreCaseComparer.Default.Equals(existingCulture, info.culture))
                        {
                            Log.LogWarningWithCodeFromResources("AssignCulture.CultureOverwritten",
                                existingCulture, AssignedFiles[i].ItemSpec, info.culture);
                            // Remove the culture if it's not recognized
                            if (string.IsNullOrEmpty(info.culture))
                            {
                                AssignedFiles[i].RemoveMetadata(ItemMetadataNames.culture);
                            }
                        }

                        if (!string.IsNullOrEmpty(info.culture))
                        {
                            AssignedFiles[i].SetMetadata(ItemMetadataNames.culture, info.culture);
                            AssignedFiles[i].SetMetadata(ItemMetadataNames.withCulture, "true");
                            cultureList.Add(AssignedFiles[i]);
                        }
                        else
                        {
                            noCultureList.Add(AssignedFiles[i]);
                            AssignedFiles[i].SetMetadata(ItemMetadataNames.withCulture, "false");
                        }

                        CultureNeutralAssignedFiles[i] =
                        new TaskItem(AssignedFiles[i]) { ItemSpec = info.cultureNeutralFilename };
                    }

                    Log.LogMessageFromResources(
                        MessageImportance.Low,
                        "AssignCulture.Comment",
                        AssignedFiles[i].GetMetadata(ItemMetadataNames.culture),
                        AssignedFiles[i].ItemSpec);
                }
                catch (ArgumentException e)
                {
                    Log.LogErrorWithCodeFromResources("AssignCulture.CannotExtractCulture", Files[i].ItemSpec, e.Message);
                    retValue = false;
                }
#if DEBUG
                catch (Exception e)
                {
                    Debug.Assert(false, "Unexpected exception in AssignCulture.Execute. " +
                        "Please log a MSBuild bug specifying the steps to reproduce the problem. " +
                        e);
                    throw;
                }
#endif
            }

            AssignedFilesWithCulture = cultureList.ToArray();
            AssignedFilesWithNoCulture = noCultureList.ToArray();

            return retValue;
        }

        #endregion
    }
}
