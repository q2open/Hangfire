﻿// This file is part of Hangfire.
// Copyright © 2013-2014 Sergey Odinokov.
// 
// Hangfire is free software: you can redistribute it and/or modify
// it under the terms of the GNU Lesser General Public License as 
// published by the Free Software Foundation, either version 3 
// of the License, or any later version.
// 
// Hangfire is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Lesser General Public License for more details.
// 
// You should have received a copy of the GNU Lesser General Public 
// License along with Hangfire. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Data.SqlClient;
using System.Linq;
using System.Text;
using System.Threading;
using Hangfire.Logging;
using Hangfire.Server;
using Hangfire.Storage;

namespace Hangfire.SqlServer
{
#pragma warning disable 618
    internal class ExpirationManager : IServerComponent
#pragma warning restore 618
    {
        private sealed class ProcessedTableDependentMapping
        {
            public string ParentTableName { get; private set; }
            public string MappedTableName { get; private set; }
            public string ParentTableKeyColumnName { get; private set; }
            public string MappedTableKeyName { get; private set; }

            public ProcessedTableDependentMapping(
                string parentTableName, 
                string mappedTableName,
                string parentTableKeyColumnName, 
                string mappedTableKeyName)
            {
                ParentTableName = parentTableName;
                MappedTableName = mappedTableName;
                ParentTableKeyColumnName = parentTableKeyColumnName;
                MappedTableKeyName = mappedTableKeyName;
            }
        }

        private static readonly ILog Logger = LogProvider.For<ExpirationManager>();

        private const string DistributedLockKey = "locks:expirationmanager";
        private static readonly TimeSpan DefaultLockTimeout = TimeSpan.FromMinutes(5);
        
        // This value should be high enough to optimize the deletion as much, as possible,
        // reducing the number of queries. But low enough to cause lock escalations (it
        // appears, when ~5000 locks were taken, but this number is a subject of version).
        // Note, that lock escalation may also happen during the cascade deletions for
        // State (3-5 rows/job usually) and JobParameters (2-3 rows/job usually) tables.
        private const int NumberOfRecordsInSinglePass = 1000;
        
        private static readonly string[] ProcessedTables =
        {
            "AggregatedCounter",
            "Job",
            "List",
            "Set",
            "Hash",
        };

        private static readonly List<ProcessedTableDependentMapping> DependentTables = new List<ProcessedTableDependentMapping>()
        {
            new ProcessedTableDependentMapping("Job", "JobParameter", "Id", "JobId"),
            new ProcessedTableDependentMapping("Job", "State", "Id", "JobId"),
            new ProcessedTableDependentMapping("Job", "JobQueue", "Id", "JobId")
        };

        private readonly SqlServerStorage _storage;
        private readonly TimeSpan _checkInterval;

        public ExpirationManager(SqlServerStorage storage, TimeSpan checkInterval)
        {
            if (storage == null) throw new ArgumentNullException(nameof(storage));

            _storage = storage;
            _checkInterval = checkInterval;
        }

        public void Execute(CancellationToken cancellationToken)
        {
            foreach (var table in ProcessedTables)
            {
                Logger.Debug($"Removing outdated records from the '{table}' table...");

                UseConnectionDistributedLock(_storage, connection =>
                {
                    int affected;

                    do
                    {
                        affected = ExecuteNonQuery(
                            connection,
                            GetQuery(_storage.SchemaName, table),
                            cancellationToken,
                            new SqlParameter("@count", NumberOfRecordsInSinglePass),
                            new SqlParameter("@now", DateTime.UtcNow));

                    } while (affected == NumberOfRecordsInSinglePass);
                });

                Logger.Trace($"Outdated records removed from the '{table}' table.");
            }

            cancellationToken.WaitHandle.WaitOne(_checkInterval);
        }

        public override string ToString()
        {
            return GetType().ToString();
        }

        private void UseConnectionDistributedLock(SqlServerStorage storage, Action<DbConnection> action)
        {
            try
            {
                storage.UseConnection(null, connection =>
                {
                    SqlServerDistributedLock.Acquire(connection, DistributedLockKey, DefaultLockTimeout);

                    try
                    {
                        action(connection);
                    }
                    finally
                    {
                        SqlServerDistributedLock.Release(connection, DistributedLockKey);
                    }
                });
            }
            catch (DistributedLockTimeoutException e) when (e.Resource == DistributedLockKey)
            {
                // DistributedLockTimeoutException here doesn't mean that outdated records weren't removed.
                // It just means another Hangfire server did this work.
                Logger.Log(
                    LogLevel.Debug,
                    () => $@"An exception was thrown during acquiring distributed lock on the {DistributedLockKey} resource within {DefaultLockTimeout.TotalSeconds} seconds. Outdated records were not removed.
It will be retried in {_checkInterval.TotalSeconds} seconds.",
                    e);
            }
        }

        private static string GetQuery(string schemaName, string table)
        {
            // Okay, let me explain all the bells and whistles in this query:
            //
            // SET TRANSACTION... is to prevent a query from running, when a
            // higher isolation level was set, for example, when it was leaked:
            // http://www.levibotelho.com/development/plugging-isolation-leaks-in-sql-server.
            //
            // LOOP JOIN hint is here to prevent merge or hash joins, that
            // cause index scan operators, and they are unacceptable, because
            // may block running background jobs.
            //
            // OPTIMIZE FOR instructs engine to generate better plan that
            // causes much fewer logical reads, because of additional sorting
            // before querying data in nested loops. The value was discovered
            // in practice.
            //
            // READPAST hint is used to simply skip blocked records, because
            // it's better to ignore them instead of waiting for unlock.
            //
            // TOP is to prevent lock escalations that may cause background
            // processing to stop, and to avoid larger batches to rollback
            // in case of connection/process termination.

            var tempTableName = "@recordsToDelete";
            var dependentTableDeleteStatements = GetDependentTableDeleteStatements(schemaName, tempTableName, table);

            return
$@"
set transaction isolation level read committed;

declare {tempTableName} table ([Id] int) 

insert into {tempTableName}([Id])
select top (@count) Id from [{schemaName}].[{table}] with (readpast) 
where ExpireAt < @now
option (loop join, optimize for (@count = 20000));

{dependentTableDeleteStatements}

delete [{table}] 
from [{schemaName}].[{table}] with (readpast) 
inner join {tempTableName} t on [{table}].[Id] = t.[Id]";
        }

        private static string GetDependentTableDeleteStatements(string schema, string tempTableName, string table)
        {
            var deleteStatements = new StringBuilder();

            foreach (var mapping in DependentTables.Where(x => x.ParentTableName == table).ToList())
            {
                deleteStatements.AppendLine();

                var dependentTableRecordsToDeleteTableName = $"@{mapping.MappedTableName}RecordsToDelete";
                deleteStatements.AppendLine($"declare {dependentTableRecordsToDeleteTableName} table ([Id] int)");
                deleteStatements.AppendLine($"insert into {dependentTableRecordsToDeleteTableName}([Id])");
                deleteStatements.AppendLine($"select [{mapping.MappedTableName}].Id from [{schema}].[{mapping.MappedTableName}] with (readpast)");
                deleteStatements.AppendLine($"inner join [{schema}].[{mapping.ParentTableName}] with (readpast)");
                deleteStatements.AppendLine($"on [{mapping.MappedTableName}].[{mapping.MappedTableKeyName}] = [{mapping.ParentTableName}].[{mapping.ParentTableKeyColumnName}]");
                deleteStatements.AppendLine($"inner join {tempTableName} t on [{mapping.ParentTableName}].[Id] = t.[Id]");

                deleteStatements.AppendLine();
                deleteStatements.AppendLine(GetDependentTableDeleteStatements(schema, dependentTableRecordsToDeleteTableName, mapping.MappedTableName));
                deleteStatements.AppendLine();

                deleteStatements.AppendLine($"delete [{mapping.MappedTableName}]");
                deleteStatements.AppendLine($"from [{schema}].[{mapping.MappedTableName}] with (readpast)");
                deleteStatements.AppendLine($"inner join {dependentTableRecordsToDeleteTableName} t");
                deleteStatements.AppendLine($"on [{mapping.MappedTableName}].[Id] = t.[Id]");
            }

            return deleteStatements.ToString();
        }

        private static int ExecuteNonQuery(
            DbConnection connection,
            string commandText,
            CancellationToken cancellationToken,
            params SqlParameter[] parameters)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = commandText;
                command.Parameters.AddRange(parameters);
                command.CommandTimeout = 0;

                using (cancellationToken.Register(state => ((SqlCommand)state).Cancel(), command))
                {
                    try
                    {
                        return command.ExecuteNonQuery();
                    }
                    catch (SqlException) when (cancellationToken.IsCancellationRequested)
                    {
                        // Exception was triggered due to the Cancel method call, ignoring
                        return 0;
                    }
                }
            }
        }
    }
}

