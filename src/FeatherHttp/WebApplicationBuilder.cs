﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Microsoft.AspNetCore.Builder
{
    /// <summary>
    /// A builder for web applications and services.
    /// </summary>
    public class WebApplicationBuilder
    {
        private readonly IHostBuilder _hostBuilder;
        private readonly DeferredHostBuilder _deferredHostBuilder;
        private readonly DeferredWebHostBuilder _deferredWebHostBuilder;

        /// <summary>
        /// Creates a <see cref="WebApplicationBuilder"/>.
        /// </summary>
        public WebApplicationBuilder() : this(new HostBuilder())
        {

        }

        internal WebApplicationBuilder(IHostBuilder hostBuilder)
        {
            _hostBuilder = hostBuilder;

            Services = new ServiceCollection();

            // HACK: MVC and Identity do this horrible thing to get the hosting environment as an instance
            // from the service collection before it is built. That needs to be fixed...
            var environment = new WebHostEnvironment();
            Environment = environment;
            Services.AddSingleton(Environment);

            Configuration = new ConfigurationBuilder();
            Logging = new LoggingBuilder(Services);
            Server = _deferredWebHostBuilder = new DeferredWebHostBuilder(environment);
            Host = _deferredHostBuilder = new DeferredHostBuilder(environment);
        }

        /// <summary>
        /// Provides information about the web hosting environment an application is running.
        /// </summary>
        public IWebHostEnvironment Environment { get; }

        /// <summary>
        /// A collection of services for the application to compose. This is useful for adding user provided or framework provided services.
        /// </summary>
        public IServiceCollection Services { get; }

        /// <summary>
        /// A collection of configuration providers for the application to compose. This is useful for adding new configuration sources and providers.
        /// </summary>
        public IConfigurationBuilder Configuration { get; }

        /// <summary>
        /// A collection of logging providers for the applicaiton to compose. This is useful for adding new logging providers.
        /// </summary>
        public ILoggingBuilder Logging { get; }

        /// <summary>
        /// A builder for configuring server specific properties. 
        /// </summary>
        public IWebHostBuilder Server { get; }

        /// <summary>
        /// A builder for configure host specific properties.
        /// </summary>
        public IHostBuilder Host { get; }

        /// <summary>
        /// Builds the <see cref="WebApplication"/>.
        /// </summary>
        /// <returns>A configured <see cref="WebApplication"/>.</returns>
        public WebApplication Build()
        {
            WebApplication sourcePipeline = null;

            _deferredHostBuilder.ExecuteActions(_hostBuilder);

            _hostBuilder.ConfigureWebHostDefaults(web =>
            {
                // Make the default web host settings match and allow overrides
                web.UseEnvironment(Environment.EnvironmentName);
                web.UseContentRoot(Environment.ContentRootPath);
                web.UseSetting(WebHostDefaults.ApplicationKey, Environment.ApplicationName);
                web.UseSetting(WebHostDefaults.WebRootKey, Environment.WebRootPath);

                _deferredWebHostBuilder.ExecuteActions(web);

                web.Configure(destinationPipeline =>
                {
                    // The endpoints were already added on the outside
                    if (sourcePipeline.DataSources.Count > 0)
                    {
                        // The user did not register the routing middleware so wrap the entire
                        // destination pipeline in UseRouting() and UseEndpoints(), essentially:
                        // destination.UseRouting()
                        // destination.Run(source)
                        // destination.UseEndpoints()
                        if (sourcePipeline.RouteBuilder == null)
                        {
                            destinationPipeline.UseRouting();

                            // Copy the route data sources over to the destination pipeline, this should be available since we just called
                            // UseRouting()
                            var routes = (IEndpointRouteBuilder)destinationPipeline.Properties[WebApplication.EndpointRouteBuilder];
                            foreach (var ds in sourcePipeline.DataSources)
                            {
                                routes.DataSources.Add(ds);
                            }

                            // Chain the execution of the source pipeline into the destination pipeline
                            destinationPipeline.Use(next =>
                            {
                                sourcePipeline.Run(next);
                                return sourcePipeline.Build();
                            });

                            // Add a UseEndpoints at the end
                            destinationPipeline.UseEndpoints(e => { });
                        }
                        else
                        {
                            // Since we register routes into the source pipeline's route builder directly,
                            // if the user called UseRouting, we need to copy the data sources
                            foreach (var ds in sourcePipeline.DataSources)
                            {
                                sourcePipeline.RouteBuilder.DataSources.Add(ds);
                            }

                            // We then implicitly call UseEndpoints at the end of the pipeline
                            sourcePipeline.UseEndpoints(_ => { });

                            // Wire the source pipeline to run in the destination pipeline
                            destinationPipeline.Run(sourcePipeline.Build());
                        }
                    }
                    else
                    {
                        // Wire the source pipeline to run in the destination pipeline
                        destinationPipeline.Run(sourcePipeline.Build());
                    }

                    // Copy the properties to the destination app builder
                    foreach (var item in sourcePipeline.Properties)
                    {
                        destinationPipeline.Properties[item.Key] = item.Value;
                    }
                });
            });

            _hostBuilder.ConfigureServices(services =>
            {
                foreach (var s in Services)
                {
                    services.Add(s);
                }
            });

            _hostBuilder.ConfigureAppConfiguration((hostContext, builder) =>
            {
                foreach (var s in Configuration.Sources)
                {
                    builder.Sources.Add(s);
                }
            });

            var host = _hostBuilder.Build();

            return sourcePipeline = new WebApplication(host);
        }

        private class DeferredHostBuilder : IHostBuilder
        {
            private Action<IHostBuilder> _operations;

            public IDictionary<object, object> Properties { get; } = new Dictionary<object, object>();

            private readonly IConfigurationBuilder _hostConfiguration = new ConfigurationBuilder();

            private readonly WebHostEnvironment _environment;

            public DeferredHostBuilder(WebHostEnvironment environment)
            {
                _environment = environment;
            }

            public IHost Build()
            {
                return null;
            }

            public IHostBuilder ConfigureAppConfiguration(Action<HostBuilderContext, IConfigurationBuilder> configureDelegate)
            {
                _operations += b => b.ConfigureAppConfiguration(configureDelegate);
                return this;
            }

            public IHostBuilder ConfigureContainer<TContainerBuilder>(Action<HostBuilderContext, TContainerBuilder> configureDelegate)
            {
                _operations += b => b.ConfigureContainer(configureDelegate);
                return this;
            }

            public IHostBuilder ConfigureHostConfiguration(Action<IConfigurationBuilder> configureDelegate)
            {
                // HACK: We need to evaluate the host configuration as they are changes so that we have an accurate view of the world
                configureDelegate(_hostConfiguration);

                var config = _hostConfiguration.Build();

                _environment.ApplicationName = config[HostDefaults.ApplicationKey] ?? _environment.ApplicationName;
                _environment.ContentRootPath = config[HostDefaults.ContentRootKey] ?? _environment.ContentRootPath;
                _environment.EnvironmentName = config[HostDefaults.EnvironmentKey] ?? _environment.EnvironmentName;
                _environment.ResolveFileProviders();

                _operations += b => b.ConfigureHostConfiguration(configureDelegate);
                return this;
            }

            public IHostBuilder ConfigureServices(Action<HostBuilderContext, IServiceCollection> configureDelegate)
            {
                _operations += b => b.ConfigureServices(configureDelegate);
                return this;
            }

            public IHostBuilder UseServiceProviderFactory<TContainerBuilder>(IServiceProviderFactory<TContainerBuilder> factory)
            {
                _operations += b => b.UseServiceProviderFactory(factory);
                return this;
            }

            public IHostBuilder UseServiceProviderFactory<TContainerBuilder>(Func<HostBuilderContext, IServiceProviderFactory<TContainerBuilder>> factory)
            {
                _operations += b => b.UseServiceProviderFactory(factory);
                return this;
            }

            public void ExecuteActions(IHostBuilder hostBuilder)
            {
                _operations?.Invoke(hostBuilder);
            }
        }

        private class DeferredWebHostBuilder : IWebHostBuilder
        {
            private Action<IWebHostBuilder> _operations;

            private readonly WebHostEnvironment _environment;
            private readonly Dictionary<string, string> _settings = new Dictionary<string, string>();

            public DeferredWebHostBuilder(WebHostEnvironment environment)
            {
                _environment = environment;
            }

            IWebHost IWebHostBuilder.Build()
            {
                return null;
            }

            public IWebHostBuilder ConfigureAppConfiguration(Action<WebHostBuilderContext, IConfigurationBuilder> configureDelegate)
            {
                _operations += b => b.ConfigureAppConfiguration(configureDelegate);
                return this;
            }

            public IWebHostBuilder ConfigureServices(Action<WebHostBuilderContext, IServiceCollection> configureServices)
            {
                _operations += b => b.ConfigureServices(configureServices);
                return this;
            }

            public IWebHostBuilder ConfigureServices(Action<IServiceCollection> configureServices)
            {
                return ConfigureServices((WebHostBuilderContext context, IServiceCollection services) => configureServices(services));
            }

            public string GetSetting(string key)
            {
                _settings.TryGetValue(key, out var value);
                return value;
            }

            public IWebHostBuilder UseSetting(string key, string value)
            {
                _settings[key] = value;

                if (key == WebHostDefaults.ApplicationKey)
                {
                    _environment.ApplicationName = value;
                }
                else if (key == WebHostDefaults.ContentRootKey)
                {
                    _environment.ContentRootPath = value;
                    _environment.ResolveFileProviders();
                }
                else if (key == WebHostDefaults.EnvironmentKey)
                {
                    _environment.EnvironmentName = value;
                }
                else if (key == WebHostDefaults.WebRootKey)
                {
                    _environment.WebRootPath = value;
                    _environment.ResolveFileProviders();
                }

                _operations += b => b.UseSetting(key, value);
                return this;
            }

            public void ExecuteActions(IWebHostBuilder webHostBuilder)
            {
                _operations?.Invoke(webHostBuilder);
            }
        }

        private class LoggingBuilder : ILoggingBuilder
        {
            public LoggingBuilder(IServiceCollection services)
            {
                Services = services;
            }

            public IServiceCollection Services { get; }
        }

        private class WebHostEnvironment : IWebHostEnvironment
        {
            public WebHostEnvironment()
            {
                WebRootPath = "wwwroot";
                ContentRootPath = Directory.GetCurrentDirectory();
                ApplicationName = Assembly.GetEntryAssembly().GetName().Name;
                EnvironmentName = Environments.Development;
                ResolveFileProviders();
            }

            public void ResolveFileProviders()
            {
                var webRoot = Path.Combine(ContentRootPath, WebRootPath);
                ContentRootFileProvider = Directory.Exists(ContentRootPath) ? (IFileProvider)new PhysicalFileProvider(ContentRootPath) : new NullFileProvider();
                WebRootFileProvider = Directory.Exists(webRoot) ? (IFileProvider)new PhysicalFileProvider(webRoot) : new NullFileProvider();
            }

            public IFileProvider WebRootFileProvider { get; set; }
            public string WebRootPath { get; set; }
            public string ApplicationName { get; set; }
            public IFileProvider ContentRootFileProvider { get; set; }
            public string ContentRootPath { get; set; }
            public string EnvironmentName { get; set; }
        }
    }
}
