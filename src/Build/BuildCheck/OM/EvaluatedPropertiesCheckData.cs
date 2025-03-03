// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using Microsoft.Build.Shared;

namespace Microsoft.Build.Experimental.BuildCheck;

/// <summary>
/// BuildCheck OM data representing the evaluated properties of a project.
/// </summary>
public class EvaluatedPropertiesCheckData : CheckData
{
    internal EvaluatedPropertiesCheckData(
        string projectFilePath,
        int? projectConfigurationId,
        IReadOnlyDictionary<string, string> evaluatedProperties,
        IReadOnlyDictionary<string, string> globalProperties,
        IReadOnlyDictionary<string, List<(string, IMSBuildElementLocation)>> propertyToLocationMap)
        : base(projectFilePath, projectConfigurationId)
        => (EvaluatedProperties, GlobalProperties, EvaluatedPropertyToLocationMap) = (evaluatedProperties, globalProperties, propertyToLocationMap);

    /// <summary>
    /// Gets the evaluated properties of the project.
    /// </summary>
    public IReadOnlyDictionary<string, string> EvaluatedProperties { get; }

    /// <summary>
    /// Gets the global properties passed to the project.
    /// </summary>
    public IReadOnlyDictionary<string, string> GlobalProperties { get; }

    /// TODO: should I restrict the number of entries?
    /// <summary>
    /// Contains the set of evaluated properties and their locations.
    /// </summary>
    public IReadOnlyDictionary<string, List<(string, IMSBuildElementLocation)>> EvaluatedPropertyToLocationMap { get; }
}
