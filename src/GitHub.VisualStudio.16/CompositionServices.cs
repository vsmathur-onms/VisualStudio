﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.ComponentModel.Composition.Hosting;
using System.ComponentModel.Composition.Primitives;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using GitHub.Api;
using GitHub.Factories;
using GitHub.Infrastructure;
using GitHub.Models;
using GitHub.Services;
using GitHub.Settings;
using GitHub.VisualStudio.Base;
using GitHub.VisualStudio.Settings;
using GitHub.VisualStudio.Views.Dialog.Clone;
using Microsoft;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Rothko;
using static Microsoft.VisualStudio.Composition.NetFxAdapters;
using ExportProvider = System.ComponentModel.Composition.Hosting.ExportProvider;
using Task = System.Threading.Tasks.Task;

namespace GitHub.VisualStudio
{
    [Export]
    public class CompositionServices
    {
        readonly ExportProvider defaultExportProvider;
        readonly IPackageSettings packageSettings;
        ExportProvider exportProvider;
        ExportProvider minimalExportProvider;

        public CompositionServices() : this(CreateOutOfProcExports(), new OutOfProcPackageSettings())
        {
        }

        [ImportingConstructor]
        public CompositionServices(Microsoft.VisualStudio.Composition.ExportProvider defaultExportProvider)
            : this(defaultExportProvider.AsExportProvider())
        {
        }

        public CompositionServices(ExportProvider defaultExportProvider, IPackageSettings packageSettings = null)
        {
            this.defaultExportProvider = defaultExportProvider;
            this.packageSettings = packageSettings;
        }

        public ExportProvider GetMinimalExportProvider()
        {
            return minimalExportProvider = minimalExportProvider ?? CreateMinimalExportProvider();
        }

        public ExportProvider GetExportProvider()
        {
            return exportProvider = exportProvider ?? CreateCompositionContainer();
        }

        ExportProvider CreateMinimalExportProvider()
        {
            var catalog = new LoggingCatalog(new TypeCatalog(
                typeof(GitService), typeof(RepositoryFacade),

                typeof(TeamExplorerContext), typeof(PullRequestService), typeof(GitClient), typeof(GitHubCredentialProvider),
                typeof(WindowsKeychain), typeof(Rothko.OperatingSystemFacade), typeof(ApiClientFactory), typeof(GraphQLClientFactory), typeof(Program),

                typeof(SimpleApiClientFactory), typeof(WikiProbe), typeof(ExportedEnterpriseProbe), typeof(ExportedHttpClient)
            ));
            var compositionContainer = new CompositionContainer(catalog, defaultExportProvider);
            var serviceProvider = compositionContainer.GetExportedValue<SVsServiceProvider>();
            var gitService = compositionContainer.GetExportedValue<IGitService>();
            var joinableTaskContext = compositionContainer.GetExportedValue<JoinableTaskContext>();
            var contextFactory = compositionContainer.GetExportedValueOrDefault<IVSUIContextFactory>();
            var vsGitExt = contextFactory == null ?
                new VSGitExt(serviceProvider, gitService, joinableTaskContext) :
                new VSGitExt(serviceProvider, contextFactory, gitService, joinableTaskContext);
            compositionContainer.ComposeExportedValue<IVSGitExt>(vsGitExt);

            return compositionContainer;
        }

        CompositionContainer CreateCompositionContainer()
        {
            var minimalExportProvider = GetMinimalExportProvider();
            var compositionContainer = CreateCompositionContainer(minimalExportProvider);

            var usageTracker = CreateUsageTracker(compositionContainer);
            compositionContainer.ComposeExportedValue<IUsageTracker>(usageTracker);

            var gitExt = minimalExportProvider.GetExportedValue<IVSGitExt>();
            compositionContainer.ComposeExportedValue(gitExt);

            return compositionContainer;
        }

        UsageTracker CreateUsageTracker(CompositionContainer compositionContainer)
        {
            var gitHubServiceProvider = compositionContainer.GetExportedValue<IGitHubServiceProvider>();
            var packageSettings = this.packageSettings ?? new PackageSettings(compositionContainer.GetExportedValue<IGitHubServiceProvider>());
            var usageService = compositionContainer.GetExportedValue<IUsageService>();
            var joinableTaskContext = compositionContainer.GetExportedValue<JoinableTaskContext>();
            return new UsageTracker(gitHubServiceProvider, usageService, packageSettings, joinableTaskContext);
        }

        static CompositionContainer CreateOutOfProcExports()
        {
            var container = new CompositionContainer();

            var serviceProvider = new OutOfProcSVsServiceProvider();
            container.ComposeExportedValue<SVsServiceProvider>(serviceProvider);

#pragma warning disable VSSDK005 // Avoid instantiating JoinableTaskContext
            var joinableTaskContext = new JoinableTaskContext();
#pragma warning restore VSSDK005 // Avoid instantiating JoinableTaskContext
            container.ComposeExportedValue(joinableTaskContext);

            return container;
        }

        static CompositionContainer CreateCompositionContainer(ExportProvider minimalExportProvider)
        {
            var catalog = new LoggingCatalog(
                GetCatalog(typeof(DialogService).Assembly), // GitHub.App
                GetCatalog(typeof(GraphQLClientFactory).Assembly), // GitHub.Api
                GetCatalog(typeof(RepositoryCloneView).Assembly), // GitHub.VisualStudio.UI
                GetCatalog(typeof(GitHubPackage).Assembly), // GitHub.VisualStudio
                GetCatalog(typeof(VSGitServices).Assembly), // GitHub.TeamFoundation.16
                GetCatalog(typeof(GitService).Assembly), // GitHub.Exports
                GetCatalog(typeof(NotificationDispatcher).Assembly), // GitHub.Exports.Reactive          
                GetCatalog(typeof(IOperatingSystem).Assembly) // Rothko
            );

            var compositionContainer = new CompositionContainer(catalog, minimalExportProvider);

            var gitHubServiceProvider = new MyGitHubServiceProvider(compositionContainer);
            compositionContainer.ComposeExportedValue<IGitHubServiceProvider>(gitHubServiceProvider);
            Services.UnitTestServiceProvider = gitHubServiceProvider; // Use gitHubServiceProvider as global provider 

            var loginManager = CreateLoginManager(compositionContainer);
            compositionContainer.ComposeExportedValue<ILoginManager>(loginManager);

            // Ensure GitHub.Resources.dll has been loaded and it visible to XAML
            EnsureLoaded(typeof(GitHub.Resources));

            return compositionContainer;
        }

        static void EnsureLoaded(Type type)
        {
            // Ensure the containing assembly has been loaded
            Assumes.NotNull(type);
        }

        static LoginManager CreateLoginManager(CompositionContainer compositionContainer)
        {
            var keychain = compositionContainer.GetExportedValue<IKeychain>();
            var lazy2Fa = new Lazy<ITwoFactorChallengeHandler>(() => compositionContainer.GetExportedValue<ITwoFactorChallengeHandler>());
            var oauthListener = compositionContainer.GetExportedValue<IOAuthCallbackListener>();
            var loginManager = new LoginManager(
                    keychain,
                    lazy2Fa,
                    oauthListener,
                    ApiClientConfiguration.ClientId,
                    ApiClientConfiguration.ClientSecret,
                    ApiClientConfiguration.MinimumScopes,
                    ApiClientConfiguration.RequestedScopes,
                    ApiClientConfiguration.AuthorizationNote,
                    ApiClientConfiguration.MachineFingerprint);
            return loginManager;
        }

        static TypeCatalog GetCatalog(Assembly assembly)
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                Debug.WriteLine(e);
                foreach (var ex in e.LoaderExceptions)
                {
                    Debug.WriteLine(ex);
                }

                types = e.Types.Where(t => t != null).ToArray();
            }

            var catalog = new TypeCatalog(types);
            return catalog;
        }
    }

    public class MyGitHubServiceProvider : IGitHubServiceProvider
    {
        readonly IServiceProvider serviceProvider;

        public MyGitHubServiceProvider(ExportProvider exportProvider)
        {
            ExportProvider = exportProvider;
            serviceProvider = exportProvider.GetExportedValue<SVsServiceProvider>();
        }

        public T TryGetService<T>() where T : class
        {
            try
            {
                return GetService<T>();
            }
            catch
            {
                return default;
            }
        }

        public T GetService<T>() where T : class
        {
            return GetService<T, T>();
        }

        public TRet GetService<T, TRet>()
            where T : class
            where TRet : class
        {
            var value = ExportProvider.GetExportedValueOrDefault<T>();
            if (value != null)
            {
                return value as TRet;
            }

            value = GetService(typeof(T)) as T;
            if (value != null)
            {
                return value as TRet;
            }

            Debug.WriteLine($"Couldn't find service of type {typeof(T)}");
            return null;
        }

        public object GetService(Type serviceType)
        {
            if (serviceType == typeof(IGitHubServiceProvider))
            {
                return this;
            }

            return serviceProvider.GetService(serviceType);
        }

        #region obsolete

        public IServiceProvider GitServiceProvider { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }

        public void AddService(Type t, object owner, object instance)
        {
            throw new NotImplementedException();
        }

        public void AddService<T>(object owner, T instance) where T : class
        {
            throw new NotImplementedException();
        }

        public void RemoveService(Type t, object owner)
        {
            throw new NotImplementedException();
        }

        public object TryGetService(Type t)
        {
            throw new NotImplementedException();
        }

        public object TryGetService(string typeName)
        {
            throw new NotImplementedException();
        }

        #endregion

        public ExportProvider ExportProvider { get; }
    }

    public class OutOfProcPackageSettings : IPackageSettings
    {
        public bool CollectMetrics { get; set; } = true;
        public bool EnableTraceLogging { get; set; } = true;
        public bool EditorComments { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
        public UIState UIState { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
        public bool HideTeamExplorerWelcomeMessage { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }

        public event PropertyChangedEventHandler PropertyChanged;

        public void Save()
        {
            throw new NotImplementedException();
        }
    }

    class OutOfProcSVsServiceProvider : SVsServiceProvider
    {
        public object GetService(Type serviceType)
        {
            Debug.WriteLine($"GetService: {serviceType}");
            return null;
        }
    }

    public class LoggingCatalog : AggregateCatalog
    {
        public LoggingCatalog(params ComposablePartCatalog[] catalogs) : base(catalogs) { }

        public override IEnumerable<Tuple<ComposablePartDefinition, ExportDefinition>> GetExports(ImportDefinition definition)
        {
            var exports = base.GetExports(definition);
            if (exports.Count() == 0)
            {
                Debug.WriteLine($"No exports for {definition}");
            }

            return exports;
        }
    }
}
