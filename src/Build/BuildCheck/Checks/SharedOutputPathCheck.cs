// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.IO;
using Microsoft.Build.Construction;
using Microsoft.Build.Shared;
using Microsoft.Build.Collections;
using System.Linq;

namespace Microsoft.Build.Experimental.BuildCheck.Checks;

internal sealed class SharedOutputPathCheck : Check
{
    private const string RuleId = "BC0101";
    public static CheckRule SupportedRule = new CheckRule(RuleId, "ConflictingOutputPath",
        ResourceUtilities.GetResourceString("BuildCheck_BC0101_Title")!,
        ResourceUtilities.GetResourceString("BuildCheck_BC0101_MessageFmt")!,
        new CheckConfiguration() { RuleId = RuleId, Severity = CheckResultSeverity.Warning });

    public override string FriendlyName => "MSBuild.SharedOutputPathCheck";

    public override IReadOnlyList<CheckRule> SupportedRules { get; } = [SupportedRule];

    public override void Initialize(ConfigurationContext configurationContext)
    {
        /* This is it - no custom configuration */
    }

    public override void RegisterActions(IBuildCheckRegistrationContext registrationContext) => registrationContext.RegisterEvaluatedPropertiesAction(EvaluatedPropertiesAction);

    internal override bool IsBuiltIn => true;

    private readonly Dictionary<string, string> _projectsPerOutputPath = new(MSBuildNameIgnoreCaseComparer.Default);
    private readonly HashSet<string> _projectsSeen = new(MSBuildNameIgnoreCaseComparer.Default);

    private void EvaluatedPropertiesAction(BuildCheckDataContext<EvaluatedPropertiesCheckData> context)
    {
        // We want to avoid repeated checking of a same project (as it might be evaluated multiple times)
        //  for this reason we use a hashset with already seen projects
        if (!_projectsSeen.Add(context.Data.ProjectFilePath))
        {
            return;
        }

        string? binPath, objPath;

        context.Data.EvaluatedProperties.TryGetValue("OutputPath", out binPath);
        context.Data.EvaluatedProperties.TryGetValue("IntermediateOutputPath", out objPath);

        List<(string value, IMSBuildElementLocation location)>? propValueToLocation;
        context.Data.EvaluatedPropertyToLocationMap.TryGetValue("OutputPath", out propValueToLocation);

        string? absoluteBinPath = CheckAndAddFullOutputPath(binPath, context, propValueToLocation);
        // Check objPath only if it is different from binPath
        if (
            !string.IsNullOrEmpty(objPath)
            && !string.IsNullOrEmpty(absoluteBinPath)
            && !MSBuildNameIgnoreCaseComparer.Default.Equals(objPath, binPath)
            && !MSBuildNameIgnoreCaseComparer.Default.Equals(objPath, absoluteBinPath)
        )
        {
            List<(string value, IMSBuildElementLocation location)>? intermediatePropValueToLocation;
            context.Data.EvaluatedPropertyToLocationMap.TryGetValue("IntermediateOutputPath", out intermediatePropValueToLocation);
            CheckAndAddFullOutputPath(objPath, context, intermediatePropValueToLocation);
        }
    }

    private string? CheckAndAddFullOutputPath(
        string? path,
        BuildCheckDataContext<EvaluatedPropertiesCheckData> context,
        List<(string value, IMSBuildElementLocation location)>? propValueToLocation)
    {
        if (string.IsNullOrEmpty(path))
        {
            return path;
        }

        string projectPath = context.Data.ProjectFilePath;
        string rawPath = path!;
        path = BuildCheckUtilities.RootEvaluatedPath(path!, projectPath);

        if (_projectsPerOutputPath.TryGetValue(path!, out string? conflictingProject))
        {
            IMSBuildElementLocation? location = propValueToLocation?.Where(pl => pl.value.Contains(rawPath))
                .LastOrDefault()
                .location;

            context.ReportResult(BuildCheckResult.CreateBuiltIn(
                SupportedRule,
                ElementLocation.Create(location!.File, location.Line, location.Column),
                Path.GetFileName(projectPath),
                Path.GetFileName(conflictingProject),
                path!));
        }
        else
        {
            _projectsPerOutputPath[path!] = projectPath;
        }

        return path;
    }
}
