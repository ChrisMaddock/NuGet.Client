// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;
using Microsoft;
using Microsoft.ServiceHub.Framework;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Utilities;
using NuGet.Configuration;
using NuGet.PackageManagement.VisualStudio;
using NuGet.Packaging;
using NuGet.Packaging.PackageExtraction;
using NuGet.Packaging.Signing;
using NuGet.ProjectManagement;
using NuGet.Protocol.Core.Types;
using NuGet.VisualStudio;
using NuGet.VisualStudio.Internal.Contracts;

namespace NuGet.PackageManagement.UI
{
    [Export(typeof(INuGetUIFactory))]
    internal sealed class NuGetUIFactory : INuGetUIFactory, IDisposable
    {
        private AsyncLazy<INuGetSolutionManagerService> _solutionManagerService;

        [Import]
        private ICommonOperations CommonOperations { get; set; }

        [Import]
        private Lazy<IDeleteOnRestartManager> DeleteOnRestartManager { get; set; }

        [Import]
        private Lazy<INuGetLockService> LockService { get; set; }

        [Import]
        private Lazy<IOptionsPageActivator> OptionsPageActivator { get; set; }

        [Import]
        private INuGetUILogger OutputConsoleLogger { get; set; }

        [ImportMany]
        private IEnumerable<Lazy<NuGet.VisualStudio.IVsPackageManagerProvider, IOrderable>> PackageManagerProviders { get; set; }

        [Import]
        private Lazy<IPackageRestoreManager> PackageRestoreManager { get; set; }

        [Export(typeof(INuGetProjectContext))]
        private NuGetUIProjectContext ProjectContext { get; }

        [Import]
        private Lazy<ISettings> Settings { get; set; }

        [Import]
        private IVsSolutionManager SolutionManager { get; set; }

        [Import]
        private SolutionUserOptions SolutionUserOptions { get; set; }

        [Import]
        private Lazy<ISourceRepositoryProvider> SourceRepositoryProvider { get; set; }

        [ImportingConstructor]
        public NuGetUIFactory(
            ICommonOperations commonOperations,
            INuGetUILogger logger,
            ISourceControlManagerProvider sourceControlManagerProvider)
        {
            ProjectContext = new NuGetUIProjectContext(
                commonOperations,
                logger,
                sourceControlManagerProvider);

            _solutionManagerService = new AsyncLazy<INuGetSolutionManagerService>(
                async () =>
                {
                    IServiceBroker serviceBroker = await BrokeredServicesUtilities.GetRemoteServiceBrokerAsync();

#pragma warning disable ISB001 // Dispose of proxies
                    INuGetSolutionManagerService solutionManagerService = await serviceBroker.GetProxyAsync<INuGetSolutionManagerService>(
                        NuGetServices.SolutionManagerService, CancellationToken.None);
#pragma warning restore ISB001 // Dispose of proxies

                    Assumes.NotNull(solutionManagerService);

                    return solutionManagerService;
                },
                NuGetUIThreadHelper.JoinableTaskFactory);
        }

        public void Dispose()
        {
            if (_solutionManagerService.IsValueCreated)
            {
                _solutionManagerService.GetValue()?.Dispose();
            }

            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Returns the UI for the project or given set of projects.
        /// </summary>
        public async ValueTask<INuGetUI> CreateAsync(params IProjectContextInfo[] projects)
        {
            INuGetSolutionManagerService solutionManagerService = await _solutionManagerService.GetValueAsync();

            var uiContext = CreateUIContext(solutionManagerService, projects);

            var adapterLogger = new LoggerAdapter(ProjectContext);
            ProjectContext.PackageExtractionContext = new PackageExtractionContext(
                    PackageSaveMode.Defaultv2,
                    PackageExtractionBehavior.XmlDocFileSaveMode,
                    ClientPolicyContext.GetClientPolicy(Settings.Value, adapterLogger),
                    adapterLogger);

            return new NuGetUI(CommonOperations, ProjectContext, uiContext, OutputConsoleLogger);
        }

        private INuGetUIContext CreateUIContext(
            INuGetSolutionManagerService solutionManagerService,
            params IProjectContextInfo[] projects)
        {
            var packageManager = new NuGetPackageManager(
                SourceRepositoryProvider.Value,
                Settings.Value,
                SolutionManager,
                DeleteOnRestartManager.Value);

            var actionEngine = new UIActionEngine(
                SourceRepositoryProvider.Value,
                packageManager,
                LockService.Value);

            // only pick up at most three integrated package managers
            const int MaxPackageManager = 3;
            var packageManagerProviders = PackageManagerProviderUtility.Sort(
                PackageManagerProviders, MaxPackageManager);

            var context = new NuGetUIContext(
                SourceRepositoryProvider.Value,
                SolutionManager,
                solutionManagerService,
                packageManager,
                actionEngine,
                PackageRestoreManager.Value,
                OptionsPageActivator.Value,
                SolutionUserOptions,
                packageManagerProviders)
            {
                Projects = projects
            };

            return context;
        }
    }
}
