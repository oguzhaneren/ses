﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.SqlServer.Server;
using Ses.Abstracts;

namespace Ses.MsSql
{
    internal class MsSqlPersistor : IEventStreamPersistor
    {
        private readonly string _connectionString;
        private readonly ILogger _logger;

        public MsSqlPersistor(string connectionString, ILogger logger = null)
        {
            _connectionString = connectionString;
            _logger = logger;
        }

        public event OnReadEventHandler OnReadEvent;
        public event OnReadSnapshotHandler OnReadSnapshot;

        public async Task<IList<IEvent>> Load(Guid streamId, int fromVersion, bool pessimisticLock, CancellationToken cancellationToken = new CancellationToken())
        {
            var list = new List<IEvent>(50);
            using (var cnn = new SqlConnection(_connectionString))
            {
                using (var cmd = await cnn.OpenAndCreateCommandAsync(SqlQueries.SelectEvents.Query, cancellationToken).ConfigureAwait(false))
                {
                    cmd.CommandType = CommandType.StoredProcedure;
                    cmd
                        .AddInputParam(SqlQueries.SelectEvents.ParamStreamId, DbType.Guid, streamId)
                        .AddInputParam(SqlQueries.SelectEvents.ParamFromVersion, DbType.Int32, fromVersion)
                        .AddInputParam(SqlQueries.SelectEvents.ParamPessimisticLock, DbType.Boolean, pessimisticLock);

                    using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) // read snapshot
                        {
                            if (reader[0] == DBNull.Value) break;

                            // ReSharper disable once PossibleNullReferenceException
                            list.Add(await OnReadSnapshot(
                                streamId,
                                reader.GetString(0),
                                reader.GetInt32(1),
                                (byte[])reader[2]).ConfigureAwait(false));
                        }

                        await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);

                        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) // read events
                        {
                            // ReSharper disable once PossibleNullReferenceException
                            list.Add(await OnReadEvent(
                                streamId,
                                reader.GetString(0),
                                reader.GetInt32(1),
                                (byte[])reader[2]).ConfigureAwait(false));
                        }
                    }
                }
            }
            return list;
        }

        public async Task DeleteStream(Guid streamId, int expectedVersion, CancellationToken cancellationToken = new CancellationToken())
        {
            using (var cnn = new SqlConnection(_connectionString))
            {
                var query = expectedVersion == ExpectedVersion.Any
                    ? SqlQueries.DeleteStream.QueryAny
                    : SqlQueries.DeleteStream.QueryExpectedVersion;

                using (var cmd = await cnn.OpenAndCreateCommandAsync(query, cancellationToken).ConfigureAwait(false))
                {
                    try
                    {
                        await cmd
                            .AddInputParam(SqlQueries.DeleteStream.ParamStreamId, DbType.Guid, streamId)
                            .AddInputParam(SqlQueries.DeleteStream.ParamExpectedVersion, DbType.Int32, expectedVersion)
                            .ExecuteNonQueryAsync(cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (SqlException e)
                    {
                        if (e.Message.StartsWith("WrongExpectedVersion"))
                        {
                            throw new WrongExpectedVersionException($"Deleting stream {streamId} error", e);
                        }
                        throw;
                    }
                }
            }
        }

        public async Task UpdateSnapshot(Guid streamId, int version, string contractName, byte[] payload, CancellationToken cancellationToken = new CancellationToken())
        {
            using (var cnn = new SqlConnection(_connectionString))
            {
                try
                {
                    using (var cmd = await cnn.OpenAndCreateCommandAsync(SqlQueries.UpdateSnapshot.Query, cancellationToken).ConfigureAwait(false))
                    {
                        await cmd
                            .AddInputParam(SqlQueries.UpdateSnapshot.ParamStreamId, DbType.Guid, streamId)
                            .AddInputParam(SqlQueries.UpdateSnapshot.ParamVersion, DbType.Int32, version)
                            .AddInputParam(SqlQueries.UpdateSnapshot.ParamContractName, DbType.AnsiString, contractName)
                            .AddInputParam(SqlQueries.UpdateSnapshot.ParamGeneratedAtUtc, DbType.DateTime, DateTime.UtcNow)
                            .AddInputParam(SqlQueries.UpdateSnapshot.ParamPayload, DbType.Binary, payload)
                            .ExecuteNonQueryAsync(cancellationToken)
                            .ConfigureAwait(false);
                    }
                }
                catch (SqlException e)
                {
                    if (e.Message.StartsWith("WrongExpectedVersion"))
                    {
                        throw new WrongExpectedVersionException($"Updating snapshot for stream {streamId} error", e);
                    }
                    throw;
                }
            }
        }

        public async Task SaveChanges(Guid streamId, Guid commitId, int expectedVersion, IEnumerable<EventRecord> events, byte[] metadata, bool isLockable, CancellationToken cancellationToken = new CancellationToken())
        {
            var records = SqlQueries.InsertEvents.CreateSqlDataRecords(events.ToArray());
            using (var cnn = new SqlConnection(_connectionString))
            {
                switch (expectedVersion)
                {
                    case ExpectedVersion.NoStream:
                        await SaveChangesNoStream(cnn, records, streamId, commitId, metadata, isLockable, cancellationToken);
                        break;
                    case ExpectedVersion.Any:
                        await SaveChangesAny(cnn, records, streamId, commitId, metadata, isLockable, cancellationToken);
                        break;
                    default:
                        await SaveChangesExpectedVersion(cnn, records, streamId, commitId, expectedVersion, metadata, cancellationToken);
                        break;
                }
            }
        }

        private static async Task SaveChangesNoStream(SqlConnection cnn, IEnumerable<SqlDataRecord> records, Guid streamId, Guid commitId, byte[] metadata, bool isLockable, CancellationToken cancellationToken)
        {
            try
            {
                using (var cmd = await cnn.OpenAndCreateCommandAsync(SqlQueries.InsertEvents.QueryNoStream, cancellationToken).ConfigureAwait(false))
                {
                    cmd.CommandType = CommandType.StoredProcedure;
                    await cmd
                        .AddInputParam(SqlQueries.InsertEvents.ParamStreamId, DbType.Guid, streamId)
                        .AddInputParam(SqlQueries.InsertEvents.ParamCommitId, DbType.Guid, commitId)
                        .AddInputParam(SqlQueries.InsertEvents.ParamCreatedAtUtc, DbType.DateTime, DateTime.UtcNow)
                        .AddInputParam(SqlQueries.InsertEvents.ParamMetadataPayload, DbType.Binary, metadata, true)
                        .AddInputParam(SqlQueries.InsertEvents.ParamIsLockable, DbType.Boolean, isLockable)
                        .AddInputParam(SqlQueries.InsertEvents.ParamEvents, records)
                        .ExecuteNonQueryAsync(cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (SqlException e)
            {
                if (e.IsUniqueConstraintViolation() || e.IsWrongExpectedVersionRised())
                {
                    throw new WrongExpectedVersionException($"Saving new stream {streamId} error. Stream exists.", e);
                }
                throw;
            }
        }

        private static async Task SaveChangesAny(SqlConnection cnn, IEnumerable<SqlDataRecord> records, Guid streamId, Guid commitId, byte[] metadata, bool isLockable, CancellationToken cancellationToken)
        {
            try
            {
                using (var cmd = await cnn.OpenAndCreateCommandAsync(SqlQueries.InsertEvents.QueryAny, cancellationToken).ConfigureAwait(false))
                {
                    await cmd
                        .AddInputParam(SqlQueries.InsertEvents.ParamStreamId, DbType.Guid, streamId)
                        .AddInputParam(SqlQueries.InsertEvents.ParamCommitId, DbType.Guid, commitId)
                        .AddInputParam(SqlQueries.InsertEvents.ParamCreatedAtUtc, DbType.DateTime, DateTime.UtcNow)
                        .AddInputParam(SqlQueries.InsertEvents.ParamMetadataPayload, DbType.Binary, metadata, true)
                        .AddInputParam(SqlQueries.InsertEvents.ParamIsLockable, DbType.Boolean, isLockable)
                        .AddInputParam(SqlQueries.InsertEvents.ParamEvents, records)
                        .ExecuteNonQueryAsync(cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (SqlException e)
            {
                // TODO: check concurrency violation
                if(e.IsUniqueConstraintViolation() || e.IsWrongExpectedVersionRised())
                {
                    throw new WrongExpectedVersionException($"Saving new or existing stream {streamId} error. Stream exists.", e);
                }
                throw;
            }
        }

        private static async Task SaveChangesExpectedVersion(SqlConnection cnn, IEnumerable<SqlDataRecord> records, Guid streamId, Guid commitId, int expectedVersion, byte[] metadata, CancellationToken cancellationToken)
        {
            try
            {
                using (var cmd = await cnn.OpenAndCreateCommandAsync(SqlQueries.InsertEvents.QueryExpectedVersion, cancellationToken).ConfigureAwait(false))
                {
                    await cmd
                        .AddInputParam(SqlQueries.InsertEvents.ParamStreamId, DbType.Guid, streamId)
                        .AddInputParam(SqlQueries.InsertEvents.ParamCommitId, DbType.Guid, commitId)
                        .AddInputParam(SqlQueries.InsertEvents.ParamCreatedAtUtc, DbType.DateTime, DateTime.UtcNow)
                        .AddInputParam(SqlQueries.InsertEvents.ParamMetadataPayload, DbType.Binary, metadata, true)
                        .AddInputParam(SqlQueries.InsertEvents.ParamExpectedVersion, DbType.Int32, expectedVersion)
                        .AddInputParam(SqlQueries.InsertEvents.ParamEvents, records)
                        .ExecuteNonQueryAsync(cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (SqlException e)
            {
                // TODO: check concurrency violation
                if (e.IsUniqueConstraintViolation() || e.IsWrongExpectedVersionRised())
                {
                    throw new WrongExpectedVersionException($"Saving new or existing stream {streamId} error. Stream exists.", e);
                }
                throw;
            }
        }
    }
}
