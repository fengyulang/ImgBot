using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Common.Messages;
using Common.TableModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.WindowsAzure.Storage.Table;
using NSubstitute;
using WebHook.Model;

namespace Test
{
    [TestClass]
    public class WebHookTests
    {
        void HandleAction()
        {
        }


        [TestMethod]
        public async Task GivenCommitToOtherBranch_ShouldReturnOkDoNothing()
        {
            var result = await ExecuteHookAsync(
                githubEvent: "push",
                payload: "data/hooks/commit-otherbranch.json",
                out var routerMessages,
                out var installationsTable,
                out var marketplaceTable);

            // Assert OKObjectResult and Value
            var response = (HookResponse)((OkObjectResult)result).Value;
            Assert.AreEqual("Commit to non default branch", response.Result);

            // No messages sent to Router
            routerMessages.DidNotReceive().Add(Arg.Any<RouterMessage>());

            // No calls to InstallationTable
            await installationsTable.DidNotReceive().ExecuteAsync(Arg.Any<TableOperation>());

            // No calls to MarketplaceTable
            await marketplaceTable.DidNotReceive().ExecuteAsync(Arg.Any<TableOperation>());
        }

        [TestMethod]
        public async Task GivenCommitToDefaultBranchNoImages_ShouldReturnOkDoNothing()
        {
            var result = await ExecuteHookAsync(
                githubEvent: "push",
                payload: "data/hooks/commit-defaultbranch-noimages.json",
                out var routerMessages,
                out var installationsTable,
                out var marketplaceTable);

            // Assert OKObjectResult and Value
            var response = (HookResponse)((OkObjectResult)result).Value;
            Assert.AreEqual("No image files touched", response.Result);

            // No messages sent to Router
            routerMessages.DidNotReceive().Add(Arg.Any<RouterMessage>());

            // No calls to InstallationTable
            await installationsTable.DidNotReceive().ExecuteAsync(Arg.Any<TableOperation>());

            // No calls to MarketplaceTable
            await marketplaceTable.DidNotReceive().ExecuteAsync(Arg.Any<TableOperation>());
        }

        [TestMethod]
        public async Task GivenCommitToDefaultBranchWithImages_ShouldReturnOkQueueToRouter()
        {
            var result = await ExecuteHookAsync(
                githubEvent: "push",
                payload: "data/hooks/commit-defaultbranch-images.json",
                out var routerMessages,
                out var installationsTable,
                out var marketplaceTable);

            // Assert OKObjectResult and Value
            var response = (HookResponse)((OkObjectResult)result).Value;
            Assert.AreEqual("true", response.Result);

            // 1 message sent to Router
            routerMessages.Received(1).Add(Arg.Is<RouterMessage>(x =>
                x.InstallationId == 23199 &&
                x.Owner == "dabutvin" &&
                x.AccessTokensUrl == "https://api.github.com/installations/23199/access_tokens" &&
                x.RepoName == "test" &&
                x.CloneUrl == "https://github.com/dabutvin/test"));

            // No calls to InstallationTable
            await installationsTable.DidNotReceive().ExecuteAsync(Arg.Any<TableOperation>());

            // No calls to MarketplaceTable
            await marketplaceTable.DidNotReceive().ExecuteAsync(Arg.Any<TableOperation>());
        }

        [TestMethod]
        public async Task GivenMarketplacePurchase_ShouldReturnOkWriteRow()
        {
            var result = await ExecuteHookAsync(
                githubEvent: "marketplace_purchase",
                payload: "data/hooks/marketplacepurchase.json",
                out var routerMessages,
                out var installationsTable,
                out var marketplaceTable);

            // Assert OKObjectResult and Value
            var response = (HookResponse)((OkObjectResult)result).Value;
            Assert.AreEqual("purchased", response.Result);

            // No messages sent to Router
            routerMessages.DidNotReceive().Add(Arg.Any<RouterMessage>());

            // No calls to InstallationTable
            await installationsTable.DidNotReceive().ExecuteAsync(Arg.Any<TableOperation>());

            // 1 call to MarketplaceTable to insert
            await marketplaceTable.Received(1).ExecuteAsync(
                Arg.Is<TableOperation>(x => x.OperationType == TableOperationType.InsertOrMerge));
        }

        [TestMethod]
        public async Task GivenMarketplaceCancellation_ShouldReturnOkDeleteRow()
        {
            void extraSetup(ICollector<RouterMessage> extraRouterMessages,
                CloudTable extraInstallationsTable,
                CloudTable extraMarketplaceTable) =>
            extraMarketplaceTable
                .ExecuteAsync(Arg.Is<TableOperation>(x => x.OperationType == TableOperationType.Retrieve))
                .Returns(Task.FromResult(new TableResult
                {
                    Result = new Marketplace(),
                    Etag = "*",
                }));

            var result = await ExecuteHookAsync(
                githubEvent: "marketplace_purchase",
                payload: "data/hooks/marketplacecancellation.json",
                out var routerMessages,
                out var installationsTable,
                out var marketplaceTable,
                extraSetup);

            // Assert OKObjectResult and Value
            var response = (HookResponse)((OkObjectResult)result).Value;
            Assert.AreEqual("cancelled", response.Result);

            // No messages sent to Router
            routerMessages.DidNotReceive().Add(Arg.Any<RouterMessage>());

            // No calls to InstallationTable
            await installationsTable.DidNotReceive().ExecuteAsync(Arg.Any<TableOperation>());

            // 1 call to MarketplaceTable to delete
            await marketplaceTable.Received(1).ExecuteAsync(
                Arg.Is<TableOperation>(x => x.OperationType == TableOperationType.Delete));
        }

        private Task<IActionResult> ExecuteHookAsync(
            string githubEvent,
            string payload,
            out ICollector<RouterMessage> routerMessages,
            out CloudTable installationsTable,
            out CloudTable marketplaceTable,
            Action<ICollector<RouterMessage>, CloudTable, CloudTable> extraSetup = null)
        {
            var request = Substitute.For<HttpRequestMessage>();
            routerMessages = Substitute.For<ICollector<RouterMessage>>();
            installationsTable = Substitute.For<CloudTable>(new Uri("https://myaccount.table.core.windows.net/Tables/installation"));
            marketplaceTable = Substitute.For<CloudTable>(new Uri("https://myaccount.table.core.windows.net/Tables/marketplace"));
            var logger = Substitute.For<TraceWriter>(TraceLevel.Error);

            request.Headers.Add("X-GitHub-Event", new[] { githubEvent });
            request.Content = new StringContent(File.ReadAllText(payload));

            extraSetup?.Invoke(routerMessages, installationsTable, marketplaceTable);

            return WebHook.WebHookFunction.Run(
                request, routerMessages, installationsTable, marketplaceTable, logger);
        }
    }
}
