// Copyright ?2013-2015 Sergey Odinokov, Marco Casamento
// This software is based on https://github.com/HangfireIO/Hangfire.Redis

// Hangfire.Redis.StackExchange is free software: you can redistribute it and/or modify
// it under the terms of the GNU Lesser General Public License as
// published by the Free Software Foundation, either version 3
// of the License, or any later version.
//
// Hangfire.Redis.StackExchange is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Lesser General Public License for more details.
//
// You should have received a copy of the GNU Lesser General Public
// License along with Hangfire.Redis.StackExchange. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Linq;
using Hangfire.Annotations;
using Hangfire.Dashboard;
using Hangfire.Logging;
using Hangfire.Server;
using Hangfire.States;
using Hangfire.Storage;
using StackExchange.Redis;

namespace Hangfire.Redis.StackExchange
{
    public class RedisStorage : JobStorage
    {
        // Make sure in Redis Cluster all transaction are in the same slot !!
        private readonly RedisStorageOptions _options;
        private readonly IConnectionMultiplexer _connectionMultiplexer;
        private readonly RedisSubscription _subscription;
        private readonly ConfigurationOptions _redisOptions;

        public RedisStorage()
            : this("localhost:6379")
        {
        }

        public RedisStorage(IConnectionMultiplexer connectionMultiplexer, RedisStorageOptions options = null)
        {
            _options = options ?? new RedisStorageOptions();

            _connectionMultiplexer = connectionMultiplexer ?? throw new ArgumentNullException(nameof(connectionMultiplexer));
            _redisOptions = ConfigurationOptions.Parse(_connectionMultiplexer.Configuration);
            
            _subscription = new RedisSubscription(this, _connectionMultiplexer.GetSubscriber());
        }

        public RedisStorage(string connectionString, RedisStorageOptions options = null)
        {
            if (connectionString == null) throw new ArgumentNullException(nameof(connectionString));

            _redisOptions = ConfigurationOptions.Parse(connectionString);

            _options = options ?? new RedisStorageOptions
            {
                Db = _redisOptions.DefaultDatabase ?? 0
            };

            _connectionMultiplexer = ConnectionMultiplexer.Connect(connectionString);
            _subscription = new RedisSubscription(this, _connectionMultiplexer.GetSubscriber());
        }

        public string ConnectionString => _connectionMultiplexer.Configuration;

        public int Db => _options.Db;
        
        internal int SucceededListSize => _options.SucceededListSize;

        internal int DeletedListSize => _options.DeletedListSize;
        
        internal string SubscriptionChannel => _subscription.Channel;

        internal string[] LifoQueues => _options.LifoQueues;

        internal bool UseTransactions => _options.UseTransactions;

        public override IMonitoringApi GetMonitoringApi()
        {
            return new RedisMonitoringApi(this, _connectionMultiplexer.GetDatabase(Db));
        }

        public override IStorageConnection GetConnection()
        {
            return new RedisConnection(this, _connectionMultiplexer.GetDatabase(Db), _subscription, _options.FetchTimeout);
        }

#pragma warning disable 618
        public override IEnumerable<IServerComponent> GetComponents()
#pragma warning restore 618
        {
            yield return new FetchedJobsWatcher(this, _options.InvisibilityTimeout);
            yield return new ExpiredJobsWatcher(this, _options.ExpiryCheckInterval);
            yield return _subscription;
        }

        public static DashboardMetric GetDashboardMetricFromRedisInfo(string title, string key)
        {
            return new DashboardMetric("redis:" + key, title, (razorPage) =>
            {
                using (var redisCnn = razorPage.Storage.GetConnection())
                {
                    var db = (redisCnn as RedisConnection).Redis;
                    var cnnMultiplexer = db.Multiplexer;
                    var srv = cnnMultiplexer.GetServer(db.IdentifyEndpoint());
                    var rawInfo = srv.InfoRaw().Split('\n')
                        .Where(x => x.Contains(':'))
                        .ToDictionary(x => x.Split(':')[0], x => x.Split(':')[1]);

                    return new Metric(rawInfo[key]);
                }
            });
        }

        public override IEnumerable<IStateHandler> GetStateHandlers()
        {
            yield return new FailedStateHandler();
            yield return new ProcessingStateHandler();
            yield return new SucceededStateHandler();
            yield return new DeletedStateHandler();
        }

        public override void WriteOptionsToLog(ILog logger)
        {
            logger.Debug("Using the following options for Redis job storage:");

            var connectionString = _redisOptions.ToString(includePassword: false);
            logger.DebugFormat("ConnectionString: {0}\nDN: {1}", connectionString, Db);
        }

        public override string ToString()
        {
            var connectionString = _redisOptions.ToString(includePassword: false);
            return string.Format("redis://{0}/{1}", connectionString, Db);
        }

        internal string GetRedisKey([NotNull] string key)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));

            return _options.Prefix + key;
        }
    }
}