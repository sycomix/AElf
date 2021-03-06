﻿using System;
using System.Threading.Tasks;
using AElf.Network;
using AElf.Network.Peers;
using AElf.RPC.Hubs.Net;
using Microsoft.AspNetCore.Hosting;
using Autofac;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using NLog;

namespace AElf.RPC
{
    public class RpcServer : IRpcServer
    {
        private IWebHost _host;
        private readonly ILogger _logger;

        public RpcServer(ILogger logger)
        {
            _logger = logger;
        }

        public bool Init(ILifetimeScope scope, string rpcHost, int rpcPort)
        {
            try
            {
                var url = "http://" + rpcHost + ":" + rpcPort;

                _host = new WebHostBuilder()
                    .UseKestrel(options =>
                        {
                            options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(20);
                            options.Limits.MaxConcurrentConnections = 200;
                        }
                    )
                    .UseUrls(url)
                    .ConfigureServices(sc =>
                    {
                        sc.AddSingleton(scope.Resolve<INetworkManager>());
                        sc.AddSingleton(scope.Resolve<IPeerManager>());

                        sc.AddSignalRCore();
                        sc.AddSignalR();

                        sc.AddScoped<NetContext>();

                        RpcServerHelpers.ConfigureServices(sc, scope);
                    })
                    .Configure(ab =>
                    {
                        ab.UseSignalR(routes => { routes.MapHub<NetworkHub>("/events/net"); });

                        RpcServerHelpers.Configure(ab, scope);
                    })
                    .Build();

                _host.Services.GetService<NetContext>();
            }
            catch (Exception e)
            {
                _logger?.Error(e, "Exception while RPC server init.");
                return false;
            }

            return true;
        }

        public async Task Start()
        {
            try
            {
                _logger?.Info("RPC server start.");
                await _host.RunAsync();
            }
            catch (Exception e)
            {
                _logger?.Error(e, "Exception while start RPC server.");
            }
        }

        public void Stop()
        {
            _host.StopAsync();
        }
    }
}