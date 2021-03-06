﻿// -----------------------------------------------------------------------
//  <copyright file="AdminDatabasesHandler.cs" company="Hibernating Rhinos LTD">
//      Copyright (c) Hibernating Rhinos LTD. All rights reserved.
//  </copyright>
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http.Features.Authentication;
using NCrontab.Advanced;
using Raven.Client.Documents.Commands;
using Raven.Client.Documents.Conventions;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Session;
using Raven.Client.Documents.Smuggler;
using Raven.Client.Exceptions;
using Raven.Client.Exceptions.Database;
using Raven.Client.Http;
using Raven.Client.Json.Converters;
using Raven.Client.ServerWide;
using Raven.Client.ServerWide.Commands;
using Raven.Client.ServerWide.ETL;
using Raven.Client.ServerWide.Operations;
using Raven.Client.ServerWide.Operations.ConnectionStrings;
using Raven.Client.ServerWide.PeriodicBackup;
using Raven.Server.Commercial;
using Raven.Server.Documents;
using Raven.Server.Json;
using Raven.Server.Routing;
using Raven.Server.ServerWide;
using Raven.Server.ServerWide.Context;
using Sparrow.Json;
using Sparrow.Json.Parsing;
using Raven.Server.Documents.Patch;
using Raven.Server.Documents.PeriodicBackup;
using Raven.Server.Documents.PeriodicBackup.Aws;
using Raven.Server.Documents.PeriodicBackup.Azure;
using Raven.Server.Rachis;
using Raven.Server.Smuggler.Migration;
using Raven.Server.ServerWide.Commands;
using Raven.Server.Smuggler.Documents;
using Raven.Client.Extensions;
using Raven.Server.Config.Categories;
using Raven.Server.Monitoring.Snmp.Objects.Database;
using Raven.Server.Utils;
using Sparrow;
using Sparrow.Utils;
using Voron.Impl;
using Constants = Raven.Client.Constants;
using DatabaseSmuggler = Raven.Server.Smuggler.Documents.DatabaseSmuggler;

namespace Raven.Server.Web.System
{
    public class AdminDatabasesHandler : RequestHandler
    {
        [RavenAction("/admin/databases", "GET", AuthorizationStatus.Operator)]
        public Task Get()
        {
            var name = GetQueryStringValueAndAssertIfSingleAndNotEmpty("name");

            using (ServerStore.ContextPool.AllocateOperationContext(out TransactionOperationContext context))
            {
                var dbId = Constants.Documents.Prefix + name;
                using (context.OpenReadTransaction())
                using (var dbDoc = ServerStore.Cluster.Read(context, dbId, out long etag))
                {
                    if (dbDoc == null)
                    {
                        HttpContext.Response.StatusCode = (int)HttpStatusCode.NotFound;
                        HttpContext.Response.Headers["Database-Missing"] = name;
                        using (var writer = new BlittableJsonTextWriter(context, HttpContext.Response.Body))
                        {
                            context.Write(writer,
                                new DynamicJsonValue
                                {
                                    ["Type"] = "Error",
                                    ["Message"] = "Database " + name + " wasn't found"
                                });
                        }
                        return Task.CompletedTask;
                    }

                    using (var writer = new BlittableJsonTextWriter(context, ResponseBodyStream()))
                    {
                        writer.WriteStartObject();
                        writer.WriteDocumentPropertiesWithoutMetdata(context, new Document
                        {
                            Data = dbDoc
                        });
                        writer.WriteComma();
                        writer.WritePropertyName("Etag");
                        writer.WriteInteger(etag);
                        writer.WriteEndObject();
                    }
                }
            }

            return Task.CompletedTask;
        }

        // add database to already existing database group
        [RavenAction("/admin/databases/node", "PUT", AuthorizationStatus.Operator)]
        public async Task AddDatabaseNode()
        {
            var name = GetQueryStringValueAndAssertIfSingleAndNotEmpty("name");
            var node = GetStringQueryString("node", false);
            var mentor = GetStringQueryString("mentor", false);

            string errorMessage;
            if (ResourceNameValidator.IsValidResourceName(name, ServerStore.Configuration.Core.DataDirectory.FullPath, out errorMessage) == false)
                throw new BadRequestException(errorMessage);

            ServerStore.EnsureNotPassive();
            using (ServerStore.ContextPool.AllocateOperationContext(out TransactionOperationContext context))
            using (context.OpenReadTransaction())
            {
                var databaseRecord = ServerStore.Cluster.ReadDatabase(context, name, out var index);
                var clusterTopology = ServerStore.GetClusterTopology(context);

                if (databaseRecord.Encrypted &&
                    ServerStore.LicenseManager.CanCreateEncryptedDatabase(out var licenseLimit) == false)
                {
                    SetLicenseLimitResponse(licenseLimit);
                    return;
                }

                // the case where an explicit node was requested 
                if (string.IsNullOrEmpty(node) == false)
                {
                    if (databaseRecord.Topology.RelevantFor(node))
                        throw new InvalidOperationException($"Can't add node {node} to {name} topology because it is already part of it");

                    var url = clusterTopology.GetUrlFromTag(node);
                    if (url == null)
                        throw new InvalidOperationException($"Can't add node {node} to {name} topology because node {node} is not part of the cluster");

                    if (databaseRecord.Encrypted && NotUsingHttps(url))
                        throw new InvalidOperationException($"Can't add node {node} to database {name} topology because database {name} is encrypted but node {node} doesn't have an SSL certificate.");

                    databaseRecord.Topology.Promotables.Add(node);
                    databaseRecord.Topology.DemotionReasons[node] = "Joined the Db-Group as a new promotable node";
                    databaseRecord.Topology.PromotablesStatus[node] = DatabasePromotionStatus.WaitingForFirstPromotion;
                }

                //The case were we don't care where the database will be added to
                else
                {
                    var allNodes = clusterTopology.Members.Keys
                        .Concat(clusterTopology.Promotables.Keys)
                        .Concat(clusterTopology.Watchers.Keys)
                        .ToList();

                    allNodes.RemoveAll(n => databaseRecord.Topology.AllNodes.Contains(n) || (databaseRecord.Encrypted && NotUsingHttps(clusterTopology.GetUrlFromTag(n))));

                    if (databaseRecord.Encrypted && allNodes.Count == 0)
                        throw new InvalidOperationException($"Database {name} is encrypted and requires a node which supports SSL. There is no such node available in the cluster.");

                    if (allNodes.Count == 0)
                        throw new InvalidOperationException($"Database {name} already exists on all the nodes of the cluster");

                    var rand = new Random().Next();
                    node = allNodes[rand % allNodes.Count];

                    databaseRecord.Topology.Promotables.Add(node);
                    databaseRecord.Topology.DemotionReasons[node] = "Joined the Db-Group as a new promotable node";
                    databaseRecord.Topology.PromotablesStatus[node] = DatabasePromotionStatus.WaitingForFirstPromotion;
                }

                if (mentor != null)
                {
                    if (databaseRecord.Topology.RelevantFor(mentor) == false)
                        throw new ArgumentException($"The node {mentor} is not part of the database group");
                    if (databaseRecord.Topology.Members.Contains(mentor) == false)
                        throw new ArgumentException($"The node {mentor} is not vaild for the operation because it is not a member");
                    databaseRecord.Topology.PredefinedMentors.Add(node, mentor);
                }

                databaseRecord.Topology.ReplicationFactor++;
                var (newIndex, _) = await ServerStore.WriteDatabaseRecordAsync(name, databaseRecord, index);

                await WaitForExecutionOnSpecificNode(context, clusterTopology, node, newIndex);

                HttpContext.Response.StatusCode = (int)HttpStatusCode.Created;

                using (var writer = new BlittableJsonTextWriter(context, ResponseBodyStream()))
                {
                    context.Write(writer, new DynamicJsonValue
                    {
                        [nameof(DatabasePutResult.RaftCommandIndex)] = newIndex,
                        [nameof(DatabasePutResult.Name)] = name,
                        [nameof(DatabasePutResult.Topology)] = databaseRecord.Topology.ToJson()
                    });
                    writer.Flush();
                }
            }
        }

        public bool NotUsingHttps(string url)
        {
            return url.StartsWith("https:", StringComparison.OrdinalIgnoreCase) == false;
        }

        [RavenAction("/admin/databases", "PUT", AuthorizationStatus.Operator)]
        public async Task Put()
        {
            var name = GetQueryStringValueAndAssertIfSingleAndNotEmpty("name");
            if (ResourceNameValidator.IsValidResourceName(name, ServerStore.Configuration.Core.DataDirectory.FullPath, out string errorMessage) == false)
                throw new BadRequestException(errorMessage);

            ServerStore.EnsureNotPassive();
            using (ServerStore.ContextPool.AllocateOperationContext(out TransactionOperationContext context))
            {
                context.OpenReadTransaction();

                var index = GetLongFromHeaders("ETag");
                var replicationFactor = GetIntValueQueryString("replication-factor", required: false) ?? 0;
                var json = context.ReadForDisk(RequestBodyStream(), name);
                var databaseRecord = JsonDeserializationCluster.DatabaseRecord(json);
                if ((databaseRecord.Topology?.DynamicNodesDistribution ?? false) &&
                    Server.ServerStore.LicenseManager.CanDynamicallyDistributeNodes(out var licenseLimit) == false)
                {
                    SetLicenseLimitResponse(licenseLimit);
                    return;
                }

                var (newIndex, topology, nodeUrlsAddedTo) = await CreateDatabase(name, databaseRecord, context, replicationFactor, index);

                HttpContext.Response.StatusCode = (int)HttpStatusCode.Created;

                using (var writer = new BlittableJsonTextWriter(context, ResponseBodyStream()))
                {
                    context.Write(writer, new DynamicJsonValue
                    {
                        [nameof(DatabasePutResult.RaftCommandIndex)] = newIndex,
                        [nameof(DatabasePutResult.Name)] = name,
                        [nameof(DatabasePutResult.Topology)] = topology.ToJson(),
                        [nameof(DatabasePutResult.NodesAddedTo)] = nodeUrlsAddedTo
                    });
                    writer.Flush();
                }
            }
        }

        private async Task<(long, DatabaseTopology, List<string>)> CreateDatabase(string name, DatabaseRecord databaseRecord, TransactionOperationContext context, int replicationFactor, long? index)
        {
            var existingDatabaseRecord = ServerStore.Cluster.ReadDatabase(context, name, out long _);

            if (index.HasValue && existingDatabaseRecord == null)
                throw new BadRequestException($"Attempted to modify non-existing database: '{name}'");

            if (existingDatabaseRecord != null && index.HasValue == false)
                throw new ConcurrencyException($"Database '{name}' already exists!");

            var nodeUrlsAddedTo = new List<string>();
            try
            {
                DatabaseHelper.Validate(name, databaseRecord);
            }
            catch (Exception e)
            {
                throw new BadRequestException("Database document validation failed.", e);
            }
            var clusterTopology = ServerStore.GetClusterTopology(context);
            ValidateClusterMembers(clusterTopology, databaseRecord);

            DatabaseTopology topology;
            if (databaseRecord.Topology?.Members?.Count > 0)
            {
                topology = databaseRecord.Topology;
                foreach (var member in topology.Members)
                {
                    var nodeUrl = clusterTopology.GetUrlFromTag(member);
                    if (nodeUrl == null)
                        throw new ArgumentException($"Failed to add node {member}, becasue we don't have it in the cluster.");
                    nodeUrlsAddedTo.Add(nodeUrl);
                }
            }
            else
            {
                var factor = Math.Max(1, replicationFactor);
                databaseRecord.Topology = topology = AssignNodesToDatabase(context, factor, name, databaseRecord.Encrypted, out nodeUrlsAddedTo);
            }
            topology.ReplicationFactor = topology.Members.Count;
            var (newIndex, _) = await ServerStore.WriteDatabaseRecordAsync(name, databaseRecord, index);

            await WaitForExecutionOnRelevantNodes(context, name, clusterTopology, databaseRecord.Topology?.Members, newIndex);
            return (newIndex, topology, nodeUrlsAddedTo);
        }

        [RavenAction("/admin/databases/reorder", "POST", AuthorizationStatus.Operator)]
        public async Task Reorder()
        {
            var name = GetStringQueryString("name");
            using (ServerStore.ContextPool.AllocateOperationContext(out TransactionOperationContext context))
            {
                var record = ServerStore.LoadDatabaseRecord(name, out var _);
                if (record == null)
                {
                    DatabaseDoesNotExistException.Throw(name);
                }
                var json = await context.ReadForMemoryAsync(RequestBodyStream(), "nodes");
                var parameters = JsonDeserializationServer.Parameters.MembersOrder(json);

                if (record.Topology.Members.Count != parameters.MembersOrder.Count
                    || record.Topology.Members.All(parameters.MembersOrder.Contains) == false)
                {
                    throw new ArgumentException("The reordered list doesn't correspond to the existing members of the database group.");
                }
                record.Topology.Members = parameters.MembersOrder;

                var reorder = new UpdateTopologyCommand
                {
                    DatabaseName = name,
                    Topology = record.Topology
                };

                var res = await ServerStore.SendToLeaderAsync(reorder);
                await ServerStore.WaitForCommitIndexChange(RachisConsensus.CommitIndexModification.GreaterOrEqual, res.Index);
                
                NoContentStatus();
            }
        }

        private async Task WaitForExecutionOnRelevantNodes(JsonOperationContext context, string database, ClusterTopology clusterTopology, List<string> members, long index)
        {
            await ServerStore.Cluster.WaitForIndexNotification(index); // first let see if we commit this in the leader
            var executors = new List<ClusterRequestExecutor>();
            var timeoutTask = TimeoutManager.WaitFor(TimeSpan.FromMilliseconds(10000));
            var waitingTasks = new List<Task>
            {
                timeoutTask
            };
            var cts = CancellationTokenSource.CreateLinkedTokenSource(ServerStore.ServerShutdown);
            try
            {
                foreach (var member in members)
                {
                    var url = clusterTopology.GetUrlFromTag(member);
                    var requester = ClusterRequestExecutor.CreateForSingleNode(url, ServerStore.Server.Certificate.Certificate);
                    executors.Add(requester);
                    waitingTasks.Add(requester.ExecuteAsync(new WaitForRaftIndexCommand(index), context, cts.Token));
                }

                while (true)
                {
                    var task = await Task.WhenAny(waitingTasks);
                    if (task == timeoutTask)
                        throw new TimeoutException($"Waited too long for the raft command (number {index}) to be executed on any of the relevant nodes to this command.");
                    if (task.IsCompletedSuccessfully)
                    {
                        break;
                    }
                    waitingTasks.Remove(task);
                    if (waitingTasks.Count == 1) // only the timeout task is left
                        throw new InvalidDataException($"The database '{database}' was create but is not accessible, because all of the nodes on which this database was supose to be created, had thrown an exception.", task.Exception);
                }
            }
            finally
            {
                cts.Cancel();
                foreach (var clusterRequestExecutor in executors)
                {
                    clusterRequestExecutor.Dispose();
                }
                cts.Dispose();
            }
        }

        private async Task WaitForExecutionOnSpecificNode(TransactionOperationContext context, ClusterTopology clusterTopology, string node, long index)
        {
            await ServerStore.Cluster.WaitForIndexNotification(index); // first let see if we commit this in the leader

            using (var requester = ClusterRequestExecutor.CreateForSingleNode(clusterTopology.GetUrlFromTag(node), ServerStore.Server.Certificate.Certificate))
            {
                await requester.ExecuteAsync(new WaitForRaftIndexCommand(index), context);
            }
        }

        private DatabaseTopology AssignNodesToDatabase(
            TransactionOperationContext context,
            int factor,
            string name,
            bool isEncrypted,
            out List<string> nodeUrlsAddedTo)
        {
            var topology = new DatabaseTopology();

            var clusterTopology = ServerStore.GetClusterTopology(context);

            var allNodes = clusterTopology.Members.Keys
                .Concat(clusterTopology.Promotables.Keys)
                .Concat(clusterTopology.Watchers.Keys)
                .ToList();

            if (isEncrypted)
            {
                allNodes.RemoveAll(n => NotUsingHttps(clusterTopology.GetUrlFromTag(n)));
                if (allNodes.Count == 0)
                    throw new InvalidOperationException($"Database {name} is encrypted and requires a node which supports SSL. There is no such node available in the cluster.");
            }

            var offset = new Random().Next();
            nodeUrlsAddedTo = new List<string>();

            for (int i = 0; i < Math.Min(allNodes.Count, factor); i++)
            {
                var selectedNode = allNodes[(i + offset) % allNodes.Count];
                var url = clusterTopology.GetUrlFromTag(selectedNode);
                topology.Members.Add(selectedNode);
                nodeUrlsAddedTo.Add(url);
            }

            return topology;
        }

        private void ValidateClusterMembers(ClusterTopology clusterTopology, DatabaseRecord databaseRecord)
        {
            var topology = databaseRecord.Topology;

            if(topology == null)
                return;

            if (topology.Members?.Count == 1 && topology.Members[0] == "?")
            {
                // this is a special case where we pass '?' as member.
                topology.Members.Clear();
            }

            foreach (var node in topology.AllNodes)
            {
                var url = clusterTopology.GetUrlFromTag(node);
                if (databaseRecord.Encrypted && NotUsingHttps(url))
                    throw new InvalidOperationException($"{databaseRecord.DatabaseName} is encrypted but node {node} with url {url} doesn't use HTTPS. This is not allowed.");
            }
        }

        [RavenAction("/admin/expiration/config", "POST", AuthorizationStatus.DatabaseAdmin)]
        public async Task ConfigExpiration()
        {
            await DatabaseConfigurations(ServerStore.ModifyDatabaseExpiration, "read-expiration-config");
        }

        [RavenAction("/admin/revisions/config", "POST", AuthorizationStatus.DatabaseAdmin)]
        public async Task ConfigRevisions()
        {
            await DatabaseConfigurations(ServerStore.ModifyDatabaseRevisions, "read-revisions-config");
        }

        [RavenAction("/admin/periodic-backup", "POST", AuthorizationStatus.DatabaseAdmin)]
        public async Task UpdatePeriodicBackup()
        {
            await DatabaseConfigurations(ServerStore.ModifyPeriodicBackup,
                "update-periodic-backup",
                beforeSetupConfiguration: (_, readerObject) =>
                {
                    if (ServerStore.LicenseManager.CanAddPeriodicBackup(readerObject, out var licenseLimit) == false)
                    {
                        SetLicenseLimitResponse(licenseLimit);
                        return false;
                    }

                    VerifyPeriodicBackupConfiguration(readerObject);
                    return true;
                },
                fillJson: (json, readerObject, index) =>
                {
                    var taskIdName = nameof(PeriodicBackupConfiguration.TaskId);
                    readerObject.TryGet(taskIdName, out long taskId);
                    if (taskId == 0)
                        taskId = index;
                    json[taskIdName] = taskId;
                });
        }

        private static void VerifyPeriodicBackupConfiguration(BlittableJsonReaderObject readerObject)
        {
            readerObject.TryGet(
                nameof(PeriodicBackupConfiguration.FullBackupFrequency),
                out string fullBackupFrequency);
            readerObject.TryGet(
                nameof(PeriodicBackupConfiguration.IncrementalBackupFrequency),
                out string incrementalBackupFrequency);

            if (VerifyBackupFrequency(fullBackupFrequency) == null &&
                VerifyBackupFrequency(incrementalBackupFrequency) == null)
            {
                throw new ArgumentException("Couldn't parse the cron expressions for both full and incremental backups. " +
                                            $"full backup cron expression: {fullBackupFrequency}, " +
                                            $"incremental backup cron expression: {incrementalBackupFrequency}");
            }

            readerObject.TryGet(nameof(PeriodicBackupConfiguration.LocalSettings),
                out BlittableJsonReaderObject localSettings);

            if (localSettings == null)
                return;

            localSettings.TryGet(nameof(LocalSettings.Disabled), out bool disabled);
            if (disabled)
                return;

            localSettings.TryGet(nameof(LocalSettings.FolderPath), out string folderPath);
            if (string.IsNullOrWhiteSpace(folderPath))
                throw new ArgumentException("Backup directory cannot be null or empty");

            var originalFolderPath = folderPath;
            while (true)
            {
                var directoryInfo = new DirectoryInfo(folderPath);
                if (directoryInfo.Exists == false)
                {
                    if (directoryInfo.Parent == null)
                        throw new ArgumentException($"Path {originalFolderPath} cannot be accessed " +
                                                    $"because '{folderPath}' doesn't exist");
                    folderPath = directoryInfo.Parent.FullName;
                    continue;
                }

                if (directoryInfo.Attributes.HasFlag(FileAttributes.ReadOnly))
                    throw new ArgumentException($"Cannot write to directory path: {originalFolderPath}");

                break;
            }
        }

        private static CrontabSchedule VerifyBackupFrequency(string backupFrequency)
        {
            if (string.IsNullOrWhiteSpace(backupFrequency))
                return null;

            return CrontabSchedule.Parse(backupFrequency);
        }

        [RavenAction("/admin/periodic-backup/test-credentials", "POST", AuthorizationStatus.DatabaseAdmin)]
        public async Task TestPeriodicBackupCredentials()
        {
            // here we explictily don't care what db I'm an admin of, since it is just a test endpoint

            var type = GetQueryStringValueAndAssertIfSingleAndNotEmpty("type");

            if (Enum.TryParse(type, out PeriodicBackupTestConnectionType connectionType) == false)
                throw new ArgumentException($"Unkown backup connection: {type}");

            DynamicJsonValue result;

            using (ServerStore.ContextPool.AllocateOperationContext(out TransactionOperationContext context))
            {
                try
                {
                    var connectionInfo = await context.ReadForMemoryAsync(RequestBodyStream(), "test-connection");
                    switch (connectionType)
                    {
                        case PeriodicBackupTestConnectionType.S3:
                            var s3Settings = JsonDeserializationClient.S3Settings(connectionInfo);
                            using (var awsClient = new RavenAwsS3Client(
                                s3Settings.AwsAccessKey, s3Settings.AwsSecretKey, s3Settings.BucketName,
                                s3Settings.AwsRegionName, cancellationToken: ServerStore.ServerShutdown))
                            {
                                await awsClient.TestConnection();
                            }
                            break;
                        case PeriodicBackupTestConnectionType.Glacier:
                            var glacierSettings = JsonDeserializationClient.GlacierSettings(connectionInfo);
                            using (var galcierClient = new RavenAwsGlacierClient(
                                glacierSettings.AwsAccessKey, glacierSettings.AwsSecretKey,
                                glacierSettings.AwsRegionName, glacierSettings.VaultName,
                                cancellationToken: ServerStore.ServerShutdown))
                            {
                                await galcierClient.TestConnection();
                            }
                            break;
                        case PeriodicBackupTestConnectionType.Azure:
                            var azureSettings = JsonDeserializationClient.AzureSettings(connectionInfo);
                            using (var azureClient = new RavenAzureClient(
                                azureSettings.AccountName, azureSettings.AccountKey,
                                azureSettings.StorageContainer, cancellationToken: ServerStore.ServerShutdown))
                            {
                                await azureClient.TestConnection();
                            }
                            break;
                        case PeriodicBackupTestConnectionType.FTP:
                            var ftpSettings = JsonDeserializationClient.FtpSettings(connectionInfo);
                            using (var ftpClient = new RavenFtpClient(ftpSettings.Url, ftpSettings.Port, ftpSettings.UserName,
                                ftpSettings.Password, ftpSettings.CertificateAsBase64, ftpSettings.CertificateFileName))
                            {
                                await ftpClient.TestConnection();
                            }
                            break;
                        case PeriodicBackupTestConnectionType.Local:
                        case PeriodicBackupTestConnectionType.None:
                        default:
                            throw new ArgumentOutOfRangeException();
                    }

                    result = new DynamicJsonValue
                    {
                        [nameof(NodeConnectionTestResult.Success)] = true,
                    };
                }
                catch (Exception e)
                {
                    result = new DynamicJsonValue
                    {
                        [nameof(NodeConnectionTestResult.Success)] = false,
                        [nameof(NodeConnectionTestResult.Error)] = e.ToString()
                    };
                }

                using (var writer = new BlittableJsonTextWriter(context, ResponseBodyStream()))
                {
                    context.Write(writer, result);
                }
            }
        }

        [RavenAction("/admin/restore/points", "POST", AuthorizationStatus.Operator)]
        public async Task GetRestorePoints()
        {
            using (ServerStore.ContextPool.AllocateOperationContext(out TransactionOperationContext context))
            {
                var restorePathBlittable = await context.ReadForMemoryAsync(RequestBodyStream(), "database-restore-path");
                var restorePathJson = JsonDeserializationServer.DatabaseRestorePath(restorePathBlittable);

                var restorePoints = new RestorePoints();

                try
                {
                    Directory.GetLastAccessTime(restorePathJson.Path);
                }
                catch (UnauthorizedAccessException)
                {
                    throw new InvalidOperationException($"Unauthorized access to path: {restorePathJson.Path}");
                }

                if (Directory.Exists(restorePathJson.Path) == false)
                    throw new InvalidOperationException($"Path '{restorePathJson.Path}' doesn't exist");

                var directories = Directory.GetDirectories(restorePathJson.Path).OrderBy(x => x).ToList();
                if (directories.Count == 0)
                {
                    // no folders in directory
                    // will scan the directory for backup files
                    Restore.FetchRestorePoints(restorePathJson.Path, restorePoints.List, assertLegacyBackups: true);
                }
                else
                {
                    foreach (var directory in directories)
                    {
                        Restore.FetchRestorePoints(directory, restorePoints.List);
                    }
                }

                if (restorePoints.List.Count == 0)
                    throw new InvalidOperationException("Couldn't locate any backup files!");

                using (var writer = new BlittableJsonTextWriter(context, ResponseBodyStream()))
                {
                    var blittable = EntityToBlittable.ConvertEntityToBlittable(restorePoints, DocumentConventions.Default, context);
                    context.Write(writer, blittable);
                    writer.Flush();
                }
            }
        }

        [RavenAction("/admin/backup/database", "POST", AuthorizationStatus.DatabaseAdmin)]
        public async Task BackupDatabase()
        {
            using (ServerStore.ContextPool.AllocateOperationContext(out TransactionOperationContext context))
            {
                DynamicJsonValue result;

                try
                {
                    var taskId = GetLongQueryString("taskId");
                    var databaseName = GetQueryStringValueAndAssertIfSingleAndNotEmpty("name");
                    var isFullBackup = GetBoolValueQueryString("isFullBackup", required: false);

                    var database = await ServerStore.DatabasesLandlord.TryGetOrCreateResourceStore(databaseName);
                    database.PeriodicBackupRunner.StartBackupTask(taskId, isFullBackup ?? true);

                    result = new DynamicJsonValue
                    {
                        [nameof(CommandResult.Success)] = true,
                    };
                }
                catch (Exception e)
                {
                    result = new DynamicJsonValue
                    {
                        [nameof(CommandResult.Success)] = false,
                        [nameof(CommandResult.Error)] = e.ToString()
                    };
                }

                using (var writer = new BlittableJsonTextWriter(context, ResponseBodyStream()))
                {
                    context.Write(writer, result);
                }
            }
        }

        [RavenAction("/admin/restore/database", "POST", AuthorizationStatus.Operator)]
        public async Task RestoreDatabase()
        {
            using (ServerStore.ContextPool.AllocateOperationContext(out TransactionOperationContext context))
            {
                var restoreConfiguration = await context.ReadForMemoryAsync(RequestBodyStream(), "database-restore");
                var restoreConfigurationJson = JsonDeserializationCluster.RestoreBackupConfiguration(restoreConfiguration);

                var databaseName = restoreConfigurationJson.DatabaseName;
                if (string.IsNullOrWhiteSpace(databaseName))
                    throw new ArgumentException("Database name can't be null or empty");

                if (ResourceNameValidator.IsValidResourceName(databaseName, ServerStore.Configuration.Core.DataDirectory.FullPath, out string errorMessage) == false)
                    throw new BadRequestException(errorMessage);

                using (context.OpenReadTransaction())
                {
                    if (ServerStore.Cluster.ReadDatabase(context, databaseName) != null)
                        throw new ArgumentException($"Cannot restore data to an existing database named {databaseName}");

                    var clusterTopology = ServerStore.GetClusterTopology(context);

                    if (string.IsNullOrWhiteSpace(restoreConfigurationJson.EncryptionKey) == false)
                    {
                        var key = Convert.FromBase64String(restoreConfigurationJson.EncryptionKey);
                        if (key.Length != 256 / 8)
                            throw new InvalidOperationException($"The size of the key must be 256 bits, but was {key.Length * 8} bits.");

                        var isEncrypted = string.IsNullOrWhiteSpace(restoreConfigurationJson.EncryptionKey) == false;
                        if (isEncrypted && NotUsingHttps(clusterTopology.GetUrlFromTag(ServerStore.NodeTag)))
                            throw new InvalidOperationException("Cannot restore an encrypted database to a node which doesn't support SSL!");
                    }
                }

                var operationId = ServerStore.Operations.GetNextOperationId();
                var cancelToken = new OperationCancelToken(ServerStore.ServerShutdown);
                var restoreBackupTask = new RestoreBackupTask(
                    ServerStore,
                    restoreConfigurationJson,
                    ServerStore.NodeTag,
                    cancelToken);

#pragma warning disable 4014
                ServerStore.Operations.AddOperation(
#pragma warning restore 4014
                    null,
                    $"Database restore: {databaseName}",
                    Documents.Operations.Operations.OperationType.DatabaseRestore,
                    taskFactory: onProgress => Task.Run(async () => await restoreBackupTask.Execute(onProgress), cancelToken.Token),
                    id: operationId, token: cancelToken);

                using (var writer = new BlittableJsonTextWriter(context, ResponseBodyStream()))
                {
                    writer.WriteOperationId(context, operationId);
                }
            }
        }

        private async Task DatabaseConfigurations(Func<TransactionOperationContext, string,
            BlittableJsonReaderObject, Task<(long, object)>> setupConfigurationFunc,
            string debug,
            Func<string, BlittableJsonReaderObject, bool> beforeSetupConfiguration = null,
            Action<DynamicJsonValue, BlittableJsonReaderObject, long> fillJson = null)
        {
            var name = GetQueryStringValueAndAssertIfSingleAndNotEmpty("name");

            if (TryGetAllowedDbs(name, out var _, requireAdmin: true) == false)
                return;

            if (ResourceNameValidator.IsValidResourceName(name, ServerStore.Configuration.Core.DataDirectory.FullPath, out string errorMessage) == false)
                throw new BadRequestException(errorMessage);

            ServerStore.EnsureNotPassive();
            using (ServerStore.ContextPool.AllocateOperationContext(out TransactionOperationContext context))
            {
                var configurationJson = await context.ReadForMemoryAsync(RequestBodyStream(), debug);
                if (beforeSetupConfiguration?.Invoke(name, configurationJson) == false)
                    return;

                var (index, _) = await setupConfigurationFunc(context, name, configurationJson);
                DatabaseRecord dbRecord;
                using (context.OpenReadTransaction())
                {
                    //TODO: maybe have a timeout here for long loading operations
                    dbRecord = ServerStore.Cluster.ReadDatabase(context, name);
                }
                if (dbRecord.Topology.RelevantFor(ServerStore.NodeTag))
                {
                    var db = await ServerStore.DatabasesLandlord.TryGetOrCreateResourceStore(name);
                    await db.RachisLogIndexNotifications.WaitForIndexNotification(index);
                }
                else
                {
                    await ServerStore.Cluster.WaitForIndexNotification(index);
                }
                HttpContext.Response.StatusCode = (int)HttpStatusCode.OK;

                using (var writer = new BlittableJsonTextWriter(context, ResponseBodyStream()))
                {
                    var json = new DynamicJsonValue
                    {
                        ["RaftCommandIndex"] = index
                    };
                    fillJson?.Invoke(json, configurationJson, index);
                    context.Write(writer, json);
                    writer.Flush();
                }
            }
        }

        [RavenAction("/admin/databases", "DELETE", AuthorizationStatus.Operator)]
        public async Task Delete()
        {
            ServerStore.EnsureNotPassive();

            var waitOnRecordDeletion = new List<string>();
            using (ServerStore.ContextPool.AllocateOperationContext(out TransactionOperationContext context))
            {
                var json = await context.ReadForMemoryAsync(RequestBodyStream(), "docs");
                var parameters = JsonDeserializationServer.Parameters.DeleteDatabasesParameters(json);

                if (parameters.FromNodes != null && parameters.FromNodes.Length > 0)
                {
                    using (context.OpenReadTransaction())
                    {
                        foreach (var databaseName in parameters.DatabaseNames)
                        {
                            var record = ServerStore.Cluster.ReadDatabase(context, databaseName);
                            if (record == null)
                                continue;

                            foreach (var node in parameters.FromNodes)
                            {
                                if (record.Topology.RelevantFor(node) == false)
                                {
                                    throw new InvalidOperationException($"Database '{databaseName}' doesn't reside on node '{node}' so it can't be deleted from it");
                                }
                                record.Topology.RemoveFromTopology(node);
                            }

                            if (record.Topology.Count == 0)
                                waitOnRecordDeletion.Add(databaseName);
                        }
                    }
                }

                long index = -1;
                foreach (var name in parameters.DatabaseNames)
                {
                    var (newIndex, _) = await ServerStore.DeleteDatabaseAsync(name, parameters.HardDelete, parameters.FromNodes);
                    index = newIndex;
                }
                await ServerStore.Cluster.WaitForIndexNotification(index);

                var timeToWaitForConfirmation = parameters.TimeToWaitForConfirmation ?? TimeSpan.FromSeconds(15);

                var sp = Stopwatch.StartNew();
                int databaseIndex = 0;
                while (waitOnRecordDeletion.Count > databaseIndex)
                {
                    var databaseName = waitOnRecordDeletion[databaseIndex];
                    using (context.OpenReadTransaction())
                    {
                        var record = ServerStore.Cluster.ReadDatabase(context, databaseName);
                        if (record == null)
                        {
                            waitOnRecordDeletion.RemoveAt(databaseIndex);
                            continue;
                        }
                    }
                    // we'll now wait for the _next_ operation in the cluster
                    // since deletion involve multiple operations in the cluster
                    // we'll now wait for the next command to be applied and check
                    // whatever that removed the db in question
                    index++;
                    var remaining = timeToWaitForConfirmation - sp.Elapsed;
                    try
                    {
                        if (remaining < TimeSpan.Zero)
                        {
                            databaseIndex++;
                            continue; // we are done waiting, but still want to locally check the rest of the dbs
                        }

                        await ServerStore.Cluster.WaitForIndexNotification(index, remaining);
                    }
                    catch (TimeoutException)
                    {
                        databaseIndex++;
                    }
                }

                using (var writer = new BlittableJsonTextWriter(context, ResponseBodyStream()))
                {
                    context.Write(writer, new DynamicJsonValue
                    {
                        [nameof(DeleteDatabaseResult.RaftCommandIndex)] = index,
                        [nameof(DeleteDatabaseResult.PendingDeletes)] = new DynamicJsonArray(waitOnRecordDeletion)
                    });
                }
            }
        }

        [RavenAction("/admin/databases/disable", "POST", AuthorizationStatus.Operator)]
        public async Task DisableDatabases()
        {
            await ToggleDisableDatabases(disable: true);
        }

        [RavenAction("/admin/databases/enable", "POST", AuthorizationStatus.Operator)]
        public async Task EnableDatabases()
        {
            await ToggleDisableDatabases(disable: false);
        }

        [RavenAction("/admin/databases/dynamic-node-distribution", "POST", AuthorizationStatus.Operator)]
        public async Task ToggleDynamicNodeDistribution()
        {
            var name = GetQueryStringValueAndAssertIfSingleAndNotEmpty("name");
            var enable = GetBoolValueQueryString("enable") ?? true;

            using (ServerStore.ContextPool.AllocateOperationContext(out TransactionOperationContext context))
            {
                DatabaseRecord databaseRecord;
                long index;
                using (context.OpenReadTransaction())
                    databaseRecord = ServerStore.Cluster.ReadDatabase(context, name, out index);

                if (enable == databaseRecord.Topology.DynamicNodesDistribution)
                    return;

                if (enable &&
                    Server.ServerStore.LicenseManager.CanDynamicallyDistributeNodes(out var licenseLimit) == false)
                {
                    SetLicenseLimitResponse(licenseLimit);
                    return;
                }

                databaseRecord.Topology.DynamicNodesDistribution = enable;

                var (commandResultIndex, _) = await ServerStore.WriteDatabaseRecordAsync(name, databaseRecord, index);
                await ServerStore.Cluster.WaitForIndexNotification(commandResultIndex);

                NoContentStatus();
            }
        }

        private async Task ToggleDisableDatabases(bool disable)
        {
            using (ServerStore.ContextPool.AllocateOperationContext(out TransactionOperationContext context))
            {
                var json = await context.ReadForMemoryAsync(RequestBodyStream(), "databases/toggle");
                var parameters = JsonDeserializationServer.Parameters.DisableDatabaseToggleParameters(json);

                using (var writer = new BlittableJsonTextWriter(context, ResponseBodyStream()))
                {
                    writer.WriteStartObject();
                    writer.WritePropertyName("Status");

                    writer.WriteStartArray();
                    var first = true;
                    foreach (var name in parameters.DatabaseNames)
                    {
                        if (first == false)
                            writer.WriteComma();
                        first = false;

                        DatabaseRecord databaseRecord;
                        using (context.OpenReadTransaction())
                            databaseRecord = ServerStore.Cluster.ReadDatabase(context, name);

                        if (databaseRecord == null)
                        {
                            context.Write(writer, new DynamicJsonValue
                            {
                                ["Name"] = name,
                                ["Success"] = false,
                                ["Reason"] = "database not found"
                            });
                            continue;
                        }

                        if (databaseRecord.Disabled == disable)
                        {
                            var state = disable ? "disabled" : "enabled";
                            context.Write(writer, new DynamicJsonValue
                            {
                                ["Name"] = name,
                                ["Success"] = true, //even if we have nothing to do, no reason to return failure status
                                ["Disabled"] = disable,
                                ["Reason"] = $"Database already {state}"
                            });
                            continue;
                        }

                        databaseRecord.Disabled = disable;

                        var (index, _) = await ServerStore.WriteDatabaseRecordAsync(name, databaseRecord, null);
                        await ServerStore.Cluster.WaitForIndexNotification(index);

                        context.Write(writer, new DynamicJsonValue
                        {
                            ["Name"] = name,
                            ["Success"] = true,
                            ["Disabled"] = disable,
                            ["Reason"] = $"Database state={databaseRecord.Disabled} was propagated on the cluster"
                        });
                    }

                    writer.WriteEndArray();

                    writer.WriteEndObject();
                }
            }
        }

        [RavenAction("/admin/databases/promote", "POST", AuthorizationStatus.Operator)]
        public async Task PromoteImmediately()
        {
            var name = GetStringQueryString("name");
            var nodeTag = GetStringQueryString("node");

            using (ServerStore.ContextPool.AllocateOperationContext(out TransactionOperationContext context))
            {
                var (index, _) = await ServerStore.PromoteDatabaseNode(name, nodeTag);
                await ServerStore.Cluster.WaitForIndexNotification(index);

                HttpContext.Response.StatusCode = (int)HttpStatusCode.OK;

                using (var writer = new BlittableJsonTextWriter(context, ResponseBodyStream()))
                {
                    context.Write(writer, new DynamicJsonValue
                    {
                        [nameof(DatabasePutResult.Name)] = name,
                        [nameof(DatabasePutResult.RaftCommandIndex)] = index
                    });
                    writer.Flush();
                }
            }
        }

        [RavenAction("/admin/etl", "PUT", AuthorizationStatus.Operator)]
        public async Task AddEtl()
        {
            var id = GetLongQueryString("id", required: false);

            if (id == null)
            {
                await DatabaseConfigurations((_, databaseName, etlConfiguration) => ServerStore.AddEtl(_, databaseName, etlConfiguration), "etl-add",
                    beforeSetupConfiguration: CanAddOrUpdateEtl, fillJson: (json, _, index) => json[nameof(EtlConfiguration<ConnectionString>.TaskId)] = index);

                return;
            }

            string etlConfigurationName = null;
            string dbName = null;
            
            await DatabaseConfigurations((_, databaseName, etlConfiguration) =>
                {
                    var task = ServerStore.UpdateEtl(_, databaseName, id.Value, etlConfiguration);
                    dbName = databaseName;
                    etlConfiguration.TryGet(nameof(RavenEtlConfiguration.Name), out etlConfigurationName);
                    return task;
                    
                }, "etl-update", fillJson: (json, _, index) => json[nameof(EtlConfiguration<ConnectionString>.TaskId)] = index);
            
           
            // Reset scripts if needed
            var scriptsToReset = HttpContext.Request.Query["reset"];
            using(ServerStore.ContextPool.AllocateOperationContext(out TransactionOperationContext ctx))
            using(ctx.OpenReadTransaction())
            {
                foreach (var script in scriptsToReset)
                {
                    await ServerStore.ResetEtl(ctx, dbName, etlConfigurationName, script);     
                }    
            }
        }

        [RavenAction("/admin/etl", "RESET", AuthorizationStatus.Operator)]
        public async Task ResetEtl()
        {
            var configurationName = GetStringQueryString("configuration-name"); // etl task name
            var transformationName = GetStringQueryString("transformation-name");

            await DatabaseConfigurations((_, databaseName, etlConfiguration) => ServerStore.ResetEtl(_, databaseName, configurationName, transformationName), "etl-reset");
        }

        private bool CanAddOrUpdateEtl(string databaseName, BlittableJsonReaderObject etlConfiguration)
        {
            LicenseLimit licenseLimit;
            switch (EtlConfiguration<ConnectionString>.GetEtlType(etlConfiguration))
            {
                case EtlType.Raven:

                    if (ServerStore.LicenseManager.CanAddRavenEtl(out licenseLimit) == false)
                    {
                        SetLicenseLimitResponse(licenseLimit);
                        return false;
                    }

                    break;
                case EtlType.Sql:
                    if (ServerStore.LicenseManager.CanAddSqlEtl(out licenseLimit) == false)
                    {
                        SetLicenseLimitResponse(licenseLimit);
                        return false;
                    }

                    break;
                default:
                    throw new NotSupportedException($"Unknown ETL configuration type. Configuration: {etlConfiguration}");
            }

            return true;
        }

        [RavenAction("/admin/console", "POST", AuthorizationStatus.ClusterAdmin)]
        public async Task AdminConsole()
        {
            var name = GetStringQueryString("database", false);
            var isServerScript = GetBoolValueQueryString("server-script", false) ?? false;
            var feature = HttpContext.Features.Get<IHttpAuthenticationFeature>() as RavenServer.AuthenticateConnection;
            var clientCert = feature?.Certificate?.FriendlyName;

            using (ServerStore.ContextPool.AllocateOperationContext(out TransactionOperationContext context))
            {
                var content = await context.ReadForMemoryAsync(RequestBodyStream(), "read-admin-script");
                if (content.TryGet(nameof(AdminJsScript.Script), out string _) == false)
                {
                    throw new InvalidDataException("Field " + nameof(AdminJsScript.Script) + " was not found.");
                }

                var adminJsScript = JsonDeserializationCluster.AdminJsScript(content);
                string result;

                if (isServerScript)
                {
                    var console = new AdminJsConsole(Server, null);
                    if (console.Log.IsOperationsEnabled)
                    {
                        console.Log.Operations($"The certificate that was used to initiate the operation: {clientCert ?? "None"}");
                    }

                    result = console.ApplyScript(adminJsScript);
                }
                else if (string.IsNullOrWhiteSpace(name) == false)
                {
                    //database script
                    var database = await ServerStore.DatabasesLandlord.TryGetOrCreateResourceStore(name);
                    if (database == null)
                    {
                        DatabaseDoesNotExistException.Throw(name);
                    }

                    var console = new AdminJsConsole(Server, database);
                    if (console.Log.IsOperationsEnabled)
                    {
                        console.Log.Operations($"The certificate that was used to initiate the operation: {clientCert ?? "None"}");
                    }
                    result = console.ApplyScript(adminJsScript);
                }

                else
                {
                    throw new InvalidOperationException("'database' query string parmater not found, and 'server-script' query string is not found. Don't know what to apply this script on");
                }

                HttpContext.Response.StatusCode = (int)HttpStatusCode.OK;
                using (var textWriter = new StreamWriter(ResponseBodyStream()))
                {
                    textWriter.Write(result);
                    await textWriter.FlushAsync();
                }
            }
        }

        [RavenAction("/admin/connection-strings", "PUT", AuthorizationStatus.DatabaseAdmin)]
        public async Task PutConnectionString()
        {
            await DatabaseConfigurations((_, databaseName, connectionString) => ServerStore.PutConnectionString(_, databaseName, connectionString), "put-connection-string");
        }

        [RavenAction("/admin/connection-strings", "DELETE", AuthorizationStatus.DatabaseAdmin)]
        public async Task RemoveConnectionString()
        {
            var dbName = GetQueryStringValueAndAssertIfSingleAndNotEmpty("name");

            if (TryGetAllowedDbs(dbName, out var _, requireAdmin: true) == false)
                return;

            if (ResourceNameValidator.IsValidResourceName(dbName, ServerStore.Configuration.Core.DataDirectory.FullPath, out string errorMessage) == false)
                throw new BadRequestException(errorMessage);

            var connectionStringName = GetQueryStringValueAndAssertIfSingleAndNotEmpty("connectionString");
            var type = GetQueryStringValueAndAssertIfSingleAndNotEmpty("type");

            ServerStore.EnsureNotPassive();

            var (index, _) = await ServerStore.RemoveConnectionString(dbName, connectionStringName, type);
            await ServerStore.Cluster.WaitForIndexNotification(index);
            HttpContext.Response.StatusCode = (int)HttpStatusCode.OK;

            using (ServerStore.ContextPool.AllocateOperationContext(out TransactionOperationContext context))
            {
                using (var writer = new BlittableJsonTextWriter(context, ResponseBodyStream()))
                {
                    context.Write(writer, new DynamicJsonValue
                    {
                        ["RaftCommandIndex"] = index
                    });
                    writer.Flush();
                }
            }
        }

        [RavenAction("/admin/connection-strings", "GET", AuthorizationStatus.DatabaseAdmin)]
        public Task GetConnectionStrings()
        {
            var dbName = GetQueryStringValueAndAssertIfSingleAndNotEmpty("name");
            if (ResourceNameValidator.IsValidResourceName(dbName, ServerStore.Configuration.Core.DataDirectory.FullPath, out string errorMessage) == false)
                throw new BadRequestException(errorMessage);

            if (TryGetAllowedDbs(dbName, out var allowedDbs, true) == false)
                return Task.CompletedTask;

            var connectionStringName = GetStringQueryString("connectionStringName", false);
            var type = GetStringQueryString("type", false);

            ServerStore.EnsureNotPassive();
            HttpContext.Response.StatusCode = (int)HttpStatusCode.OK;

            using (ServerStore.ContextPool.AllocateOperationContext(out TransactionOperationContext context))
            {
                DatabaseRecord record;
                using (context.OpenReadTransaction())
                {
                    record = ServerStore.Cluster.ReadDatabase(context, dbName);
                }

                Dictionary<string, RavenConnectionString> ravenConnectionStrings;
                Dictionary<string, SqlConnectionString> sqlConnectionstrings;
                if (connectionStringName != null)
                {
                    if(string.IsNullOrWhiteSpace(connectionStringName))
                        throw new ArgumentException($"connectionStringName {connectionStringName}' must have a non empty value");


                    if (Enum.TryParse<ConnectionStringType>(type, true, out var connectionStringType) == false)
                        throw new NotSupportedException($"Unknown connection string type: {connectionStringType}");

                    (ravenConnectionStrings, sqlConnectionstrings) = GetConnectionString(record, connectionStringName, connectionStringType);
                }
                else
                {
                    ravenConnectionStrings = record.RavenConnectionStrings;
                    sqlConnectionstrings = record.SqlConnectionStrings;
                }

                using (var writer = new BlittableJsonTextWriter(context, ResponseBodyStream()))
                {
                    var result = new GetConnectionStringsResult
                    {
                        RavenConnectionStrings = ravenConnectionStrings,
                        SqlConnectionStrings = sqlConnectionstrings
                    };
                    context.Write(writer, result.ToJson());
                    writer.Flush();
                }
            }

            return Task.CompletedTask;
        }

        private static (Dictionary<string, RavenConnectionString>, Dictionary<string, SqlConnectionString>)
            GetConnectionString(DatabaseRecord record ,string connectionStringName, ConnectionStringType connectionStringType)
        {
            var ravenConnectionStrings = new Dictionary<string, RavenConnectionString>();
            var sqlConnectionStrings = new Dictionary<string, SqlConnectionString>();

            switch (connectionStringType)
            {
                case ConnectionStringType.Raven:
                    if (record.RavenConnectionStrings.TryGetValue(connectionStringName, out var ravenConnectionString))
                    {
                        ravenConnectionStrings.TryAdd(connectionStringName, new RavenConnectionString
                        {
                            Name = ravenConnectionString.Name,
                            TopologyDiscoveryUrls = ravenConnectionString.TopologyDiscoveryUrls,
                            Database = ravenConnectionString.Database
                        });
                    }

                    break;

                case ConnectionStringType.Sql:
                    if (record.SqlConnectionStrings.TryGetValue(connectionStringName, out var sqlConnectionString))
                    {
                        sqlConnectionStrings.TryAdd(connectionStringName, new SqlConnectionString
                        {
                            Name = sqlConnectionString.Name,
                            ConnectionString = sqlConnectionString.ConnectionString
                        });
                    }

                    break;

                default:
                    throw new NotSupportedException($"Unknown connection string type: {connectionStringType}");
            }

            return (ravenConnectionStrings, sqlConnectionStrings);
        }

        [RavenAction("/admin/replication/conflicts/solver", "POST", AuthorizationStatus.DatabaseAdmin)]
        public async Task UpdateConflictSolver()
        {
            var name = GetQueryStringValueAndAssertIfSingleAndNotEmpty("name");

            if (TryGetAllowedDbs(name, out var _, requireAdmin: true) == false)
                return;

            if (ResourceNameValidator.IsValidResourceName(name, ServerStore.Configuration.Core.DataDirectory.FullPath, out string errorMessage) == false)
                throw new BadRequestException(errorMessage);

            ServerStore.EnsureNotPassive();
            using (ServerStore.ContextPool.AllocateOperationContext(out TransactionOperationContext context))
            {
                var json = await context.ReadForMemoryAsync(RequestBodyStream(), "read-conflict-resolver");
                var conflictResolver = (ConflictSolver)EntityToBlittable.ConvertToEntity(typeof(ConflictSolver), "convert-conflict-resolver", json, DocumentConventions.Default);

                using (context.OpenReadTransaction())
                {
                    var databaseRecord = ServerStore.Cluster.ReadDatabase(context, name, out _);

                    var (index, _) = await ServerStore.ModifyConflictSolverAsync(name, conflictResolver);
                    await ServerStore.Cluster.WaitForIndexNotification(index);

                    HttpContext.Response.StatusCode = (int)HttpStatusCode.Created;

                    using (var writer = new BlittableJsonTextWriter(context, ResponseBodyStream()))
                    {
                        context.Write(writer, new DynamicJsonValue
                        {
                            ["RaftCommandIndex"] = index,
                            ["Key"] = name,
                            [nameof(DatabaseRecord.ConflictSolverConfig)] = databaseRecord.ConflictSolverConfig.ToJson()
                        });
                        writer.Flush();
                    }
                }
            }
        }

        [RavenAction("/admin/compact", "POST", AuthorizationStatus.Operator)]
        public Task CompactDatabase()
        {
            using (ServerStore.ContextPool.AllocateOperationContext(out TransactionOperationContext context))
            {
                var compactSettingsJson = context.ReadForDisk(RequestBodyStream(), string.Empty);

                var compactSettings = JsonDeserializationServer.CompactSettings(compactSettingsJson);
                
                if (string.IsNullOrEmpty(compactSettings.DatabaseName))
                    throw new InvalidOperationException($"{nameof(compactSettings.DatabaseName)} is a required field when compacting a database.");

                if (compactSettings.Documents == false && compactSettings.Indexes.Length == 0)
                    throw new InvalidOperationException($"{nameof(compactSettings.Documents)} is false in compact settings and no indexes were supplied. Nothing to compact.");

                using (context.OpenReadTransaction())
                {
                    var record = ServerStore.Cluster.ReadDatabase(context, compactSettings.DatabaseName);
                    if (record == null)
                        throw new InvalidOperationException($"Cannot compact database {compactSettings.DatabaseName}, it doesn't exist.");
                    if (record.Topology.RelevantFor(ServerStore.NodeTag) == false)
                        throw new InvalidOperationException($"Cannot compact database {compactSettings.DatabaseName} on node {ServerStore.NodeTag}, because it doesn't reside on this node.");
                }

                var database = ServerStore.DatabasesLandlord.TryGetOrCreateResourceStore(compactSettings.DatabaseName).Result;
               
                var token = new OperationCancelToken(ServerStore.ServerShutdown);
                var compactDatabaseTask = new CompactDatabaseTask(
                    ServerStore,
                    compactSettings.DatabaseName,
                    token.Token);

                var operationId = ServerStore.Operations.GetNextOperationId();

                ServerStore.Operations.AddOperation(
                    null,
                    "Compacting database: " + compactSettings.DatabaseName,
                    Documents.Operations.Operations.OperationType.DatabaseCompact,
                    taskFactory: onProgress => Task.Run(async () =>
                    {
                        using (token)
                        {
                            var before = CalculateStorageSizeInBytes(compactSettings.DatabaseName).Result / 1024 / 1024;
                            var overallResult = new CompactionResult(compactSettings.DatabaseName);
                            
                            // first fill in data 
                            foreach (var indexName in compactSettings.Indexes)
                            {
                                var indexCompactionResult = new CompactionResult(indexName);
                                overallResult.IndexesResults.Add(indexName, indexCompactionResult);
                            }
                            
                            // then do actual compaction
                            foreach (var indexName in compactSettings.Indexes)
                            {
                                var index = database.IndexStore.GetIndex(indexName);
                                var indexCompactionResult = overallResult.IndexesResults[indexName];
                                index.Compact(onProgress, (CompactionResult) indexCompactionResult);
                                indexCompactionResult.Processed = true;
                            }

                            if (!compactSettings.Documents)
                            {
                                overallResult.Skipped = true;
                                overallResult.Processed = true;
                                return overallResult;   
                            }

                            await compactDatabaseTask.Execute(onProgress, overallResult);
                            overallResult.Processed = true;
                            
                            overallResult.SizeAfterCompactionInMb = CalculateStorageSizeInBytes(compactSettings.DatabaseName).Result / 1024 / 1024;
                            overallResult.SizeBeforeCompactionInMb = before;

                            return (IOperationResult)overallResult;
                        }
                    }, token.Token),
                    id: operationId, token: token);

                using (var writer = new BlittableJsonTextWriter(context, ResponseBodyStream()))
                {
                    writer.WriteOperationId(context, operationId);
                }
            }
            return Task.CompletedTask;
        }

        public async Task<long> CalculateStorageSizeInBytes(string databaseName)
        {
            long sizeOnDiskInBytes = 0;

            var database = await ServerStore.DatabasesLandlord.TryGetOrCreateResourceStore(databaseName);
            var storageEnvironments = database?.GetAllStoragesEnvironment();
            if (storageEnvironments != null)
            {
                foreach (var environment in storageEnvironments)
                {
                    Transaction tx = null;
                    try
                    {
                        try
                        {
                            tx = environment?.Environment.ReadTransaction();
                        }
                        catch (OperationCanceledException)
                        {
                            continue;
                        }
                        var storageReport = environment?.Environment.GenerateReport(tx);
                        if (storageReport == null)
                            continue;

                        var journalSize = storageReport.Journals.Sum(j => j.AllocatedSpaceInBytes);
                        sizeOnDiskInBytes += storageReport.DataFile.AllocatedSpaceInBytes + journalSize;
                    }
                    finally
                    {
                        tx?.Dispose();
                    }
                }
            }
            return sizeOnDiskInBytes;
        }

        [RavenAction("/admin/migrate", "POST", AuthorizationStatus.ClusterAdmin)]
        public async Task MigrateDatabases()
        {
            using (ServerStore.ContextPool.AllocateOperationContext(out TransactionOperationContext context))
            {
                var migrationConfiguration = await context.ReadForMemoryAsync(RequestBodyStream(), "migration-configuration");
                var migrationConfigurationJson = JsonDeserializationServer.DatabasesMigrationConfiguration(migrationConfiguration);

                if (string.IsNullOrWhiteSpace(migrationConfigurationJson.ServerUrl))
                    throw new ArgumentException("Url cannot be null or empty");

                var migrator = new Migrator(migrationConfigurationJson, ServerStore, ServerStore.ServerShutdown);
                await migrator.MigrateDatabases(migrationConfigurationJson.DatabasesNames);

                NoContentStatus();
            }
        }
        

        
        [RavenAction("/admin/migrate/offline", "POST", AuthorizationStatus.ClusterAdmin)]
        public async Task MigrateDatabaseOffline()
        {
            OfflineMigrationConfiguration configuration;

            ServerStore.EnsureNotPassive();

            using (ServerStore.ContextPool.AllocateOperationContext(out TransactionOperationContext context))
            {
                var migrationConfiguration = await context.ReadForMemoryAsync(RequestBodyStream(), "migration-configuration");
                configuration = JsonDeserializationServer.OfflineMigrationConfiguration(migrationConfiguration);
            }

            var dataDir = configuration.DataDirectory;
            if (Directory.Exists(dataDir) == false)
                throw new DirectoryNotFoundException($"Could not find directory {dataDir}");

            var dataExporter = configuration.DataExporterFullPath;
            if (File.Exists(configuration.DataExporterFullPath) == false)
                throw new FileNotFoundException($"Could not find file {dataExporter}");

            var databaseName = configuration.DatabaseRecord.DatabaseName;
            if (ResourceNameValidator.IsValidResourceName(databaseName, ServerStore.Configuration.Core.DataDirectory.FullPath, out string errorMessage) == false)
                throw new BadRequestException(errorMessage);

            using (ServerStore.ContextPool.AllocateOperationContext(out TransactionOperationContext context))
            {
                context.OpenReadTransaction();
                await CreateDatabase(databaseName, configuration.DatabaseRecord, context, 1, null);
            }
            
            var database = await ServerStore.DatabasesLandlord.TryGetOrCreateResourceStore(databaseName, true);
            if (database == null)
            {
                throw new DatabaseDoesNotExistException($"Can't import into database {databaseName} because it doesn't exist.");
            }
            var (commandline, tmpFile) = configuration.GenerateExporterCommandLine();
            var processStartInfo = new ProcessStartInfo(dataExporter, commandline);
            var token = new OperationCancelToken(database.DatabaseShutdown);
            Task timeout = null;
            if (configuration.Timeout.HasValue)
            {
                timeout = Task.Delay((int)configuration.Timeout.Value.TotalMilliseconds, token.Token);
            }
            processStartInfo.RedirectStandardOutput = true;
            processStartInfo.RedirectStandardInput = true;
            var process = new Process
            {
                StartInfo = processStartInfo,
                EnableRaisingEvents = true,
            };            
            process.Start();
            var result = new OfflineMigrationResult();
            var overallProgress = result.Progress as SmugglerResult.SmugglerProgress;
            var operationId = ServerStore.Operations.GetNextOperationId();
            var processDone = new AsyncManualResetEvent(token.Token);

            // send new line to avoid issue with read key 
            process.StandardInput.WriteLine();
            process.EnableRaisingEvents = true;
            process.Exited += (sender, e) => { processDone.Set(); };
           
           // don't await here - this operation is async - all we return is operation id 
            var t = ServerStore.Operations.AddOperation(null, $"Migration of {dataDir} to {databaseName}",
                Documents.Operations.Operations.OperationType.MigrationFromLegacyData,
                onProgress =>
                {
                    return Task.Run(async () =>
                    {
                        try
                        {
                            while (true)
                            {
                                var (hasTimeout, readMessage) = await ReadLineOrTimeout(process, timeout, configuration,token.Token);
                                if (readMessage == null)
                                {
                                    // reached end of stream
                                    break;
                                }
                                if(token.Token.IsCancellationRequested)
                                    throw new TaskCanceledException("Was requested to cancel the offline migration task");
                                if (hasTimeout)
                                {
                                    //renewing the timeout so not to spam timeouts once the timeout is reached
                                    timeout = Task.Delay(configuration.Timeout.Value, token.Token);
                                }

                                result.AddInfo(readMessage);
                                onProgress(overallProgress);
                            }

                            var ended = await processDone.WaitAsync(configuration.Timeout ?? TimeSpan.MaxValue);
                            if (ended == false)
                            {
                                if (token.Token.IsCancellationRequested)
                                    throw new TaskCanceledException("Was requested to cancel the offline migration process midway");
                                token.Cancel(); //To release the MRE
                                throw new TimeoutException($"After waiting for {configuration.Timeout.HasValue} the export tool didn't exit, aborting.");
                            }

                            if (process.ExitCode != 0)
                            {
                                throw new ApplicationException($"The data export tool have exited with code {process.ExitCode}.");
                            }

                            result.DataExporter.Processed = true;

                            if (File.Exists(configuration.OutputFilePath) == false)
                            {
                                throw new FileNotFoundException($"Was expecting the output file to be located at {configuration.OutputFilePath}, but it is not there.");
                            }

                            result.AddInfo("Starting the import phase of the migration");
                            onProgress(overallProgress);
                            using (database.DocumentsStorage.ContextPool.AllocateOperationContext(out DocumentsOperationContext context))
                            using (var reader = File.OpenRead(configuration.OutputFilePath))
                            using (var stream = new GZipStream(reader, CompressionMode.Decompress))
                            using (var source = new StreamSource(stream, context, database))
                            {
                                var destination = new DatabaseDestination(database);
                                var smuggler = new DatabaseSmuggler(database, source, destination, database.Time, result: result, onProgress: onProgress,
                                    token: token.Token);

                                smuggler.Execute();
                            }
                        }
                        catch (Exception e)
                        {
                            result.AddError(e.ToString());
                            throw;
                        }
                        finally
                        {
                            if (string.IsNullOrEmpty(tmpFile) == false)
                            {
                                IOExtensions.DeleteFile(tmpFile);
                            }
                        }
                        return (IOperationResult)result;
                    });
                }, operationId, token);

            using (ServerStore.ContextPool.AllocateOperationContext(out TransactionOperationContext context))
            using (var writer = new BlittableJsonTextWriter(context, ResponseBodyStream()))
            {
                writer.WriteOperationId(context, operationId);
            }
        }

        private static async Task<(bool HasTimeout, string Line)> ReadLineOrTimeout(Process process, Task timeout, OfflineMigrationConfiguration configuration, CancellationToken token)
        {
            var readline = process.StandardOutput.ReadLineAsync();
            string progressLine = null;
            if (timeout != null)
            {
                var finishedTask = await Task.WhenAny(readline, timeout);
                if (finishedTask == timeout)
                {
                    return (true, $"Export is taking more than the configured timeout {configuration.Timeout.Value}");
                }
            }
            else
            {
              progressLine = await readline.WithCancellation(token);
            } 
            return (false, progressLine);
        }
    }
}
