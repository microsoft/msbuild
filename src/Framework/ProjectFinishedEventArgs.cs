// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.IO;
using Microsoft.Build.Shared;

namespace Microsoft.Build.Framework
{
    /// <summary>
    /// Arguments for project finished events
    /// </summary>
    // WARNING: marking a type [Serializable] without implementing
    // ISerializable imposes a serialization contract -- it is a
    // promise to never change the type's fields i.e. the type is
    // immutable; adding new fields in the next version of the type
    // without following certain special FX guidelines, can break both
    // forward and backward compatibility
    [Serializable]
    public class ProjectFinishedEventArgs : BuildStatusEventArgs
    {
        /// <summary>
        /// Default constructor
        /// </summary>
        protected ProjectFinishedEventArgs()
            : base()
        {
            // do nothing
        }

        /// <summary>
        /// This constructor allows event data to be initialized.
        /// Sender is assumed to be "MSBuild".
        /// </summary>
        /// <param name="message">text message</param>
        /// <param name="helpKeyword">help keyword </param>
        /// <param name="projectFile">name of the project</param>
        /// <param name="succeeded">true indicates project built successfully</param>
        public ProjectFinishedEventArgs(
            string? message,
            string? helpKeyword,
            string? projectFile,
            bool succeeded)
            : this(message, helpKeyword, projectFile, succeeded, DateTime.UtcNow)
        {
        }

        /// <summary>
        /// This constructor allows event data to be initialized.
        /// Sender is assumed to be "MSBuild". This constructor allows the timestamp to be set as well
        /// </summary>
        /// <param name="message">text message</param>
        /// <param name="helpKeyword">help keyword </param>
        /// <param name="projectFile">name of the project</param>
        /// <param name="succeeded">true indicates project built successfully</param>
        /// <param name="eventTimestamp">Timestamp when the event was created</param>
        public ProjectFinishedEventArgs(
            string? message,
            string? helpKeyword,
            string? projectFile,
            bool succeeded,
            DateTime eventTimestamp)
            : base(message, helpKeyword, "MSBuild", eventTimestamp)
        {
            this.projectFile = projectFile;
            this.succeeded = succeeded;
        }

        private string? projectFile;
        private bool succeeded;

        #region CustomSerializationToStream
        /// <summary>
        /// Serializes to a stream through a binary writer
        /// </summary>
        /// <param name="writer">Binary writer which is attached to the stream the event will be serialized into</param>
        internal override void WriteToStream(BinaryWriter writer)
        {
            base.WriteToStream(writer);

            writer.WriteOptionalString(projectFile);
            writer.Write(succeeded);

            ProjectBuildStats.WriteToStream(writer, ProjectBuildStats);
        }

        /// <summary>
        /// Deserializes from a stream through a binary reader
        /// </summary>
        /// <param name="reader">Binary reader which is attached to the stream the event will be deserialized from</param>
        /// <param name="version">The version of the runtime the message packet was created from</param>
        internal override void CreateFromStream(BinaryReader reader, int version)
        {
            base.CreateFromStream(reader, version);

            projectFile = reader.ReadByte() == 0 ? null : reader.ReadString();
            succeeded = reader.ReadBoolean();

            ProjectBuildStats = ProjectBuildStats.ReadFromStream(reader, version);
        }
        #endregion

        /// <summary>
        /// Project name
        /// </summary>
        public string? ProjectFile => projectFile;

        /// <summary>
        /// True if project built successfully, false otherwise
        /// </summary>
        public bool Succeeded => succeeded;

        public override string Message
        {
            get
            {
                if (RawMessage == null)
                {
                    RawMessage = FormatResourceStringIgnoreCodeAndKeyword(Succeeded ? "ProjectFinishedSuccess" : "ProjectFinishedFailure", Path.GetFileName(ProjectFile));
                }

                return RawMessage;
            }
        }

        /// <summary>
        /// Optional holder of stats for telemetry.
        /// Not intended to be de/serialized for binlogs.
        /// </summary>
        internal ProjectBuildStats? ProjectBuildStats { get; set; }
    }
}
