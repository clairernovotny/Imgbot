using System;
using System.IO;
using System.Threading.Tasks;
using Common;
using Common.Messages;
using Install;
using Microsoft.Azure.WebJobs;
using Microsoft.Extensions.Logging;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Table;

namespace CompressImagesFunction
{
    public static class CompressImagesFunction
    {
        [FunctionName("CompressImagesFunction")]
        public static async Task Trigger(
            [QueueTrigger("compressimagesmessage")]CompressImagesMessage compressImagesMessage,
            [Queue("longrunningcompressmessage")] ICollector<CompressImagesMessage> longRunningCompressMessages,
            [Queue("openprmessage")] ICollector<OpenPrMessage> openPrMessages,
            ILogger logger,
            ExecutionContext context)
        {
            logger.LogInformation($"Starting compress");

            var storageAccount = CloudStorageAccount.Parse(KnownEnvironmentVariables.AzureWebJobsStorage);
            var settingsTable = storageAccount.CreateCloudTableClient().GetTableReference("settings");

            var installationTokenProvider = new InstallationTokenProvider();
            var repoChecks = new RepoChecks();
            var task = RunAsync(installationTokenProvider, compressImagesMessage, openPrMessages, settingsTable, repoChecks, logger, context);
            if (await Task.WhenAny(task, Task.Delay(570000)) == task)
            {
                await task;
            }
            else
            {
                logger.LogInformation($"Time out exceeded!");
                longRunningCompressMessages.Add(compressImagesMessage);
            }
        }

        [FunctionName("LongCompressImagesFunction")]
        public static async Task LongTrigger(
            [QueueTrigger("longrunningcompressmessage")]CompressImagesMessage compressImagesMessage,
            [Queue("openprmessage")] ICollector<OpenPrMessage> openPrMessages,
            ILogger logger,
            ExecutionContext context)
        {
            logger.LogInformation($"Starting long compress");

            var storageAccount = CloudStorageAccount.Parse(KnownEnvironmentVariables.AzureWebJobsStorage);
            var settingsTable = storageAccount.CreateCloudTableClient().GetTableReference("settings");

            var installationTokenProvider = new InstallationTokenProvider();
            var repoChecks = new RepoChecks();
            var task = RunAsync(installationTokenProvider, compressImagesMessage, openPrMessages, settingsTable, repoChecks, logger, context);
            await task;
        }

        public static async Task RunAsync(
            IInstallationTokenProvider installationTokenProvider,
            CompressImagesMessage compressImagesMessage,
            ICollector<OpenPrMessage> openPrMessages,
            CloudTable settingsTable,
            IRepoChecks repoChecks,
            ILogger logger,
            ExecutionContext context)
        {
            logger.LogInformation("CompressImagesFunction: starting run for {Owner}/{RepoName}", compressImagesMessage.Owner, compressImagesMessage.RepoName);
            var installationTokenParameters = new InstallationTokenParameters
            {
                AccessTokensUrl = string.Format(KnownGitHubs.AccessTokensUrlFormat, compressImagesMessage.InstallationId),
                AppId = KnownGitHubs.AppId,
            };

            var installationToken = await installationTokenProvider.GenerateAsync(
                installationTokenParameters,
                KnownEnvironmentVariables.APP_PRIVATE_KEY);

            // check if repo is archived before starting work
            var isArchived = await repoChecks.IsArchived(new GitHubClientParameters
            {
                Password = installationToken.Token,
                RepoName = compressImagesMessage.RepoName,
                RepoOwner = compressImagesMessage.Owner
            });

            if (isArchived)
            {
                logger.LogInformation("CompressImagesFunction: skipping archived repo {Owner}/{RepoName}", compressImagesMessage.Owner, compressImagesMessage.RepoName);
                return;
            }

            // check if imgbot branch already exists before starting work
            var branchExists = await repoChecks.BranchExists(new GitHubClientParameters
            {
                Password = installationToken.Token,
                RepoName = compressImagesMessage.RepoName,
                RepoOwner = compressImagesMessage.Owner,
            });

            if (branchExists)
            {
                logger.LogInformation("CompressImagesFunction: skipping repo {Owner}/{RepoName} as branch exists", compressImagesMessage.Owner, compressImagesMessage.RepoName);
                return;
            }

            var compressImagesParameters = new CompressimagesParameters
            {
                CloneUrl = compressImagesMessage.CloneUrl,
                LocalPath = LocalPath.CloneDir(KnownEnvironmentVariables.TMP ?? "/private/tmp/", compressImagesMessage.RepoName),
                Password = installationToken.Token,
                RepoName = compressImagesMessage.RepoName,
                RepoOwner = compressImagesMessage.Owner,
                PgpPrivateKey = KnownEnvironmentVariables.PGP_PRIVATE_KEY,
                PgPPassword = KnownEnvironmentVariables.PGP_PASSWORD,
                CompressImagesMessage = compressImagesMessage,
                Settings = await Common.TableModels.SettingsHelper.GetSettings(settingsTable, compressImagesMessage.InstallationId, compressImagesMessage.RepoName),
            };

            var didCompress = CompressImages.Run(compressImagesParameters, logger);

            if (didCompress)
            {
                logger.LogInformation("CompressImagesFunction: Successfully compressed images for {Owner}/{RepoName}", compressImagesMessage.Owner, compressImagesMessage.RepoName);
                openPrMessages.Add(new OpenPrMessage
                {
                    InstallationId = compressImagesMessage.InstallationId,
                    RepoName = compressImagesMessage.RepoName,
                    CloneUrl = compressImagesMessage.CloneUrl,
                });
            }

            try
            {
                Directory.Delete(compressImagesParameters.LocalPath, recursive: true);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Error cleaning up local directory");
            }

            logger.LogInformation("CompressImagesFunction: finished run for {Owner}/{RepoName}", compressImagesMessage.Owner, compressImagesMessage.RepoName);
        }
    }
}
