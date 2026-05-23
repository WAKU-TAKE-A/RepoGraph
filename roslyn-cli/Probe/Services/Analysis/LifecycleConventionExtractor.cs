using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;

namespace Probe.Services.Analysis
{
    internal static class LifecycleConventionExtractor
    {
        public static IReadOnlyList<FrameworkEntrypoint> GetEntrypoints(IMethodSymbol method)
        {
            var entrypoints = new List<FrameworkEntrypoint>();
            var methodName = method.Name;
            var containingTypeName = method.ContainingType?.Name ?? "";
            var containingTypeFqn = method.ContainingType?.ToDisplayString() ?? "";

            if (string.Equals(methodName, "Main", StringComparison.Ordinal) ||
                string.Equals(methodName, "MainAsync", StringComparison.Ordinal))
            {
                entrypoints.Add(new FrameworkEntrypoint(
                    FrameworkRuleCatalog.DotNetRuntimeEntrypoint,
                    "framework::dotnet.runtime.entrypoint",
                    "Entrypoint",
                    "Framework.Runtime",
                    "DotNetRuntime"));
            }

            if (string.Equals(methodName, "CreateHostBuilder", StringComparison.Ordinal) ||
                string.Equals(methodName, "CreateWebHostBuilder", StringComparison.Ordinal))
            {
                entrypoints.Add(new FrameworkEntrypoint(
                    FrameworkRuleCatalog.DotNetHostBuilder,
                    $"framework::dotnet.host_builder.{methodName}",
                    methodName,
                    "Framework.Runtime",
                    "DotNetHostBuilder"));
            }

            if (string.Equals(containingTypeName, "Startup", StringComparison.Ordinal) &&
                string.Equals(methodName, "ConfigureServices", StringComparison.Ordinal))
            {
                entrypoints.Add(new FrameworkEntrypoint(
                    FrameworkRuleCatalog.AspNetStartupConfigureServices,
                    "framework::aspnet.startup.ConfigureServices",
                    "ConfigureServices",
                    "Framework.AspNetCore",
                    "Startup"));
            }

            if (string.Equals(containingTypeName, "Startup", StringComparison.Ordinal) &&
                string.Equals(methodName, "Configure", StringComparison.Ordinal))
            {
                entrypoints.Add(new FrameworkEntrypoint(
                    FrameworkRuleCatalog.AspNetStartupConfigure,
                    "framework::aspnet.startup.Configure",
                    "Configure",
                    "Framework.AspNetCore",
                    "Startup"));
            }

            if (LooksLikeUiApplicationLifecycle(containingTypeName, containingTypeFqn, method))
            {
                entrypoints.Add(new FrameworkEntrypoint(
                    FrameworkRuleCatalog.UiLifecycleEntrypoint,
                    $"framework::ui.lifecycle.{methodName}",
                    methodName,
                    "Framework.UI",
                    "ApplicationLifecycle"));
            }

            return entrypoints;
        }

        private static bool LooksLikeUiApplicationLifecycle(string containingTypeName, string containingTypeFqn, IMethodSymbol method)
        {
            var lifecycleMethods = new HashSet<string>(StringComparer.Ordinal)
            {
                "OnStartup",
                "OnActivated",
                "OnLaunched",
                "OnBackgroundActivated",
                "OnFrameworkInitializationCompleted",
                "OnExit",
                "OnNavigatedTo",
                "OnNavigatedFrom",
                "OnAppearing",
                "OnDisappearing",
                "OnInitialized"
            };

            if (!lifecycleMethods.Contains(method.Name))
            {
                return false;
            }

            return string.Equals(containingTypeName, "App", StringComparison.Ordinal) ||
                   IsOrDerivedFrom(method.ContainingType, "Windows.UI.Xaml.Application") ||
                   IsOrDerivedFrom(method.ContainingType, "System.Windows.Application") ||
                   containingTypeFqn.EndsWith(".App", StringComparison.Ordinal);
        }

        private static bool IsOrDerivedFrom(ITypeSymbol? type, string baseTypeFqn)
        {
            var current = type;
            while (current != null)
            {
                if (string.Equals(current.ToDisplayString(), baseTypeFqn, StringComparison.Ordinal))
                {
                    return true;
                }

                current = current.BaseType;
            }

            return false;
        }
    }
}
