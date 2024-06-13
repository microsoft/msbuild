﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Build.BackEnd.Logging;
using Microsoft.Build.BackEnd.Shared;
using Microsoft.Build.Framework;
using Microsoft.Build.Shared;

namespace Microsoft.Build.Experimental.BuildCheck;

internal class AnalysisDispatchingContext : IAnalysisContext
{
    private readonly Action<BuildEventArgs> _dispatch;
    private readonly BuildEventContext _eventContext;

    public AnalysisDispatchingContext(
        Action<BuildEventArgs> dispatch,
        BuildEventContext eventContext)
    {
        _dispatch = dispatch;
        _eventContext = eventContext;
    }

    public BuildEventContext BuildEventContext => _eventContext;

    public void DispatchBuildEvent(BuildEventArgs buildEvent)
    {
        ErrorUtilities.VerifyThrow(buildEvent != null, "buildEvent is null");

        _dispatch!(buildEvent!);
    }

    public void DispatchAsComment(MessageImportance importance, string messageResourceName, params object?[] messageArgs)
    {
        ErrorUtilities.VerifyThrow(!string.IsNullOrEmpty(messageResourceName), "Need resource string for comment message.");

        DispatchAsCommentFromText(_eventContext, importance, ResourceUtilities.GetResourceString(messageResourceName), messageArgs);
    }

    public void DispatchAsCommentFromText(MessageImportance importance, string message)
        => DispatchAsCommentFromText(_eventContext, importance, message, messageArgs: null);

    private void DispatchAsCommentFromText(BuildEventContext buildEventContext, MessageImportance importance, string message, params object?[]? messageArgs)
    {
        BuildMessageEventArgs buildEvent = EventsCreatorHelper.CreateMessageEventFromText(buildEventContext, importance, message, messageArgs);

        _dispatch!(buildEvent!);
    }

    public void DispatchAsErrorFromText(string? subcategoryResourceName, string? errorCode, string? helpKeyword, BuildEventFileInfo file, string message)
    {
        BuildErrorEventArgs buildEvent = EventsCreatorHelper.CreateErrorEventFromText(_eventContext, subcategoryResourceName, errorCode, helpKeyword, file, message);

        _dispatch!(buildEvent!);
    }
}
