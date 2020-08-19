// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft;
using Microsoft.ServiceHub.Framework;
using Microsoft.VisualStudio.Shell;
using NuGet.Common;
using NuGet.PackageManagement.Telemetry;
using NuGet.PackageManagement.VisualStudio;
using NuGet.Packaging.Core;
using NuGet.ProjectManagement;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using NuGet.VisualStudio;
using NuGet.VisualStudio.Internal.Contracts;
using Task = System.Threading.Tasks.Task;
using TelemetryPiiProperty = Microsoft.VisualStudio.Telemetry.TelemetryPiiProperty;

namespace NuGet.PackageManagement.UI
{
    /// <summary>
    /// Performs package manager actions and controls the UI to display output while the actions are taking place.
    /// </summary>
    public sealed class UIActionEngine
    {
        private delegate Task<IReadOnlyList<ProjectAction>> ResolveActionsAsync(INuGetProjectManagerService projectManagerService);

        private readonly ISourceRepositoryProvider _sourceProvider;
        private readonly NuGetPackageManager _packageManager;
        private readonly INuGetLockService _lockService;

        /// <summary>
        /// Create a UIActionEngine to perform installs/uninstalls
        /// </summary>
        public UIActionEngine(
            ISourceRepositoryProvider sourceProvider,
            NuGetPackageManager packageManager,
            INuGetLockService lockService)
        {
            if (sourceProvider == null)
            {
                throw new ArgumentNullException(nameof(sourceProvider));
            }

            if (packageManager == null)
            {
                throw new ArgumentNullException(nameof(packageManager));
            }

            if (lockService == null)
            {
                throw new ArgumentNullException(nameof(lockService));
            }

            _sourceProvider = sourceProvider;
            _packageManager = packageManager;
            _lockService = lockService;
        }

        /// <summary>
        /// Perform an install or uninstall user action.
        /// </summary>
        /// <remarks>This needs to be called from a background thread. It may hang on the UI thread.</remarks>
        public async Task PerformInstallOrUninstallAsync(
            INuGetUI uiService,
            UserAction userAction,
            CancellationToken cancellationToken)
        {
            var operationType = NuGetOperationType.Install;
            if (userAction.Action == NuGetProjectActionType.Uninstall)
            {
                operationType = NuGetOperationType.Uninstall;
            }

            await PerformActionAsync(
                uiService,
                userAction,
                operationType,
                (projectManagerService) => GetActionsAsync(
                    projectManagerService,
                    uiService,
                    uiService.Projects,
                    userAction,
                    uiService.RemoveDependencies,
                    uiService.ForceRemove,
                    cancellationToken),
                cancellationToken);
        }

        public async Task UpgradeNuGetProjectAsync(INuGetUI uiService, NuGetProject nuGetProject)
        {
            var context = uiService.UIContext;
            // Restore the project before proceeding
            var solutionDirectory = context.SolutionManager.SolutionDirectory;

            await context.PackageRestoreManager.RestoreMissingPackagesInSolutionAsync(
                solutionDirectory,
                uiService.ProjectContext,
                new LoggerAdapter(uiService.ProjectContext),
                CancellationToken.None);

            var packagesDependencyInfo = await context.PackageManager.GetInstalledPackagesDependencyInfo(nuGetProject, CancellationToken.None, includeUnresolved: true);
            var upgradeInformationWindowModel = new NuGetProjectUpgradeWindowModel((MSBuildNuGetProject)nuGetProject, packagesDependencyInfo.ToList());

            var result = uiService.ShowNuGetUpgradeWindow(upgradeInformationWindowModel);
            if (!result)
            {
                // raise upgrade telemetry event with Cancelled status
                var packagesCount = upgradeInformationWindowModel.UpgradeDependencyItems.Count;

                var upgradeTelemetryEvent = new UpgradeInformationTelemetryEvent();
                IEnumerable<string> projectIds = await ProjectUtility.GetProjectIdsAsync(uiService.Projects, CancellationToken.None);

                upgradeTelemetryEvent.SetResult(projectIds, NuGetOperationStatus.Cancelled, packagesCount);

                TelemetryActivity.EmitTelemetryEvent(upgradeTelemetryEvent);

                return;
            }

            var progressDialogData = new ProgressDialogData(Resources.NuGetUpgrade_WaitMessage);
            string backupPath;

            var windowTitle = string.Format(
                CultureInfo.CurrentCulture,
                Resources.WindowTitle_NuGetMigrator,
                NuGetProject.GetUniqueNameOrName(nuGetProject));

            using (var progressDialogSession = await context.StartModalProgressDialogAsync(windowTitle, progressDialogData, uiService))
            {
                backupPath = await PackagesConfigToPackageReferenceMigrator.DoUpgradeAsync(
                    context,
                    uiService,
                    nuGetProject,
                    upgradeInformationWindowModel.UpgradeDependencyItems,
                    upgradeInformationWindowModel.NotFoundPackages,
                    progressDialogSession.Progress,
                    progressDialogSession.UserCancellationToken);
            }

            if (!string.IsNullOrEmpty(backupPath))
            {
                var htmlLogFile = GenerateUpgradeReport(nuGetProject, backupPath, upgradeInformationWindowModel);

                Process process = null;
                try
                {
                    process = Process.Start(htmlLogFile);
                }
                catch { }
            }
        }

        private static string GenerateUpgradeReport(NuGetProject nuGetProject, string backupPath, NuGetProjectUpgradeWindowModel upgradeInformationWindowModel)
        {
            var projectName = NuGetProject.GetUniqueNameOrName(nuGetProject);
            using (var upgradeLogger = new UpgradeLogger(projectName, backupPath))
            {
                var installedAsTopLevel = upgradeInformationWindowModel.UpgradeDependencyItems.Where(t => t.InstallAsTopLevel);
                var transitiveDependencies = upgradeInformationWindowModel.TransitiveDependencies.Where(t => !t.InstallAsTopLevel);
                foreach (var package in installedAsTopLevel)
                {
                    upgradeLogger.RegisterPackage(projectName, package.Id, package.Version, package.Issues, true);
                }

                foreach (var package in transitiveDependencies)
                {
                    upgradeLogger.RegisterPackage(projectName, package.Id, package.Version, package.Issues, false);
                }

                return upgradeLogger.GetHtmlFilePath();
            }
        }

        /// <summary>
        /// Perform the multi-package update action.
        /// </summary>
        /// <remarks>This needs to be called from a background thread. It may hang on the UI thread.</remarks>
        public Task PerformUpdateAsync(
            INuGetUI uiService,
            List<PackageIdentity> packagesToUpdate,
            CancellationToken cancellationToken)
        {
            return PerformActionAsync(
                uiService,
                userAction: null,
                NuGetOperationType.Update,
                (projectManagerService) =>
                    ResolveActionsForUpdateAsync(projectManagerService, uiService, packagesToUpdate, cancellationToken),
                cancellationToken);
        }

        /// <summary>
        /// Calculates the list of actions needed to perform packages updates.
        /// </summary>
        private async Task<IReadOnlyList<ProjectAction>> ResolveActionsForUpdateAsync(
            INuGetProjectManagerService projectManagerService,
            INuGetUI uiService,
            List<PackageIdentity> packagesToUpdate,
            CancellationToken token)
        {
            bool includePrerelease = packagesToUpdate.Where(
                package => package.Version.IsPrerelease).Any();

            string[] projectIds = uiService.Projects.Select(project => project.ProjectId).ToArray();

            IReadOnlyList<string> packageSourceNames = uiService.ActiveSources.Select(source => source.PackageSource.Name).ToList();

            return await projectManagerService.GetUpdateActionsAsync(
                projectIds,
                packagesToUpdate,
                VersionConstraints.None,
                includePrerelease,
                uiService.DependencyBehavior,
                packageSourceNames,
                token);
        }

        private async Task PerformActionAsync(
            INuGetUI uiService,
            UserAction userAction,
            NuGetOperationType operationType,
            ResolveActionsAsync resolveActionsAsync,
            CancellationToken cancellationToken)
        {
            IServiceBroker serviceBroker = await BrokeredServicesUtilities.GetRemoteServiceBrokerAsync();

            using (INuGetProjectManagerService projectManagerService = await serviceBroker.GetProxyAsync<INuGetProjectManagerService>(
                NuGetServices.ProjectManagerService,
                cancellationToken: cancellationToken))
            {
                Assumes.NotNull(projectManagerService);

                await projectManagerService.BeginOperationAsync();

                try
                {
                    await PerformActionImplAsync(
                        projectManagerService,
                        uiService,
                        resolveActionsAsync,
                        operationType,
                        userAction,
                        cancellationToken);
                }
                finally
                {
                    await projectManagerService.EndOperationAsync();
                }
            }
        }

        private async Task PerformActionImplAsync(
            INuGetProjectManagerService projectManagerService,
            INuGetUI uiService,
            ResolveActionsAsync resolveActionsAsync,
            NuGetOperationType operationType,
            UserAction userAction,
            CancellationToken cancellationToken)
        {
            var status = NuGetOperationStatus.Succeeded;
            var startTime = DateTimeOffset.Now;
            var packageCount = 0;

            var continueAfterPreview = true;
            var acceptedLicense = true;

            List<string> removedPackages = null;
            var existingPackages = new HashSet<Tuple<string, string>>();
            List<Tuple<string, string>> addedPackages = null;
            List<Tuple<string, string>> updatedPackagesOld = null;
            List<Tuple<string, string>> updatedPackagesNew = null;

            // Enable granular level telemetry events for nuget ui operation
            uiService.ProjectContext.OperationId = Guid.NewGuid();

            Stopwatch packageEnumerationTime = new Stopwatch();
            packageEnumerationTime.Start();
            try
            {
                // collect the install state of the existing packages
                foreach (IProjectContextInfo project in uiService.Projects)
                {
                    IEnumerable<Packaging.PackageReference> packages = await project.GetInstalledPackagesAsync(cancellationToken);

                    foreach (Packaging.PackageReference package in packages)
                    {
                        Tuple<string, string> packageInfo = new Tuple<string, string>(package.PackageIdentity.Id, (package.PackageIdentity.Version == null ? "" : package.PackageIdentity.Version.ToNormalizedString()));

                        if (!existingPackages.Contains(packageInfo))
                        {
                            existingPackages.Add(packageInfo);
                        }
                    }
                }
            }
            catch (Exception)
            {
                // don't teardown the process if we have a telemetry failure
            }
            packageEnumerationTime.Stop();

            await _lockService.ExecuteNuGetOperationAsync(async () =>
            {
                try
                {
                    uiService.BeginOperation();

                    var acceptedFormat = await CheckPackageManagementFormatAsync(projectManagerService, uiService, cancellationToken);
                    if (!acceptedFormat)
                    {
                        return;
                    }

                    TelemetryServiceUtility.StartOrResumeTimer();

                    IReadOnlyList<ProjectAction> actions = await resolveActionsAsync(projectManagerService);
                    IReadOnlyList<PreviewResult> results = await GetPreviewResultsAsync(projectManagerService, actions, cancellationToken);

                    if (operationType == NuGetOperationType.Uninstall)
                    {
                        // removed packages don't have version info
                        removedPackages = results.SelectMany(result => result.Deleted)
                                                    .Select(package => package.Id)
                                                    .Distinct()
                                                    .ToList();
                        packageCount = removedPackages.Count;
                    }
                    else
                    {
                        // log rich info about added packages
                        addedPackages = results.SelectMany(result => result.Added)
                                                .Select(package => new Tuple<string, string>(package.Id, (package.Version == null ? "" : package.Version.ToNormalizedString())))
                                                .Distinct()
                                                .ToList();
                        var addCount = addedPackages.Count;

                        //updated packages can have an old and a new id.
                        updatedPackagesOld = results.SelectMany(result => result.Updated)
                                                    .Select(package => new Tuple<string, string>(package.Old.Id, (package.Old.Version == null ? "" : package.Old.Version.ToNormalizedString())))
                                                    .Distinct()
                                                    .ToList();
                        updatedPackagesNew = results.SelectMany(result => result.Updated)
                                                    .Select(package => new Tuple<string, string>(package.New.Id, (package.New.Version == null ? "" : package.New.Version.ToNormalizedString())))
                                                    .Distinct()
                                                    .ToList();
                        var updateCount = updatedPackagesNew.Count;

                        // update packages count
                        packageCount = addCount + updateCount;

                        if (updateCount > 0)
                        {
                            // set operation type to update when there are packages being updated
                            operationType = NuGetOperationType.Update;
                        }
                    }

                    TelemetryServiceUtility.StopTimer();

                    // Show the preview window.
                    if (uiService.DisplayPreviewWindow)
                    {
                        bool shouldContinue = uiService.PromptForPreviewAcceptance(results);
                        if (!shouldContinue)
                        {
                            continueAfterPreview = false;
                            return;
                        }
                    }

                    TelemetryServiceUtility.StartOrResumeTimer();

                    // Show the license acceptance window.
                    bool accepted = await CheckLicenseAcceptanceAsync(uiService, results, cancellationToken);

                    TelemetryServiceUtility.StartOrResumeTimer();

                    if (!accepted)
                    {
                        acceptedLicense = false;
                        return;
                    }

                    // Warn about the fact that the "dotnet" TFM is deprecated.
                    if (uiService.DisplayDeprecatedFrameworkWindow)
                    {
                        bool shouldContinue = await ShouldContinueDueToDotnetDeprecationAsync(projectManagerService, uiService, cancellationToken);

                        TelemetryServiceUtility.StartOrResumeTimer();

                        if (!shouldContinue)
                        {
                            return;
                        }
                    }

                    if (!cancellationToken.IsCancellationRequested)
                    {
                        await projectManagerService.ExecuteActionsAsync(actions, cancellationToken);

                        uiService.UIContext.FireProjectActionsExecuted(actions);
                    }
                    else
                    {
                        status = NuGetOperationStatus.Cancelled;
                    }
                }
                catch (System.Net.Http.HttpRequestException ex)
                {
                    status = NuGetOperationStatus.Failed;
                    if (ex.InnerException != null)
                    {
                        uiService.ShowError(ex.InnerException);
                    }
                    else
                    {
                        uiService.ShowError(ex);
                    }
                }
                catch (Exception ex)
                {
                    status = NuGetOperationStatus.Failed;
                    uiService.ShowError(ex);
                }
                finally
                {
                    TelemetryServiceUtility.StopTimer();

                    var duration = TelemetryServiceUtility.GetTimerElapsedTime();

                    uiService.ProjectContext.Log(MessageLevel.Info,
                        string.Format(CultureInfo.CurrentCulture, Resources.Operation_TotalTime, duration));

                    uiService.EndOperation();

                    // don't show "Succeeded" if we actually cancelled...
                    if ((!continueAfterPreview) || (!acceptedLicense))
                    {
                        if (status == NuGetOperationStatus.Succeeded)
                        {
                            status = NuGetOperationStatus.Cancelled;
                        }
                    }

                    PackageLoadContext plc = new PackageLoadContext(sourceRepositories: null, isSolution: false, uiService.UIContext);
                    var frameworks = (await plc.GetSupportedFrameworksAsync()).ToList();
                    string[] projectIds = (await ProjectUtility.GetProjectIdsAsync(uiService.Projects, cancellationToken)).ToArray();

                    var actionTelemetryEvent = new VSActionsTelemetryEvent(
                        uiService.ProjectContext.OperationId.ToString(),
                        projectIds,
                        operationType,
                        OperationSource.UI,
                        startTime,
                        status,
                        packageCount,
                        DateTimeOffset.Now,
                        duration.TotalSeconds);

                    var nuGetUI = uiService as NuGetUI;
                    AddUiActionEngineTelemetryProperties(
                        actionTelemetryEvent,
                        continueAfterPreview,
                        acceptedLicense,
                        userAction,
                        nuGetUI?.SelectedIndex,
                        nuGetUI?.RecommendedCount,
                        nuGetUI?.RecommendPackages,
                        nuGetUI?.RecommenderVersion,
                        existingPackages,
                        addedPackages,
                        removedPackages,
                        updatedPackagesOld,
                        updatedPackagesNew,
                        frameworks);

                    actionTelemetryEvent["InstalledPackageEnumerationTimeInMilliseconds"] = packageEnumerationTime.ElapsedMilliseconds;

                    TelemetryActivity.EmitTelemetryEvent(actionTelemetryEvent);
                }
            }, cancellationToken);
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Globalization", "CA1308:Normalize strings to uppercase", Justification = "We require lowercase package names in telemetry so that the hashes are consistent")]
        private static void AddUiActionEngineTelemetryProperties(
            VSActionsTelemetryEvent actionTelemetryEvent,
            bool continueAfterPreview,
            bool acceptedLicense,
            UserAction userAction,
            int? selectedIndex,
            int? recommendedCount,
            bool? recommendPackages,
            (string modelVersion, string vsixVersion)? recommenderVersion,
            HashSet<Tuple<string, string>> existingPackages,
            List<Tuple<string, string>> addedPackages,
            List<string> removedPackages,
            List<Tuple<string, string>> updatedPackagesOld,
            List<Tuple<string, string>> updatedPackagesNew,
            List<string> targetFrameworks)
        {
            static TelemetryEvent ToTelemetryPackage(Tuple<string, string> package)
            {
                var subEvent = new TelemetryEvent(eventName: null);
                subEvent.AddPiiData("id", package.Item1?.ToLowerInvariant() ?? "(empty package id)");
                subEvent["version"] = package.Item2;
                return subEvent;
            }

            static List<TelemetryEvent> ToTelemetryPackageList(List<Tuple<string, string>> packages)
            {
                var list = new List<TelemetryEvent>(packages.Count);
                list.AddRange(packages.Select(ToTelemetryPackage));
                return list;
            }

            // log possible cancel reasons
            if (!continueAfterPreview)
            {
                actionTelemetryEvent["CancelAfterPreview"] = "True";
            }

            if (!acceptedLicense)
            {
                actionTelemetryEvent["AcceptedLicense"] = "False";
            }

            // log the single top level package the user is installing or removing
            if (userAction != null)
            {
                // userAction.Version can be null for deleted packages.
                actionTelemetryEvent.ComplexData["SelectedPackage"] = ToTelemetryPackage(new Tuple<string, string>(userAction.PackageId, userAction.Version?.ToNormalizedString() ?? string.Empty));
                actionTelemetryEvent["SelectedIndex"] = selectedIndex;
                actionTelemetryEvent["RecommendedCount"] = recommendedCount;
                actionTelemetryEvent["RecommendPackages"] = recommendPackages;
                actionTelemetryEvent["Recommender.ModelVersion"] = recommenderVersion?.modelVersion;
                actionTelemetryEvent["Recommender.VsixVersion"] = recommenderVersion?.vsixVersion;
            }

            // log the installed package state
            if (existingPackages?.Count > 0)
            {
                var packages = new List<TelemetryEvent>();

                foreach (var package in existingPackages)
                {
                    packages.Add(ToTelemetryPackage(package));
                }

                actionTelemetryEvent.ComplexData["ExistingPackages"] = packages;
            }

            // other packages can be added, removed, or upgraded as part of bulk upgrade or as part of satisfying package dependencies, so log that also
            if (addedPackages?.Count > 0)
            {
                var packages = new List<TelemetryEvent>();

                foreach (var package in addedPackages)
                {
                    packages.Add(ToTelemetryPackage(package));
                }

                actionTelemetryEvent.ComplexData["AddedPackages"] = packages;
            }

            if (removedPackages?.Count > 0)
            {
                var packages = new List<TelemetryPiiProperty>();

                foreach (var package in removedPackages)
                {
                    packages.Add(new TelemetryPiiProperty(package?.ToLowerInvariant() ?? "(empty package id)"));
                }

                actionTelemetryEvent.ComplexData["RemovedPackages"] = packages;
            }

            // two collections for updated packages: pre and post upgrade
            if (updatedPackagesNew?.Count > 0)
            {
                actionTelemetryEvent.ComplexData["UpdatedPackagesNew"] = ToTelemetryPackageList(updatedPackagesNew);
            }

            if (updatedPackagesOld?.Count > 0)
            {
                actionTelemetryEvent.ComplexData["UpdatedPackagesOld"] = ToTelemetryPackageList(updatedPackagesOld);
            }

            // target framworks
            if (targetFrameworks?.Count > 0)
            {
                actionTelemetryEvent["TargetFrameworks"] = string.Join(";", targetFrameworks);
            }
        }

        private async Task<bool> CheckPackageManagementFormatAsync(
            INuGetProjectManagerService projectManagerService,
            INuGetUI uiService,
            CancellationToken cancellationToken)
        {
            IReadOnlyCollection<string> projectGuids = uiService.Projects.Select(project => project.ProjectId).ToArray();
            IReadOnlyCollection<IProjectContextInfo> upgradeableProjects = await projectManagerService.GetUpgradeableProjectsAsync(
                projectGuids,
                cancellationToken);

            // only show this dialog if there are any new project(s) with no installed packages.
            if (upgradeableProjects.Count > 0)
            {
                var packageManagementFormat = new PackageManagementFormat(uiService.Settings);

                if (!packageManagementFormat.Enabled)
                {
                    // user disabled this prompt either through Tools->options or previous interaction of this dialog.
                    // now check for default package format, if its set to PackageReference then update the project.
                    if (packageManagementFormat.SelectedPackageManagementFormat == 1)
                    {
                        await uiService.UpgradeProjectsToPackageReferenceAsync(upgradeableProjects);
                    }

                    return true;
                }

                Task<string>[] tasks = upgradeableProjects
                    .Select(project => project.GetMetadataAsync<string>(NuGetProjectMetadataKeys.Name, cancellationToken).AsTask())
                    .ToArray();

                await Task.WhenAll(tasks);

                packageManagementFormat.ProjectNames = tasks.Select(task => task.Result)
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                // show dialog for package format selector
                bool result = uiService.PromptForPackageManagementFormat(packageManagementFormat);

                // update nuget projects if user selected PackageReference option
                if (result && packageManagementFormat.SelectedPackageManagementFormat == 1)
                {
                    await uiService.UpgradeProjectsToPackageReferenceAsync(upgradeableProjects);
                }

                return result;
            }

            return true;
        }

        // Returns false if user doesn't accept license agreements.
        private async Task<bool> CheckLicenseAcceptanceAsync(
            INuGetUI uiService,
            IEnumerable<PreviewResult> results,
            CancellationToken token)
        {
            // find all the packages that might need a license acceptance
            var licenseCheck = new HashSet<PackageIdentity>(PackageIdentity.Comparer);

            foreach (PreviewResult result in results)
            {
                foreach (AccessiblePackageIdentity pkg in result.Added)
                {
                    licenseCheck.Add(pkg);
                }

                foreach (UpdatePreviewResult pkg in result.Updated)
                {
                    licenseCheck.Add(pkg.New);
                }
            }

            IEnumerable<SourceRepository> sources = _sourceProvider.GetRepositories().Where(e => e.PackageSource.IsEnabled);
            List<IPackageSearchMetadata> licenseMetadata = await GetPackageMetadataAsync(sources, licenseCheck, token);

            TelemetryServiceUtility.StopTimer();

            // show license agreement
            if (licenseMetadata.Any(e => e.RequireLicenseAcceptance))
            {
                IEnumerable<PackageLicenseInfo> licenseInfoItems = licenseMetadata
                    .Where(p => p.RequireLicenseAcceptance)
                    .Select(e => GeneratePackageLicenseInfo(e));

                return uiService.PromptForLicenseAcceptance(licenseInfoItems);
            }

            return true;
        }

        private PackageLicenseInfo GeneratePackageLicenseInfo(IPackageSearchMetadata metadata)
        {
            return new PackageLicenseInfo(
                metadata.Identity.Id,
                PackageLicenseUtilities.GenerateLicenseLinks(metadata),
                metadata.Authors);
        }

        private async ValueTask<bool> ShouldContinueDueToDotnetDeprecationAsync(
            INuGetProjectManagerService projectManagerService,
            INuGetUI uiService,
            CancellationToken token)
        {
            IReadOnlyCollection<IProjectContextInfo> projects = await projectManagerService.GetProjectsWithDeprecatedDotnetFrameworkAsync(token);

            TelemetryServiceUtility.StopTimer();

            if (projects.Any())
            {
                return await uiService.WarnAboutDotnetDeprecationAsync(projects, token);
            }

            return true;
        }

        private async Task<IReadOnlyList<ProjectAction>> GetActionsAsync(
            INuGetProjectManagerService projectManagerService,
            INuGetUI uiService,
            IEnumerable<IProjectContextInfo> projects,
            UserAction userAction,
            bool removeDependencies,
            bool forceRemove,
            CancellationToken token)
        {
            var results = new List<ProjectAction>();

            // Allow prerelease packages only if the target is prerelease
            bool includePrelease =
                userAction.Action == NuGetProjectActionType.Uninstall ||
                userAction.Version.IsPrerelease == true;

            IReadOnlyList<string> packageSourceNames = uiService.ActiveSources.Select(source => source.PackageSource.Name).ToList();

            if (userAction.Action == NuGetProjectActionType.Install)
            {
                var packageIdentity = new PackageIdentity(userAction.PackageId, userAction.Version);

                foreach (ProjectContextInfo project in projects)
                {
                    string projectGuid = await project.GetMetadataAsync<string>(NuGetProjectMetadataKeys.ProjectId, token);

                    IReadOnlyList<ProjectAction> actions = await projectManagerService.GetInstallActionsAsync(
                        projectGuid,
                        packageIdentity,
                        VersionConstraints.None,
                        includePrelease,
                        uiService.DependencyBehavior,
                        packageSourceNames,
                        token);

                    results.AddRange(actions);
                }
            }
            else
            {
                foreach (ProjectContextInfo project in projects)
                {
                    string projectGuid = await project.GetMetadataAsync<string>(NuGetProjectMetadataKeys.ProjectId, token);

                    IEnumerable<ProjectAction> actions = await projectManagerService.GetUninstallActionsAsync(
                        projectGuid,
                        userAction.PackageId,
                        removeDependencies,
                        forceRemove,
                        token);

                    results.AddRange(actions);
                }
            }

            return results;
        }

        /// <summary>
        /// Convert NuGetProjectActions into PreviewResult types
        /// </summary>
        private static IReadOnlyList<PreviewResult> GetPreviewResults(IEnumerable<ResolvedAction> projectActions)
        {
            var results = new List<PreviewResult>();

            var expandedActions = new List<ResolvedAction>();

            // BuildIntegratedProjectActions contain all project actions rolled up into a single action,
            // to display these we need to expand them into the low level actions.
            foreach (var action in projectActions)
            {
                var buildIntegratedAction = action.Action as BuildIntegratedProjectAction;

                if (buildIntegratedAction != null)
                {
                    foreach (var buildAction in buildIntegratedAction.GetProjectActions())
                    {
                        expandedActions.Add(new ResolvedAction(action.Project, buildAction));
                    }
                }
                else
                {
                    // leave the action as is
                    expandedActions.Add(action);
                }
            }

            // Group actions by project
            var actionsByProject = expandedActions.GroupBy(action => action.Project);

            // Group actions by operation
            foreach (var actions in actionsByProject)
            {
                var installed = new Dictionary<string, PackageIdentity>(StringComparer.OrdinalIgnoreCase);
                var uninstalled = new Dictionary<string, PackageIdentity>(StringComparer.OrdinalIgnoreCase);
                var packageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var action in actions.Select(a => a.Action))
                {
                    var package = action.PackageIdentity;
                    packageIds.Add(package.Id);

                    // Create new identities without the dependency graph
                    if (action.NuGetProjectActionType == NuGetProjectActionType.Install)
                    {
                        installed[package.Id] = new PackageIdentity(package.Id, package.Version);
                    }
                    else
                    {
                        uninstalled[package.Id] = new PackageIdentity(package.Id, package.Version);
                    }
                }

                var added = new List<AccessiblePackageIdentity>();
                var deleted = new List<AccessiblePackageIdentity>();
                var updated = new List<UpdatePreviewResult>();
                foreach (var packageId in packageIds)
                {
                    var isInstalled = installed.ContainsKey(packageId);
                    var isUninstalled = uninstalled.ContainsKey(packageId);

                    if (isInstalled && isUninstalled)
                    {
                        // the package is updated
                        updated.Add(new UpdatePreviewResult(uninstalled[packageId], installed[packageId]));
                        installed.Remove(packageId);
                    }
                    else if (isInstalled && !isUninstalled)
                    {
                        // the package is added
                        added.Add(new AccessiblePackageIdentity(installed[packageId]));
                    }
                    else if (!isInstalled && isUninstalled)
                    {
                        // the package is deleted
                        deleted.Add(new AccessiblePackageIdentity(uninstalled[packageId]));
                    }
                }

                string projectName;

                if (actions.Key.TryGetMetadata(NuGetProjectMetadataKeys.UniqueName, out string value))
                {
                    projectName = value;
                }
                else
                {
                    projectName = Resources.Preview_UnknownProject;
                }

                var result = new PreviewResult(projectName, added, deleted, updated);
                results.Add(result);
            }

            return results;
        }

        private static async ValueTask<IReadOnlyList<PreviewResult>> GetPreviewResultsAsync(
            INuGetProjectManagerService projectManagerService,
            IReadOnlyList<ProjectAction> projectActions,
            CancellationToken cancellationToken)
        {
            var results = new List<PreviewResult>();

            var expandedActions = new List<ProjectAction>(projectActions);

            foreach (ProjectAction projectAction in projectActions)
            {
                if (projectAction.ImplicitActions.Any())
                {
                    foreach (ImplicitProjectAction implicitAction in projectAction.ImplicitActions)
                    {
                        expandedActions.Add(
                            new ProjectAction(
                                implicitAction.Id,
                                projectAction.ProjectId,
                                implicitAction.PackageId,
                                implicitAction.PackageVersion,
                                implicitAction.ProjectActionType,
                                implicitActions: null));
                    }
                }
                else
                {
                    // leave the action as is
                    expandedActions.Add(projectAction);
                }
            }

            // Group actions by project
            var actionsByProject = expandedActions.GroupBy(action => action.ProjectId);

            // Group actions by operation
            foreach (IGrouping<string, ProjectAction> actions in actionsByProject)
            {
                var installed = new Dictionary<string, PackageIdentity>(StringComparer.OrdinalIgnoreCase);
                var uninstalled = new Dictionary<string, PackageIdentity>(StringComparer.OrdinalIgnoreCase);
                var packageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (ProjectAction action in actions)
                {
                    var packageIdentity = new PackageIdentity(action.PackageId, NuGetVersion.Parse(action.PackageVersion));

                    packageIds.Add(packageIdentity.Id);

                    // Create new identities without the dependency graph
                    if (action.ProjectActionType == NuGetProjectActionType.Install)
                    {
                        installed[action.PackageId] = packageIdentity;
                    }
                    else
                    {
                        uninstalled[action.PackageId] = packageIdentity;
                    }
                }

                var added = new List<AccessiblePackageIdentity>();
                var deleted = new List<AccessiblePackageIdentity>();
                var updated = new List<UpdatePreviewResult>();

                foreach (var packageId in packageIds)
                {
                    var isInstalled = installed.ContainsKey(packageId);
                    var isUninstalled = uninstalled.ContainsKey(packageId);

                    if (isInstalled && isUninstalled)
                    {
                        // the package is updated
                        updated.Add(new UpdatePreviewResult(uninstalled[packageId], installed[packageId]));
                        installed.Remove(packageId);
                    }
                    else if (isInstalled && !isUninstalled)
                    {
                        // the package is added
                        added.Add(new AccessiblePackageIdentity(installed[packageId]));
                    }
                    else if (!isInstalled && isUninstalled)
                    {
                        // the package is deleted
                        deleted.Add(new AccessiblePackageIdentity(uninstalled[packageId]));
                    }
                }

                string projectName;

                (bool success, object value) = await projectManagerService.TryGetMetadataAsync(actions.Key, NuGetProjectMetadataKeys.UniqueName, cancellationToken);

                if (success)
                {
                    projectName = value as string;
                }
                else
                {
                    projectName = Resources.Preview_UnknownProject;
                }

                var result = new PreviewResult(projectName, added, deleted, updated);

                results.Add(result);
            }

            return results;
        }

        /// <summary>
        /// Get the package metadata to see if RequireLicenseAcceptance is true
        /// </summary>
        private async Task<List<IPackageSearchMetadata>> GetPackageMetadataAsync(
            IEnumerable<SourceRepository> sources,
            IEnumerable<PackageIdentity> packages,
            CancellationToken token)
        {
            var results = new List<IPackageSearchMetadata>();

            // local sources
            var localSources = new List<SourceRepository>();
            localSources.Add(_packageManager.PackagesFolderSourceRepository);
            localSources.AddRange(_packageManager.GlobalPackageFolderRepositories);

            var allPackages = packages.ToArray();

            using (var sourceCacheContext = new SourceCacheContext())
            {
                // first check all the packages with local sources.
                var completed = (await TaskCombinators.ThrottledAsync(
                    allPackages,
                    (p, t) => GetPackageMetadataAsync(localSources, sourceCacheContext, p, t),
                    token)).Where(metadata => metadata != null).ToArray();

                results.AddRange(completed);

                if (completed.Length != allPackages.Length)
                {
                    // get remaining package's metadata from remote repositories
                    var remainingPackages = allPackages.Where(package => !completed.Any(pack => pack.Identity.Equals(package)));

                    var remoteResults = (await TaskCombinators.ThrottledAsync(
                        remainingPackages,
                        (p, t) => GetPackageMetadataAsync(sources, sourceCacheContext, p, t),
                        token)).Where(metadata => metadata != null).ToArray();

                    results.AddRange(remoteResults);
                }
            }
            // check if missing metadata for any package
            if (allPackages.Length != results.Count)
            {
                var package = allPackages.First(pkg => !results.Any(result => result.Identity.Equals(pkg)));

                throw new InvalidOperationException(string.Format("Unable to find metadata of {0}", package));
            }

            return results;
        }

        private static async Task<IPackageSearchMetadata> GetPackageMetadataAsync(
            IEnumerable<SourceRepository> sources,
            SourceCacheContext sourceCacheContext,
            PackageIdentity package,
            CancellationToken token)
        {
            var exceptionList = new List<InvalidOperationException>();

            foreach (var source in sources)
            {
                var metadataResource = source.GetResource<PackageMetadataResource>();
                if (metadataResource == null)
                {
                    continue;
                }

                try
                {
                    var packageMetadata = await metadataResource.GetMetadataAsync(
                        package,
                        sourceCacheContext,
                        log: Common.NullLogger.Instance,
                        token: token);
                    if (packageMetadata != null)
                    {
                        return packageMetadata;
                    }
                }
                catch (InvalidOperationException e)
                {
                    exceptionList.Add(e);
                }
            }

            if (exceptionList.Count > 0)
            {
                throw new AggregateException(exceptionList);
            }

            return null;
        }
    }
}
