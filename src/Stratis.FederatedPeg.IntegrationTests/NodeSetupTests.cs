using System.Threading;
using System.Threading.Tasks;
using NBitcoin;
using Stratis.Bitcoin.IntegrationTests.Common;
using Stratis.FederatedPeg.IntegrationTests.Utils;
using Xunit;

namespace Stratis.FederatedPeg.IntegrationTests
{
    public class NodeSetupTests
    {
        [Fact]
        public void StartBothChainsWithWallets()
        {
            using (var context = new SidechainTestContext())
            {
                context.StartAndConnectNodes();

                context.EnableSideFedWallets();
                context.EnableMainFedWallets();
            }
        }

        [Fact]
        public void FundMainChain()
        {
            using (var context = new SidechainTestContext())
            {
                context.StartMainNodes();
                context.ConnectMainChainNodes();
                context.EnableMainFedWallets();

                TestHelper.MineBlocks(context.MainUser, (int)context.MainChainNetwork.Consensus.CoinbaseMaturity + (int)context.MainChainNetwork.Consensus.PremineHeight);
                TestHelper.WaitForNodeToSync(context.MainUser, context.FedMain1, context.FedMain2, context.FedMain3);
                Assert.True(context.GetBalance(context.MainUser) > context.MainChainNetwork.Consensus.PremineReward);
            }
        }

        [Fact]
        public void FundSideChain()
        {
            using (var context = new SidechainTestContext())
            {
                context.StartSideNodes();
                context.ConnectSideChainNodes();
                context.EnableSideFedWallets();

                // Wait for node to reach premine height
                TestHelper.WaitLoop(() => context.SideUser.FullNode.Chain.Height >= context.SideChainNetwork.Consensus.PremineHeight);
                TestHelper.WaitForNodeToSync(context.SideUser, context.FedSide1, context.FedSide2, context.FedSide3);

                // Ensure that coinbase contains premine reward and it goes to the fed.
                Block block = context.SideUser.FullNode.Chain.GetBlock((int)context.SideChainNetwork.Consensus.PremineHeight).Block;
                Transaction coinbase = block.Transactions[0];
                Assert.Single(coinbase.Outputs);
                Assert.Equal(context.SideChainNetwork.Consensus.PremineReward, coinbase.Outputs[0].Value);
                Assert.Equal(context.scriptAndAddresses.payToMultiSig.PaymentScript, coinbase.Outputs[0].ScriptPubKey);
            }
        }

        [Fact]
        public async Task MainChain_To_SideChain_Transfer_And_Back()
        {
            using (var context = new SidechainTestContext())
            {
                // Set everything up
                context.StartAndConnectNodes();
                context.EnableSideFedWallets();
                context.EnableMainFedWallets();

                // Fund a main chain node
                TestHelper.MineBlocks(context.MainUser, (int)context.MainChainNetwork.Consensus.CoinbaseMaturity + (int)context.MainChainNetwork.Consensus.PremineHeight);
                TestHelper.WaitForNodeToSync(context.MainUser, context.FedMain1);
                Assert.True(context.GetBalance(context.MainUser) > context.MainChainNetwork.Consensus.PremineReward);

                // Let sidechain progress to point where fed has the premine
                TestHelper.WaitLoop(() => context.SideUser.FullNode.Chain.Height >= context.SideUser.FullNode.Network.Consensus.PremineHeight);
                TestHelper.WaitForNodeToSync(context.SideUser, context.FedSide1);
                Block block = context.SideUser.FullNode.Chain.GetBlock((int)context.SideChainNetwork.Consensus.PremineHeight).Block;
                Transaction coinbase = block.Transactions[0];
                Assert.Single(coinbase.Outputs);
                Assert.Equal(context.SideChainNetwork.Consensus.PremineReward, coinbase.Outputs[0].Value);
                Assert.Equal(context.scriptAndAddresses.payToMultiSig.PaymentScript, coinbase.Outputs[0].ScriptPubKey);

                // Send to sidechain
                decimal transferValueCoins = 25;
                var transferValue = new Money(transferValueCoins, MoneyUnit.BTC);
                string sidechainAddress = context.GetAddress(context.SideUser);
                await context.DepositToSideChain(context.MainUser, transferValueCoins, sidechainAddress);
                TestHelper.WaitLoop(() => context.FedMain1.CreateRPCClient().GetRawMempool().Length == 1);
                TestHelper.MineBlocks(context.FedMain1, 15);

                var source = new CancellationTokenSource(15_000);
                TestHelper.WaitLoop(() => context.GetBalance(context.SideUser) == transferValue, cancellationToken: source.Token);

                // Sidechain user has balance - transfer complete.
                Assert.Equal(transferValue, context.GetBalance(context.SideUser));

                await Task.Delay(5_000).ConfigureAwait(false);

                // Send funds back to the main chain
                string mainchainAddress = context.GetAddress(context.MainUser);
                Money currentMainUserBalance = context.GetBalance(context.MainUser);

                await context.WithdrawToMainChain(context.SideUser, 24, mainchainAddress);
                int currentSideHeight = context.SideUser.FullNode.Chain.Tip.Height;
                // Mine just enough to get past min deposit and allow time for fed to work
                TestHelper.WaitLoop(() => context.SideUser.FullNode.Chain.Height >= currentSideHeight + 7);

                // Should unlock funds back on the main chain
                TestHelper.WaitLoop(() => context.FedMain1.CreateRPCClient().GetRawMempool().Length == 1);
                TestHelper.MineBlocks(context.FedMain1, 1);
                Assert.Equal(currentMainUserBalance + new Money(24, MoneyUnit.BTC), context.GetBalance(context.MainUser));
            }
        }
    }
}
