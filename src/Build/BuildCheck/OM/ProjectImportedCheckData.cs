﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Build.Experimental.BuildCheck;

namespace Microsoft.Build.Experimental.BuildCheck;

public class ProjectImportedCheckData : CheckData
{
    public ProjectImportedCheckData(string importedProjectFile, string projectFilePath, int? projectConfigurationId)
        : base(projectFilePath, projectConfigurationId) => ImportedProjectFile = importedProjectFile;

    public string ImportedProjectFile { get; }
}
