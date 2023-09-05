using System.Diagnostics;
using Angor.Shared.Models;
//using Angor.Shared.Protocol;
using Angor.Shared.ProtocolNew.Scripts;
using Blockcore.NBitcoin.DataEncoders;
using NBitcoin;
using IndexedTxOut = NBitcoin.IndexedTxOut;
using Key = NBitcoin.Key;
using Op = NBitcoin.Op;
using OutPoint = NBitcoin.OutPoint;
using Script = Blockcore.Consensus.ScriptInfo.Script;
using Transaction = Blockcore.Consensus.TransactionInfo.Transaction;
using uint256 = Blockcore.NBitcoin.uint256;
using Utils = NBitcoin.Utils;
using WitScript = NBitcoin.WitScript;

namespace Angor.Shared.ProtocolNew;

public class FounderTransactionActions : IFounderTransactionActions
{
    private readonly INetworkConfiguration _networkConfiguration;
    private readonly IProjectScriptsBuilder _projectScriptsBuilder;
    private readonly IInvestmentScriptBuilder _investmentScriptBuilder;
    private readonly ITaprootScriptBuilder _taprootScriptBuilder;
    
    public FounderTransactionActions(INetworkConfiguration networkConfiguration, IProjectScriptsBuilder projectScriptsBuilder, IInvestmentScriptBuilder investmentScriptBuilder, ITaprootScriptBuilder taprootScriptBuilder)
    {
        _networkConfiguration = networkConfiguration;
        _projectScriptsBuilder = projectScriptsBuilder;
        _investmentScriptBuilder = investmentScriptBuilder;
        _taprootScriptBuilder = taprootScriptBuilder;
    }

    public List<string> SignInvestorRecoveryTransactions(ProjectInfo projectInfo, string investmentTrxHex, 
        Transaction recoveryTransaction, string founderPrivateKey)
    {
        var network = _networkConfiguration.GetNetwork();
        
        var nbitcoinNetwork = NetworkMapper.Map(network);
        var investmentTransaction = NBitcoin.Transaction.Parse(investmentTrxHex, nbitcoinNetwork);
        
        var key = new Key(Encoders.Hex.DecodeData(founderPrivateKey));

        var (investorKey, secretHash) = GetProjectDetailsFromOpReturn(investmentTransaction);
        
        var outputs = investmentTransaction.Outputs.AsIndexedOutputs()
            .Where(_ => _.TxOut.ScriptPubKey.IsScriptType(ScriptType.Taproot))
            .Select(_ => _.TxOut)
            .ToArray();

        const TaprootSigHash sigHash = TaprootSigHash.Single | TaprootSigHash.AnyoneCanPay;

        var nBitcoinTransaction = NBitcoin.Transaction.Parse(recoveryTransaction.ToHex(), nbitcoinNetwork); //We need to convert to NBitcoin transactions 

        return Enumerable.Range(0,recoveryTransaction.Outputs.Count)
            .Select(index =>
            {
                var scriptStages =  _investmentScriptBuilder.BuildProjectScriptsForStage(projectInfo, investorKey, 
                    index, secretHash);
                
                var hash = nBitcoinTransaction.GetSignatureHashTaproot(outputs ,
                    new TaprootExecutionData(index,
                            new NBitcoin.Script(scriptStages.Recover.ToBytes()).TaprootV1LeafHash)
                        { SigHash = sigHash });

                return key.SignTaprootKeySpend(hash, sigHash).ToString();
            }).ToList();
    }

    /// <summary>
    /// Allow the founder to spend the coins in a stage after the timelock has passed
    /// </summary>
    /// <exception cref="Exception"></exception>
    public Transaction SpendFounderStage(ProjectInfo projectInfo, IEnumerable<string> investmentTransactionsHex, int stageNumber, Script founderRecieveAddress, string founderPrivateKey,
        FeeEstimation fee)
    {
        var network = _networkConfiguration.GetNetwork();
        
        // We'll use the NBitcoin lib because its a taproot spend
        var nbitcoinNetwork = NetworkMapper.Map(network);

        var spendingTransaction = nbitcoinNetwork.CreateTransaction();
        
        // Step 1 - the time lock
        // we must set the locktime to be ahead of the current block time
        // and ahead of the cltv otherwise the trx wont get accepted in the chain
        spendingTransaction.LockTime = Utils.DateTimeToUnixTime(projectInfo.Stages[stageNumber - 1].ReleaseDate.AddMinutes(1));

        // Step 2 - build the transaction outputs and inputs without signing using fake sigs for fee estimation
        spendingTransaction.Outputs.Add(NBitcoin.Money.Zero, new NBitcoin.Script(founderRecieveAddress.ToBytes()));

        var stageOutputs = investmentTransactionsHex
            .Select(trxHex => NBitcoin.Transaction.Parse(trxHex, nbitcoinNetwork))
            .Select(trx => AddInputToSpendingTransaction(projectInfo, stageNumber, trx, spendingTransaction))
            .ToList();

        spendingTransaction.Outputs[0].Value -= nbitcoinNetwork
            .CreateTransactionBuilder()
            .AddCoins(stageOutputs.Select(_ => _.ToCoin()))
            .EstimateFees(spendingTransaction, new NBitcoin.FeeRate(NBitcoin.Money.Satoshis(fee.FeeRate)));

        // Step 4 - sign the taproot inputs
        var trxData = spendingTransaction.PrecomputeTransactionData(stageOutputs.Select(_ => _.TxOut).ToArray());
        const TaprootSigHash sigHash = TaprootSigHash.All;
        var key = new Key(Encoders.Hex.DecodeData(founderPrivateKey));
        
        var inputIndex = 0;
        foreach (var input in spendingTransaction.Inputs)
        {
            var scriptToExecute = new NBitcoin.Script(input.WitScript[1]);
            var controlBlock = input.WitScript[2];
            
            var execData = new TaprootExecutionData(inputIndex, scriptToExecute.TaprootV1LeafHash) { SigHash = sigHash };
            var hash = spendingTransaction.GetSignatureHashTaproot(trxData, execData);
            
            var sig = key.SignTaprootKeySpend(hash, sigHash);

            Debug.Assert(key.CreateTaprootKeyPair().PubKey.VerifySignature(hash, sig.SchnorrSignature));
            
            input.WitScript = new WitScript(
                Op.GetPushOp(sig.ToBytes()),
                Op.GetPushOp(scriptToExecute.ToBytes()),
                Op.GetPushOp(controlBlock));

            inputIndex++;
        }

        return network.CreateTransaction(spendingTransaction.ToHex());
    }

    public Transaction CreateNewProjectTransaction(string founderKey, Script angorKey, long angorFeeSatoshis)
    {
        var projectStartTransaction = _networkConfiguration.GetNetwork()
            .Consensus.ConsensusFactory.CreateTransaction();
        
        // create the output and script of the project id
        var investorInfoOutput = new Blockcore.Consensus.TransactionInfo.TxOut(
            new Blockcore.NBitcoin.Money(angorFeeSatoshis), angorKey);
        
        projectStartTransaction.AddOutput(investorInfoOutput);

        // todo: here we should add the hash of the project data as opreturn

        // create the output and script of the investor pubkey script opreturn
        var angorFeeOutputScript = _projectScriptsBuilder.BuildFounderInfoScript(founderKey);
        var founderOPReturnOutput = new Blockcore.Consensus.TransactionInfo.TxOut(
            new Blockcore.NBitcoin.Money(0), angorFeeOutputScript);
        projectStartTransaction.AddOutput(founderOPReturnOutput);

        return projectStartTransaction;
    }

    private IndexedTxOut AddInputToSpendingTransaction(ProjectInfo projectInfo, int stageNumber, NBitcoin.Transaction trx,
         NBitcoin.Transaction spendingTransaction)
     {
         var stageOutput = trx.Outputs.AsIndexedOutputs().ElementAt(stageNumber + 1);

         spendingTransaction.Outputs[0].Value += stageOutput.TxOut.Value;

         var input = spendingTransaction.Inputs.Add(new OutPoint(stageOutput.Transaction, stageOutput.N), 
             null, null, new NBitcoin.Sequence(spendingTransaction.LockTime.Value));

         var (investorKey, secretHash) = GetProjectDetailsFromOpReturn(trx);

         var scriptStages =  _investmentScriptBuilder.BuildProjectScriptsForStage(projectInfo, investorKey, 
             stageNumber - 1, secretHash);

         var controlBlock = _taprootScriptBuilder.CreateControlBlock(scriptStages, _ => _.Founder);
         
         // use fake data for fee estimation
         var sigPlaceHolder = new byte[64];

         input.WitScript = new WitScript(Op.GetPushOp(sigPlaceHolder), Op.GetPushOp(scriptStages.Founder.ToBytes()),
             Op.GetPushOp(controlBlock.ToBytes()));
         
         return stageOutput;
     }

     private (string investorKey, uint256? secretHash) GetProjectDetailsFromOpReturn(NBitcoin.Transaction investmentTransaction)
     {
         var opretunOutput = investmentTransaction.Outputs.AsIndexedOutputs().ElementAt(1);

         return
             _projectScriptsBuilder.GetInvestmentDataFromOpReturnScript(
                 new Script(opretunOutput.TxOut.ScriptPubKey.ToBytes()));
     }
}