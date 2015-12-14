﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using RethinkDb.Driver.Ast;
using RethinkDb.Driver.Utils;

namespace RethinkDb.Driver.Net.Clustering
{
    public class ConnectionPool : IConnection
    {
        private string authKey;
        private string dbname;
        private string[] seeds;
        private bool discover;
        private IPoolingStrategy poolingStrategy;

        private Func<ReqlAst, object, Task> RunAtom;

        #region REQL AST RUNNERS

        Task<dynamic> IConnection.RunAsync<T>(ReqlAst term, object globalOpts)
        {
            return poolingStrategy.RunAsync<T>(term, globalOpts);
        }

        Task<Cursor<T>> IConnection.RunCursorAsync<T>(ReqlAst term, object globalOpts)
        {
            return poolingStrategy.RunCursorAsync<T>(term, globalOpts);
        }

        Task<T> IConnection.RunAtomAsync<T>(ReqlAst term, object globalOpts)
        {
            return poolingStrategy.RunAtomAsync<T>(term, globalOpts);
        }

        void IConnection.RunNoReply(ReqlAst term, object globalOpts)
        {
            poolingStrategy.RunNoReply(term, globalOpts);
        }

        #endregion

        internal ConnectionPool(Builder builder)
        {
            authKey = builder._authKey;
            dbname = builder._dbname;
            seeds = builder.seeds;
            discover = builder._discover;
            poolingStrategy = builder.hostpool;
        }

        public void shutdown()
        {
            shutdownSignal?.Cancel();
            poolingStrategy?.Shutdown();

            if ( poolingStrategy != null )
            {
                //shutdown all connections.
                foreach( var h in poolingStrategy.HostList )
                {
                    ((Connection)h.conn).close(false);
                }
            }
        }

        private CancellationTokenSource shutdownSignal;
        private TaskCompletionSource<ConnectionPool> poolReady;

        protected virtual void StartPool()
        {
            shutdownSignal = new CancellationTokenSource();
            poolReady = new TaskCompletionSource<ConnectionPool>();

            var initialSeeds = this.seeds.Select(s =>
                {
                    var parts = s.Split(new[] {':'}, StringSplitOptions.RemoveEmptyEntries);
                    var host = parts[0];

                    IPAddress.Parse(host); //make sure it's an IP address.

                    var port = parts.Length == 2 ? int.Parse(parts[1]) : RethinkDBConstants.DEFAULT_PORT;

                    var conn = NewConnection(host, port);
                    return new {conn, host = s};
                });

            foreach( var conn in initialSeeds )
            {
                this.poolingStrategy.AddHost(conn.host, conn.conn);
            }

            Task.Factory.StartNew(SuperviseDeadHosts, TaskCreationOptions.LongRunning);
            if( discover )
            {
                Task.Factory.StartNew(DiscoverNewHosts, TaskCreationOptions.LongRunning);
            }
        }

        private void DiscoverNewHosts()
        {
            var r = RethinkDB.r;

            var changeFeed = r.db("rethinkdb").table("server_status").changes()[new { include_initial = true }];


            while ( true )
            {
                if( shutdownSignal.IsCancellationRequested )
                {
                    Log.Debug($"{nameof(DiscoverNewHosts)}: Shutdown Signal Received");
                    break;
                }

                try
                {
                    poolReady.Task.Wait();
                }
                catch
                {
                    Log.Trace($"{nameof(DiscoverNewHosts)}: Pool is not ready to discover new hosts.");
                    Thread.Sleep(1000);
                }

                try
                {
                    var cursor = changeFeed.runChanges<Server>(this);

                    foreach ( var change in cursor )
                    {
                        if( change.NewValue != null )
                        {
                            //Ok, could be initial or new host.
                            //either way, see if we need to add
                            //the connection.

                            var server = change.NewValue;
                            var port = server.Network.ReqlPort;

                            var realAddresses = server.Network.CanonicalAddress
                                .Where(s => // no localhost and no ipv6. for now.
                                    !s.Host.StartsWith("127.0.0.1") &&
                                    !s.Host.Contains(":"))
                                .Select(c => c.Host);

                            //now do any of the real
                            //addresses match the ones already in
                            //the host list?
                            var hlist = poolingStrategy.HostList;

                            if( !realAddresses.Any(ip => hlist.Any(s => s.Host.Contains(ip))) )
                            {
                                //the host IP is not found, so, see if we can connect?
                                foreach( var ip in realAddresses )
                                {
                                    var client = new TcpClient();
                                    try
                                    {
                                        client.Connect(ip, port);
                                    }
                                    catch
                                    {
                                        continue;
                                    }
                                    if( client.Connected )
                                    {
                                        client.Shutdown();
                                        //good chance we can connect to it then.
                                        var conn = NewConnection(ip, port);
                                        var host = $"{ip}:{port}";
                                        this.poolingStrategy.AddHost(host, conn);
                                        Log.Trace($"{nameof(DiscoverNewHosts)}: Server {server.Name} ({host}) was added to the host pool.");
                                    }
                                }
                            }
                            else
                            {
                                Log.Trace($"{nameof(DiscoverNewHosts)}: Server {server.Name} is back, but doesn't need to be added to the pool.");
                            }
                        }
                    }
                }
                catch
                {
                    Log.Trace($"{nameof(DiscoverNewHosts)}: Change feed broke.");
                }
            }
        }

        private void SuperviseDeadHosts()
        {
            var restartWorkers = new List<Task>();

            while ( true )
            {
                if( shutdownSignal.IsCancellationRequested )
                {
                    Log.Debug($"{nameof(SuperviseDeadHosts)}: Shutdown Signal Received");
                    break;
                }

                var hlist = poolingStrategy.HostList;

                for( int i = 0; i < hlist.Length; i++ )
                {
                    var he = hlist[i];
                    if( he.Dead && he.NextRetry < DateTime.Now)
                    {
                        var conn = he.conn as Connection;

                        var worker = Task.Run(() =>
                            {
                                conn.reconnect();

                                if( conn.Open )
                                {
                                    he.Dead = false;
                                }
                                else
                                {
                                    he.UpdateRetry();
                                }
                            });

                        restartWorkers.Add(worker);
                    }
                }

                if( restartWorkers.Any() )
                {
                    Task.WaitAll(restartWorkers.ToArray());
                }
                else
                {
                    Thread.Sleep(1000);
                }
            }
        }

        protected virtual Connection NewConnection(string hostname, int port)
        {
            return new Connection(new Connection.Builder(() => new ConnectionInstance())
                {
                    _authKey = authKey,
                    _dbname = dbname,
                    _hostname = hostname,
                    _port = port
                });
        }


        public static Builder build()
        {
            return new Builder();
        }

        public class Builder
        {
            internal bool _discover;
            internal string[] seeds;
            internal string _dbname;
            internal string _authKey;
            internal IPoolingStrategy hostpool;
            internal TimeSpan _retrywait;

            /// <summary>
            /// Should be strings of the form "Host:Port".
            /// </summary>
            public Builder seed(string[] seeds)
            {
                this.seeds = seeds;
                return this;
            }

            /// <summary>
            /// discover() is used to enable host discovery, when true the driver
            /// will attempt to discover any new nodes added to the cluster and then
            /// start sending queries to these new nodes.
            /// </summary>
            public Builder discover(bool discoverNewHosts)
            {
                this._discover = discoverNewHosts;
                return this;
            }

            public virtual Builder db(string val)
            {
                this._dbname = val;
                return this;
            }

            public virtual Builder authKey(string val)
            {
                this._authKey = val;
                return this;
            }

            //public virtual Builder retry(TimeSpan retryWait)
            //{
            //    this._retrywait = retryWait;
            //    return this;
            //}

            /// <summary>
            /// The selection strategy to for selecting a connection. IE: RoundRobin, HeartBeat, or EpsilonGreedy.
            /// </summary>
            public Builder selectionStrategy(IPoolingStrategy hostPool)
            {
                this.hostpool = hostPool;
                return this;
            }

            public virtual ConnectionPool connect()
            {
                var conn = new ConnectionPool(this);
                conn.StartPool();
                return conn;
            }

            public virtual Task<ConnectionPool> connectAsync()
            {
                var conn = new ConnectionPool(this);
                conn.StartPool();
                return conn.poolReady.Task;
            }
        }
    }
}