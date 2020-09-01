// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace NuGet.VisualStudio
{
    /// <summary>
    /// Helper methods to acquire services via <see cref="IServiceProvider"/>.
    /// </summary>
    public static class ServiceProviderExtensions
    {
        public static EnvDTE.DTE GetDTE(this IServiceProvider serviceProvider)
        {
            return serviceProvider.GetService<SDTE, EnvDTE.DTE>();
        }

        public static IComponentModel GetComponentModel(this IServiceProvider serviceProvider)
        {
            return serviceProvider.GetService<SComponentModel, IComponentModel>();
        }

        public static TService GetService<TService>(
            this IServiceProvider serviceProvider)
            where TService : class
        {
            return serviceProvider.GetService(typeof(TService)) as TService;
        }

        public static async Task<EnvDte> GetDTEAsync(this Microsoft.VisualStudio.Shell.IAsyncServiceProvider site)
        {
            return new EnvDte(await site.GetServiceAsync<SDTE, EnvDTE.DTE>());
        }

        public static Task<IComponentModel> GetComponentModelAsync(
            this Microsoft.VisualStudio.Shell.IAsyncServiceProvider site)
        {
            return site.GetServiceAsync<SComponentModel, IComponentModel>();
        }

        public static async Task<TService> GetServiceAsync<TService>(
            this Microsoft.VisualStudio.Shell.IAsyncServiceProvider site)
            where TService : class
        {
            return await site.GetServiceAsync(typeof(TService)) as TService;
        }
    }
}
